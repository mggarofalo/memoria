// Package mcpbridge exposes a remote Memoria MCP server to a client that only
// speaks stdio.
//
// It relays frames rather than using an MCP SDK. An SDK would parse each message
// into its own types and pin a protocol version, while a relay stays correct as
// the protocol moves and only has to get JSON-RPC framing and failure right.
//
// Stdout carries protocol frames and nothing else. Every frame written there is
// validated JSON on one line. Anything for a human goes to the log writer.
package mcpbridge

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"sync"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/redact"
)

// Defaults for Options.
const (
	DefaultMaxInboundBytes  = 4 << 20
	DefaultMaxOutboundBytes = 32 << 20
	DefaultRequestTimeout   = 5 * time.Minute
	maxConcurrent           = 16
)

// Options configures a Bridge.
type Options struct {
	// Endpoint is the upstream /mcp URL.
	Endpoint string
	// Client sends requests upstream. It owns the key and its transport rules.
	Client *http.Client
	// StartupErr, when set, is what every request is answered with. A stdio
	// server that exits at once gives an MCP client nothing to show, so missing
	// credentials are reported in protocol instead.
	StartupErr error
	// Redactor scrubs the key from frames and logs.
	Redactor *redact.Redactor
	// Log receives diagnostics. It must not be stdout.
	Log io.Writer
	// MaxInboundBytes caps one message from the client.
	MaxInboundBytes int
	// MaxOutboundBytes caps one frame from the server.
	MaxOutboundBytes int
	// RequestTimeout bounds one upstream exchange.
	RequestTimeout time.Duration
}

// Bridge relays newline-delimited JSON-RPC between stdio and HTTP.
type Bridge struct {
	opts Options
	out  *frameWriter

	mu       sync.Mutex
	cancels  map[string]context.CancelFunc
	session  string
	protocol string
}

// New creates a Bridge writing frames to stdout.
func New(stdout io.Writer, opts Options) *Bridge {
	if opts.MaxInboundBytes <= 0 {
		opts.MaxInboundBytes = DefaultMaxInboundBytes
	}
	if opts.MaxOutboundBytes <= 0 {
		opts.MaxOutboundBytes = DefaultMaxOutboundBytes
	}
	if opts.RequestTimeout <= 0 {
		opts.RequestTimeout = DefaultRequestTimeout
	}
	if opts.Redactor == nil {
		opts.Redactor = redact.New()
	}
	if opts.Log == nil {
		opts.Log = io.Discard
	}
	return &Bridge{
		opts:    opts,
		out:     &frameWriter{w: stdout, redactor: opts.Redactor, max: opts.MaxOutboundBytes},
		cancels: map[string]context.CancelFunc{},
	}
}

// Run relays until stdin closes or ctx ends. In-flight requests finish first on
// a clean close, so a client that writes and closes still gets its answers.
func (b *Bridge) Run(ctx context.Context, stdin io.Reader) error {
	if b.opts.StartupErr != nil {
		b.logf("cannot reach upstream: %v", b.opts.StartupErr)
	}
	reader := bufio.NewReaderSize(stdin, 64*1024)
	slots := make(chan struct{}, maxConcurrent)
	var wg sync.WaitGroup
	defer wg.Wait()

	for ctx.Err() == nil {
		line, tooLong, err := readBoundedLine(reader, b.opts.MaxInboundBytes)
		if tooLong {
			b.reject(nil, codeInvalidRequest, "message exceeds the size limit")
		} else if len(bytes.TrimSpace(line)) > 0 {
			b.dispatch(ctx, line, slots, &wg)
		}
		if err != nil {
			return readEndError(err)
		}
	}
	return nil
}

func readEndError(err error) error {
	if errors.Is(err, io.EOF) {
		return nil
	}
	return fmt.Errorf("reading stdin: %w", err)
}

// readBoundedLine returns one line without buffering more than max bytes. An
// oversized line is consumed through its newline and reported, so the stream
// stays in sync for the next message.
func readBoundedLine(r *bufio.Reader, max int) (line []byte, tooLong bool, err error) {
	for {
		chunk, readErr := r.ReadSlice('\n')
		if !tooLong {
			if len(line)+len(chunk) > max+2 {
				tooLong, line = true, nil
			} else {
				line = append(line, chunk...)
			}
		}
		if !errors.Is(readErr, bufio.ErrBufferFull) {
			return line, tooLong, readErr
		}
	}
}

// dispatch classifies one message and starts its exchange.
func (b *Bridge) dispatch(ctx context.Context, raw []byte, slots chan struct{}, wg *sync.WaitGroup) {
	msg, rejection := parseMessage(raw)
	if rejection != nil {
		b.reject(rejection.id, rejection.code, rejection.message)
		return
	}
	if msg.isCancellation() {
		b.cancelRequest(msg.cancelledID())
		return
	}
	if b.opts.StartupErr != nil {
		b.fail(msg, b.opts.StartupErr)
		return
	}
	select {
	case slots <- struct{}{}:
	case <-ctx.Done():
		return
	}
	reqCtx, cancel := b.track(ctx, msg)
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer func() { <-slots }()
		defer b.untrack(msg, cancel)
		b.exchange(reqCtx, msg)
	}()
}

func (b *Bridge) track(ctx context.Context, msg *message) (context.Context, context.CancelFunc) {
	reqCtx, cancel := context.WithTimeout(ctx, b.opts.RequestTimeout)
	if msg.isRequest() {
		b.mu.Lock()
		b.cancels[string(msg.id)] = cancel
		b.mu.Unlock()
	}
	return reqCtx, cancel
}

func (b *Bridge) untrack(msg *message, cancel context.CancelFunc) {
	cancel()
	if msg.isRequest() {
		b.mu.Lock()
		delete(b.cancels, string(msg.id))
		b.mu.Unlock()
	}
}

func (b *Bridge) cancelRequest(id string) {
	b.mu.Lock()
	cancel := b.cancels[id]
	b.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

// exchange performs one POST and relays what comes back. Per the MCP spec, a
// request the client cancelled gets no response.
func (b *Bridge) exchange(ctx context.Context, msg *message) {
	answered, err := b.post(ctx, msg)
	switch {
	case errors.Is(ctx.Err(), context.Canceled):
		return
	case err != nil:
		b.fail(msg, err)
	case msg.isRequest() && !answered:
		b.fail(msg, errNoResponse)
	}
}

func (b *Bridge) logf(format string, args ...any) {
	line := fmt.Sprintf(format, args...) + "\n"
	_, _ = io.WriteString(b.opts.Log, b.opts.Redactor.Diagnostic(line))
}

// frameWriter serializes frames onto stdout, each validated, one line, scrubbed.
type frameWriter struct {
	mu       sync.Mutex
	w        io.Writer
	redactor *redact.Redactor
	max      int
}

var errInvalidFrame = errors.New("upstream sent an invalid frame")

func (f *frameWriter) write(frame []byte) error {
	var compact bytes.Buffer
	if err := json.Compact(&compact, f.redactor.Bytes(frame)); err != nil {
		return errInvalidFrame
	}
	if compact.Len() > f.max {
		return errFrameTooLarge
	}
	compact.WriteByte('\n')
	f.mu.Lock()
	defer f.mu.Unlock()
	_, err := f.w.Write(compact.Bytes())
	return err
}
