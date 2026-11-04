package mcpbridge

import (
	"bytes"
	"encoding/json"
	"errors"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// JSON-RPC error codes. -32000 to -32099 are reserved for server errors; the
// failure kind is also in the error's data, for clients that act on it.
const (
	codeParseError     = -32700
	codeInvalidRequest = -32600
	codeInternal       = -32603
	codeUpstream       = -32000
	codeAuth           = -32001
	codeForbidden      = -32002
	codeNotFound       = -32003
	codeUnavailable    = -32004
	codeRateLimited    = -32005
	codeInsecure       = -32006
)

var (
	errNoResponse    = errors.New("upstream closed without answering the request")
	errFrameTooLarge = errors.New("upstream frame exceeds the size limit")
)

// message is one inbound JSON-RPC message. Unknown fields are relayed untouched
// because the raw bytes are what goes upstream.
type message struct {
	raw    []byte
	id     json.RawMessage
	method string
	params json.RawMessage
}

func (m *message) isRequest() bool { return m.id != nil && m.method != "" }

func (m *message) isInitialize() bool { return m.method == "initialize" }

func (m *message) isCancellation() bool { return m.method == "notifications/cancelled" }

// cancelledID returns the key of the request a cancellation names.
func (m *message) cancelledID() string {
	var p struct {
		RequestID json.RawMessage `json:"requestId"`
	}
	if json.Unmarshal(m.params, &p) != nil {
		return ""
	}
	return string(p.RequestID)
}

type rejection struct {
	id      json.RawMessage
	code    int
	message string
}

// parseMessage validates framing. It accepts requests, notifications and
// responses, the last because a client answers server-initiated requests.
func parseMessage(raw []byte) (*message, *rejection) {
	var envelope struct {
		JSONRPC string          `json:"jsonrpc"`
		ID      json.RawMessage `json:"id"`
		Method  string          `json:"method"`
		Params  json.RawMessage `json:"params"`
		Result  json.RawMessage `json:"result"`
		Error   json.RawMessage `json:"error"`
	}
	trimmed := bytes.TrimSpace(bytes.TrimPrefix(raw, []byte("\xef\xbb\xbf")))
	if err := json.Unmarshal(trimmed, &envelope); err != nil {
		return nil, parseFailure(trimmed)
	}
	id := normalizeID(envelope.ID)
	if envelope.JSONRPC != "2.0" {
		return nil, &rejection{id: id, code: codeInvalidRequest, message: "jsonrpc must be \"2.0\""}
	}
	if !validID(id) {
		return nil, &rejection{code: codeInvalidRequest, message: "id must be a string or a number"}
	}
	isResponse := id != nil && (envelope.Result != nil || envelope.Error != nil)
	if envelope.Method == "" && !isResponse {
		return nil, &rejection{id: id, code: codeInvalidRequest, message: "message has no method"}
	}
	return &message{raw: trimmed, id: id, method: envelope.Method, params: envelope.Params}, nil
}

func parseFailure(raw []byte) *rejection {
	if json.Valid(raw) {
		// Valid JSON of the wrong shape, including batches, which MCP removed.
		return &rejection{code: codeInvalidRequest, message: "message must be a single JSON-RPC object"}
	}
	return &rejection{code: codeParseError, message: "message is not valid JSON"}
}

// normalizeID treats an explicit null id as absent.
func normalizeID(id json.RawMessage) json.RawMessage {
	if bytes.Equal(bytes.TrimSpace(id), []byte("null")) {
		return nil
	}
	return id
}

func validID(id json.RawMessage) bool {
	if id == nil {
		return true
	}
	var value any
	if json.Unmarshal(id, &value) != nil {
		return false
	}
	switch value.(type) {
	case string, float64:
		return true
	}
	return false
}

type rpcError struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Error   rpcErrorBody    `json:"error"`
}

type rpcErrorBody struct {
	Code    int       `json:"code"`
	Message string    `json:"message"`
	Data    *failData `json:"data,omitempty"`
}

type failData struct {
	Kind              apperr.Kind `json:"kind"`
	Status            int         `json:"status,omitempty"`
	RetryAfterSeconds int64       `json:"retryAfterSeconds,omitempty"`
	TraceID           string      `json:"traceId,omitempty"`
}

// reject answers a message that could not be relayed. An unknown id is sent as
// null, as JSON-RPC requires.
func (b *Bridge) reject(id json.RawMessage, code int, text string) {
	b.writeError(id, rpcErrorBody{Code: code, Message: text})
}

// fail answers a request with err. A notification has nobody to answer, so its
// failure is only logged.
func (b *Bridge) fail(msg *message, err error) {
	if !msg.isRequest() {
		b.logf("upstream failure for %s: %s", termsafe.Line(msg.method), termsafe.Line(err.Error()))
		return
	}
	b.writeError(msg.id, errorBody(apperr.FromTransport(err)))
}

func (b *Bridge) writeError(id json.RawMessage, body rpcErrorBody) {
	if id == nil {
		id = json.RawMessage("null")
	}
	body.Message = b.opts.Redactor.Diagnostic(termsafe.Line(body.Message))
	if body.Data != nil {
		body.Data.TraceID = b.opts.Redactor.Diagnostic(termsafe.Line(body.Data.TraceID))
	}
	frame, err := json.Marshal(rpcError{JSONRPC: "2.0", ID: id, Error: body})
	if err == nil {
		err = b.out.write(frame)
	}
	if err != nil {
		b.logf("cannot write to stdout: %v", err)
	}
}

// errorBody maps a classified failure onto a JSON-RPC error.
func errorBody(err error) rpcErrorBody {
	var e *apperr.Error
	if !errors.As(err, &e) {
		return rpcErrorBody{Code: codeInternal, Message: err.Error()}
	}
	return rpcErrorBody{
		Code:    kindCode(e.Kind),
		Message: e.Message,
		Data: &failData{
			Kind:              e.Kind,
			Status:            e.Status,
			RetryAfterSeconds: int64(e.RetryAfter / time.Second),
			TraceID:           e.TraceID,
		},
	}
}

var kindCodes = map[apperr.Kind]int{
	apperr.KindAuth:        codeAuth,
	apperr.KindForbidden:   codeForbidden,
	apperr.KindNotFound:    codeNotFound,
	apperr.KindUnavailable: codeUnavailable,
	apperr.KindRateLimited: codeRateLimited,
	apperr.KindInsecure:    codeInsecure,
}

func kindCode(kind apperr.Kind) int {
	if code, ok := kindCodes[kind]; ok {
		return code
	}
	return codeUpstream
}
