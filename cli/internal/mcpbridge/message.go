package mcpbridge

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
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
	errNoResponse      = errors.New("upstream closed without answering the request")
	errFrameTooLarge   = errors.New("upstream frame exceeds the size limit")
	errInvalidFrame    = errors.New("upstream sent an invalid frame")
	errTooManyRequests = errors.New("too many requests are waiting")
	errNotObject       = errors.New("not a JSON object")
	errDuplicateMember = errors.New("duplicate member")
)

func compactJSON(dst *bytes.Buffer, src []byte) error {
	return json.Compact(dst, src)
}

// objectFields reads one JSON object by exact member name. encoding/json matches
// struct fields case-insensitively, so {"Id":1} would count as an id, and with
// {"id":2,"ID":1} the last one would win. A client's parser may disagree on
// either, so a repeated name is refused too.
func objectFields(raw []byte) (map[string]json.RawMessage, error) {
	dec := json.NewDecoder(bytes.NewReader(raw))
	tok, err := dec.Token()
	if err != nil {
		return nil, err
	}
	if tok != json.Delim('{') {
		return nil, errNotObject
	}
	fields := map[string]json.RawMessage{}
	for dec.More() {
		if err := readMember(dec, fields); err != nil {
			return nil, err
		}
	}
	if _, err := dec.Token(); err != nil {
		return nil, err
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return nil, errNotObject
	}
	return fields, nil
}

func readMember(dec *json.Decoder, fields map[string]json.RawMessage) error {
	tok, err := dec.Token()
	if err != nil {
		return err
	}
	name, _ := tok.(string)
	if _, repeated := fields[name]; repeated {
		return errDuplicateMember
	}
	var value json.RawMessage
	if err := dec.Decode(&value); err != nil {
		return err
	}
	fields[name] = value
	return nil
}

func stringField(fields map[string]json.RawMessage, name string) string {
	var value string
	if raw, ok := fields[name]; ok && json.Unmarshal(raw, &value) == nil {
		return value
	}
	return ""
}

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
	fields, err := objectFields(m.params)
	if err != nil {
		return ""
	}
	return string(fields["requestId"])
}

// stringID returns the request id if it is a JSON string.
func (m *message) stringID() string {
	var id string
	if json.Unmarshal(m.id, &id) == nil {
		return id
	}
	return ""
}

type rejection struct {
	id      json.RawMessage
	code    int
	message string
}

// parseMessage validates framing. It accepts requests, notifications and
// responses, the last because a client answers server-initiated requests.
func parseMessage(raw []byte) (*message, *rejection) {
	trimmed := bytes.TrimSpace(bytes.TrimPrefix(raw, []byte("\xef\xbb\xbf")))
	fields, err := objectFields(trimmed)
	if err != nil {
		return nil, parseFailure(trimmed)
	}
	id := normalizeID(fields["id"])
	if stringField(fields, "jsonrpc") != "2.0" {
		return nil, &rejection{id: validIDOrNil(id), code: codeInvalidRequest, message: "jsonrpc must be \"2.0\""}
	}
	if !validID(id) {
		return nil, &rejection{code: codeInvalidRequest, message: "id must be a string or a number"}
	}
	method := stringField(fields, "method")
	if method == "" && !isAnswer(fields, id) {
		return nil, &rejection{id: id, code: codeInvalidRequest, message: "message has no method"}
	}
	return &message{raw: trimmed, id: id, method: method, params: fields["params"]}, nil
}

// isAnswer reports exactly one of result or error, on a message with an id.
func isAnswer(fields map[string]json.RawMessage, id json.RawMessage) bool {
	_, result := fields["result"]
	_, failure := fields["error"]
	return id != nil && result != failure
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

func validIDOrNil(id json.RawMessage) json.RawMessage {
	if validID(id) {
		return id
	}
	return nil
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
		b.logf("upstream failure for %s: %s", msg.method, err.Error())
		return
	}
	b.writeError(msg.id, errorBody(apperr.FromTransport(err)))
}

func (b *Bridge) writeError(id json.RawMessage, body rpcErrorBody) {
	keepID := ""
	if id == nil {
		id = json.RawMessage("null")
	} else {
		keepID = (&message{id: id}).stringID()
	}
	body.Message = b.opts.Redactor.Diagnostic(termsafe.Line(body.Message))
	if body.Data != nil {
		body.Data.TraceID = b.opts.Redactor.Diagnostic(termsafe.Line(body.Data.TraceID))
	}
	frame, err := json.Marshal(rpcError{JSONRPC: "2.0", ID: id, Error: body})
	if err == nil {
		err = b.out.write(frame, keepID)
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
