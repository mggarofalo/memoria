package mcpbridge

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"
	"regexp"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

// headerValue limits session ids and protocol versions copied from a response
// into later requests to visible ASCII.
var headerValue = regexp.MustCompile(`^[\x21-\x7e]{1,256}$`)

// post sends one message and relays the response frames. It reports whether the
// reply to msg was among them.
func (b *Bridge) post(ctx context.Context, msg *message) (answered bool, err error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, b.opts.Endpoint, bytes.NewReader(msg.raw))
	if err != nil {
		return false, errors.New("cannot build the upstream request")
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	b.setSessionHeaders(req)

	resp, err := b.opts.Client.Do(req)
	if err != nil {
		return false, err
	}
	defer func() { _ = resp.Body.Close() }()

	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
		return false, apperr.FromResponse(resp.StatusCode, resp.Header, body)
	}
	b.rememberSession(resp.Header)
	if resp.StatusCode == http.StatusAccepted || resp.StatusCode == http.StatusNoContent {
		return false, nil
	}
	return b.relayBody(msg, resp)
}

func (b *Bridge) setSessionHeaders(req *http.Request) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.session != "" {
		req.Header.Set("Mcp-Session-Id", b.session)
	}
	if b.protocol != "" {
		req.Header.Set("Mcp-Protocol-Version", b.protocol)
	}
}

func (b *Bridge) rememberSession(header http.Header) {
	if id := header.Get("Mcp-Session-Id"); headerValue.MatchString(id) {
		b.mu.Lock()
		b.session = id
		b.mu.Unlock()
	}
}

func (b *Bridge) relayBody(msg *message, resp *http.Response) (bool, error) {
	media, _, err := mime.ParseMediaType(resp.Header.Get("Content-Type"))
	if err != nil {
		return false, errors.New("upstream sent no usable content type")
	}
	switch {
	case media == "text/event-stream":
		return b.relayStream(msg, resp.Body)
	case strings.HasSuffix(media, "json"):
		return b.relayJSON(msg, resp.Body)
	}
	return false, errors.New("upstream sent an unexpected content type")
}

func (b *Bridge) relayJSON(msg *message, body io.Reader) (bool, error) {
	data, err := io.ReadAll(io.LimitReader(body, int64(b.opts.MaxOutboundBytes)+1))
	if err != nil {
		return false, err
	}
	return b.relayFrame(msg, data)
}

// relayStream relays each SSE message event as one frame. It keeps reading past
// a frame that fails, so progress noise cannot hide the real answer, and
// reports the failure only if no answer ever comes.
func (b *Bridge) relayStream(msg *message, body io.Reader) (bool, error) {
	reader := bufio.NewReaderSize(body, 64*1024)
	answered := false
	var firstErr error
	note := func(ok bool, err error) {
		answered = answered || ok
		if err != nil && firstErr == nil {
			firstErr = err
		}
	}
	for {
		data, err := readEvent(reader, b.opts.MaxOutboundBytes)
		if len(data) > 0 {
			note(b.relayFrame(msg, data))
		}
		if err != nil {
			return streamResult(answered, firstErr, err)
		}
	}
}

func streamResult(answered bool, frameErr, readErr error) (bool, error) {
	if answered {
		return true, nil
	}
	if !errors.Is(readErr, io.EOF) {
		return false, readErr
	}
	return false, frameErr
}

// readEvent returns the data of the next SSE "message" event, joining multi-line
// data as the SSE format specifies. Events with another name are skipped.
func readEvent(r *bufio.Reader, max int) ([]byte, error) {
	var event sseEvent
	for {
		line, tooLong, err := readBoundedLine(r, max)
		if tooLong {
			return nil, errFrameTooLarge
		}
		text := strings.TrimRight(string(line), "\r\n")
		if text == "" {
			if event.isMessage() {
				return event.data, err
			}
			event = sseEvent{}
		}
		event.apply(text)
		if len(event.data) > max {
			return nil, errFrameTooLarge
		}
		if err != nil {
			return nil, err
		}
	}
}

type sseEvent struct {
	data []byte
	name string
}

func (e *sseEvent) isMessage() bool {
	return len(e.data) > 0 && (e.name == "" || e.name == "message")
}

func (e *sseEvent) apply(line string) {
	field, value, _ := strings.Cut(line, ":")
	value = strings.TrimPrefix(value, " ")
	switch field {
	case "data":
		if len(e.data) > 0 {
			e.data = append(e.data, '\n')
		}
		e.data = append(e.data, value...)
	case "event":
		e.name = value
	}
}

// relayFrame writes one upstream frame and reports whether it answered msg.
func (b *Bridge) relayFrame(msg *message, frame []byte) (bool, error) {
	if len(frame) > b.opts.MaxOutboundBytes {
		return false, errFrameTooLarge
	}
	var parsed struct {
		ID     json.RawMessage `json:"id"`
		Method string          `json:"method"`
		Result json.RawMessage `json:"result"`
		Error  json.RawMessage `json:"error"`
	}
	if err := json.Unmarshal(frame, &parsed); err != nil {
		return false, errInvalidFrame
	}
	answers := msg.isRequest() && parsed.Method == "" && bytes.Equal(bytes.TrimSpace(parsed.ID), msg.id)
	if answers && msg.isInitialize() {
		b.rememberProtocol(parsed.Result)
	}
	if err := b.out.write(frame); err != nil {
		return false, err
	}
	return answers, nil
}

func (b *Bridge) rememberProtocol(result json.RawMessage) {
	var init struct {
		ProtocolVersion string `json:"protocolVersion"`
	}
	if json.Unmarshal(result, &init) == nil && headerValue.MatchString(init.ProtocolVersion) {
		b.mu.Lock()
		b.protocol = init.ProtocolVersion
		b.mu.Unlock()
	}
}
