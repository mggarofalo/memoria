package redact

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
)

var errTrailingJSON = errors.New("trailing data after JSON value")

// JSON scrubs known secrets from a JSON document after decoding it, so a form with
// JSON escapes in it cannot slip past a byte-level match. When nothing
// matches, it returns the input byte for byte. Otherwise it rewrites the document
// with member order and number spelling intact.
//
// A member name that scrubs to the same text as another stays distinct, so no
// members collapse. A top-level "id" equal to keepID stays as it is: that value
// is the client's own, and the client needs it to match the response.
//
// What this does not catch: a secret split across strings, or transformed in any
// way other than the encodings Add registers.
func (r *Redactor) JSON(raw []byte, keepID string) ([]byte, error) {
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	w := jsonRewriter{r: r, dec: dec, keepID: keepID}
	if err := w.value(0, ""); err != nil {
		return nil, err
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return nil, errTrailingJSON
	}
	if !w.changed {
		return raw, nil
	}
	return w.out.Bytes(), nil
}

type jsonRewriter struct {
	r       *Redactor
	dec     *json.Decoder
	out     bytes.Buffer
	keepID  string
	changed bool
}

func (w *jsonRewriter) value(depth int, member string) error {
	tok, err := w.dec.Token()
	if err != nil {
		return err
	}
	switch t := tok.(type) {
	case json.Delim:
		if t == '{' {
			return w.object(depth)
		}
		return w.array(depth)
	case string:
		w.writeString(w.scrubValue(t, depth, member))
	case json.Number:
		w.out.WriteString(t.String())
	default:
		fmt.Fprint(&w.out, jsonScalar(t))
	}
	return nil
}

func jsonScalar(t json.Token) string {
	if t == nil {
		return "null"
	}
	return fmt.Sprint(t)
}

func (w *jsonRewriter) scrubValue(s string, depth int, member string) string {
	if depth == 1 && member == "id" && w.keepID != "" && s == w.keepID {
		return s
	}
	return w.scrub(s)
}

func (w *jsonRewriter) scrub(s string) string {
	scrubbed := w.r.String(s)
	if scrubbed != s {
		w.changed = true
	}
	return scrubbed
}

func (w *jsonRewriter) writeString(s string) {
	// Marshalling a string cannot fail.
	data, _ := json.Marshal(s)
	w.out.Write(data)
}

func (w *jsonRewriter) object(depth int) error {
	w.out.WriteByte('{')
	seen := map[string]int{}
	for first := true; w.dec.More(); first = false {
		if !first {
			w.out.WriteByte(',')
		}
		tok, err := w.dec.Token()
		if err != nil {
			return err
		}
		name, _ := tok.(string)
		w.writeString(w.memberName(name, seen))
		w.out.WriteByte(':')
		if err := w.value(depth+1, name); err != nil {
			return err
		}
	}
	return w.closeDelim('}')
}

// memberName scrubs a member name, numbering any that would then collide.
func (w *jsonRewriter) memberName(name string, seen map[string]int) string {
	scrubbed := w.scrub(name)
	seen[scrubbed]++
	if count := seen[scrubbed]; count > 1 && strings.Contains(scrubbed, Placeholder) {
		return fmt.Sprintf("%s-%d", scrubbed, count)
	}
	return scrubbed
}

func (w *jsonRewriter) array(depth int) error {
	w.out.WriteByte('[')
	for first := true; w.dec.More(); first = false {
		if !first {
			w.out.WriteByte(',')
		}
		if err := w.value(depth+1, ""); err != nil {
			return err
		}
	}
	return w.closeDelim(']')
}

func (w *jsonRewriter) closeDelim(delim byte) error {
	if _, err := w.dec.Token(); err != nil {
		return err
	}
	w.out.WriteByte(delim)
	return nil
}
