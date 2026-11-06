package migrate

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"reflect"
	"strings"

	"github.com/pelletier/go-toml/v2"
)

// jsonObject is a JSON object that keeps its keys' order and every value's exact text, including large numbers, so an edit to one
// key changes nothing else in a file a client owns.
type jsonObject struct {
	keys   []string
	values []json.RawMessage
}

var errNotObject = errors.New("not a JSON object")

// parseObject takes exactly one object. A null, a list, or anything after the object is refused rather than dropped.
func parseObject(data []byte) (*jsonObject, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	open, err := decoder.Token()
	if err != nil || open != json.Delim('{') {
		return nil, errNotObject
	}
	object := &jsonObject{}
	for decoder.More() {
		key, err := decoder.Token()
		if err != nil {
			return nil, err
		}
		var value json.RawMessage
		if err := decoder.Decode(&value); err != nil {
			return nil, err
		}
		object.keys = append(object.keys, key.(string))
		object.values = append(object.values, value)
	}
	if _, err := decoder.Token(); err != nil {
		return nil, err
	}
	if _, err := decoder.Token(); !errors.Is(err, io.EOF) {
		return nil, errNotObject
	}
	return object, nil
}

func (o *jsonObject) index(key string) int {
	for i, name := range o.keys {
		if name == key {
			return i
		}
	}
	return -1
}

func (o *jsonObject) get(key string) (json.RawMessage, bool) {
	if i := o.index(key); i >= 0 {
		return o.values[i], true
	}
	return nil, false
}

func (o *jsonObject) set(key string, value json.RawMessage) {
	if i := o.index(key); i >= 0 {
		o.values[i] = value
		return
	}
	o.keys = append(o.keys, key)
	o.values = append(o.values, value)
}

func (o *jsonObject) remove(key string) {
	if i := o.index(key); i >= 0 {
		o.keys = append(o.keys[:i], o.keys[i+1:]...)
		o.values = append(o.values[:i], o.values[i+1:]...)
	}
}

func encodeString(text string) json.RawMessage {
	var out bytes.Buffer
	encoder := json.NewEncoder(&out)
	encoder.SetEscapeHTML(false)
	_ = encoder.Encode(text)
	return json.RawMessage(bytes.TrimSpace(out.Bytes()))
}

// raw is the object compact, with each value as it came.
func (o *jsonObject) raw() json.RawMessage {
	var out bytes.Buffer
	out.WriteByte('{')
	for i, key := range o.keys {
		if i > 0 {
			out.WriteByte(',')
		}
		out.Write(encodeString(key))
		out.WriteByte(':')
		out.Write(o.values[i])
	}
	out.WriteByte('}')
	return out.Bytes()
}

// file is the object as a file's text.
func (o *jsonObject) file() ([]byte, error) {
	var out bytes.Buffer
	if err := json.Indent(&out, o.raw(), "", "  "); err != nil {
		return nil, err
	}
	out.WriteByte('\n')
	return out.Bytes(), nil
}

// childObject is a key holding an object, or nil when it is absent. A key of any other type is an error, never replaced.
func (o *jsonObject) childObject(key string) (*jsonObject, error) {
	raw, found := o.get(key)
	if !found {
		return nil, nil
	}
	return parseObject(raw)
}

// listOf is a key holding a list. A key of any other type is an error, never replaced.
func (o *jsonObject) listOf(key string) ([]json.RawMessage, error) {
	raw, found := o.get(key)
	if !found {
		return nil, nil
	}
	var list []json.RawMessage
	if err := json.Unmarshal(raw, &list); err != nil {
		return nil, err
	}
	return list, nil
}

func encodeList(list []json.RawMessage) json.RawMessage {
	var out bytes.Buffer
	out.WriteByte('[')
	for i, item := range list {
		if i > 0 {
			out.WriteByte(',')
		}
		out.Write(item)
	}
	out.WriteByte(']')
	return out.Bytes()
}

func listStrings(list []json.RawMessage) []string {
	out := make([]string, 0, len(list))
	for _, item := range list {
		var text string
		if json.Unmarshal(item, &text) == nil {
			out = append(out, text)
		}
	}
	return out
}

// withoutStrings is a list with these strings taken out.
func withoutStrings(list []json.RawMessage, gone []string) []json.RawMessage {
	kept := make([]json.RawMessage, 0, len(list))
	for _, item := range list {
		var text string
		if json.Unmarshal(item, &text) == nil && containsString(gone, text) {
			continue
		}
		kept = append(kept, item)
	}
	return kept
}

func containsString(list []string, want string) bool {
	for _, item := range list {
		if item == want {
			return true
		}
	}
	return false
}

// tomlTable reads a config for checking only. Its error can quote the file, so it is never repeated.
func tomlTable(data []byte) (map[string]any, error) {
	table := map[string]any{}
	if err := toml.Unmarshal(data, &table); err != nil {
		return nil, errors.New("not valid TOML")
	}
	return table, nil
}

// tomlServers is the server table, or nil. mcp_servers of another type is an error.
func tomlServers(table map[string]any) (map[string]any, error) {
	raw, found := table["mcp_servers"]
	if !found {
		return nil, nil
	}
	servers, ok := raw.(map[string]any)
	if !ok {
		return nil, errors.New("mcp_servers is not a table")
	}
	return servers, nil
}

// tableHeader reads a line as a [table] or [[table]] header, with quoted and spaced keys and a trailing comment. False for anything else.
func tableHeader(line string) ([]string, bool) {
	text := strings.TrimSpace(line)
	if !strings.HasPrefix(text, "[") {
		return nil, false
	}
	text = strings.TrimPrefix(strings.TrimPrefix(text, "["), "[")
	var keys []string
	for {
		key, rest, ok := nextTOMLKey(strings.TrimLeft(text, " \t"))
		if !ok {
			return nil, false
		}
		keys = append(keys, key)
		text = strings.TrimLeft(rest, " \t")
		if !strings.HasPrefix(text, ".") {
			break
		}
		text = text[1:]
	}
	return keys, headerEnds(text)
}

func headerEnds(text string) bool {
	if !strings.HasPrefix(text, "]") {
		return false
	}
	rest := strings.TrimSpace(strings.TrimPrefix(strings.TrimPrefix(text, "]"), "]"))
	return rest == "" || strings.HasPrefix(rest, "#")
}

func nextTOMLKey(text string) (string, string, bool) {
	if text == "" {
		return "", "", false
	}
	if text[0] == '"' || text[0] == '\'' {
		end := quotedEnd(text)
		if end < 0 {
			return "", "", false
		}
		return text[1:end], text[end+1:], true
	}
	end := strings.IndexFunc(text, notBareKey)
	if end <= 0 {
		return "", "", false
	}
	return text[:end], text[end:], true
}

func notBareKey(r rune) bool {
	return !strings.ContainsRune("-_0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", r)
}

// quotedEnd finds the closing quote. Only a basic string has escapes, and a key with one is left to the check after an edit.
func quotedEnd(text string) int {
	for i := 1; i < len(text); i++ {
		if text[0] == '"' && text[i] == '\\' {
			i++
			continue
		}
		if text[i] == text[0] {
			return i
		}
	}
	return -1
}

func isServerTable(keys []string, name string) bool {
	return len(keys) >= 2 && keys[0] == "mcp_servers" && keys[1] == name
}

// splitServerTable takes a server's tables out of a file's lines, with every other byte as it was. A comment just before the next
// table belongs to that table, and stays. It returns what is kept, and what was taken.
func splitServerTable(text, name string) (string, string) {
	var kept, taken strings.Builder
	var held []string
	inside := false
	for _, line := range strings.SplitAfter(text, "\n") {
		if keys, isHeader := tableHeader(line); isHeader {
			inside = isServerTable(keys, name)
			writeAll(&kept, held)
			held = nil
		}
		switch {
		case !inside:
			kept.WriteString(line)
		case isBlankOrComment(line):
			held = append(held, line)
		default:
			writeAll(&taken, held)
			held = nil
			taken.WriteString(line)
		}
	}
	writeAll(&kept, held)
	return kept.String(), taken.String()
}

func writeAll(out *strings.Builder, lines []string) {
	for _, line := range lines {
		out.WriteString(line)
	}
}

func isBlankOrComment(line string) bool {
	text := strings.TrimSpace(line)
	return text == "" || strings.HasPrefix(text, "#")
}

func newlineOf(text string) string {
	if strings.Contains(text, "\r\n") {
		return "\r\n"
	}
	return "\n"
}

func appendTable(text, table string) string {
	newline := newlineOf(text)
	if text != "" && !strings.HasSuffix(text, "\n") {
		text += newline
	}
	return text + newline + strings.ReplaceAll(strings.ReplaceAll(table, "\r\n", "\n"), "\n", newline)
}

// sameExcept checks an edit by what the file means: the two differ only in the named servers. Anything this text-level edit got
// wrong, such as a table written in a form it does not know, stops here rather than reach the file.
func sameExcept(before, after map[string]any, names ...string) bool {
	return reflect.DeepEqual(withoutServers(before, names), withoutServers(after, names))
}

func withoutServers(table map[string]any, names []string) map[string]any {
	copied := make(map[string]any, len(table))
	for key, value := range table {
		copied[key] = value
	}
	servers, ok := table["mcp_servers"].(map[string]any)
	if !ok {
		return copied
	}
	kept := make(map[string]any, len(servers))
	for key, value := range servers {
		if !containsString(names, key) {
			kept[key] = value
		}
	}
	if len(kept) == 0 {
		delete(copied, "mcp_servers")
	} else {
		copied["mcp_servers"] = kept
	}
	return copied
}
