package redact

import (
	"encoding/json"
	"strings"
	"testing"
)

const (
	jsonKey   = "mem_0123456789abcdef_feedfacefeedface"
	backslash = string(rune(92))
)

func newJSONRedactor() *Redactor {
	r := New()
	r.Add(jsonKey)
	return r
}

func TestJSONLeavesCleanDocumentsByteForByte(t *testing.T) {
	raw := []byte(`{ "b": 1.0, "a" : [true, null, "x` + backslash + `u0041"] }`)
	got, err := newJSONRedactor().JSON(raw, "")
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != string(raw) {
		t.Fatalf("got %s", got)
	}
}

func TestJSONCatchesEscapedForms(t *testing.T) {
	for name, raw := range map[string]string{
		"escaped underscore": `{"text":"mem` + backslash + `u005f0123456789abcdef_feedfacefeedface"}`,
		"escaped letter":     `{"text":"` + backslash + `u006dem_0123456789abcdef_feedfacefeedface"}`,
		"in an array":        `{"a":[{"b":["mem` + backslash + `u005f0123456789abcdef_feedfacefeedface"]}]}`,
		"secret half alone":  `{"text":"feedfacefeed` + backslash + `u0066ace"}`,
	} {
		t.Run(name, func(t *testing.T) {
			got, err := newJSONRedactor().JSON([]byte(raw), "")
			if err != nil {
				t.Fatal(err)
			}
			if strings.Contains(string(got), "feedface") || strings.Contains(string(got), backslash+"u") {
				t.Fatalf("got %s", got)
			}
			if !strings.Contains(string(got), Placeholder) || !json.Valid(got) {
				t.Fatalf("got %s", got)
			}
		})
	}
}

func TestJSONKeepsOrderAndNumberSpelling(t *testing.T) {
	raw := `{"z":1.0,"a":"` + jsonKey + `","m":[null,false,2e3]}`
	got, err := newJSONRedactor().JSON([]byte(raw), "")
	if err != nil {
		t.Fatal(err)
	}
	want := `{"z":1.0,"a":"` + Placeholder + `","m":[null,false,2e3]}`
	if string(got) != want {
		t.Fatalf("got %s", got)
	}
}

func TestJSONMemberNamesDoNotCollapse(t *testing.T) {
	raw := `{"` + jsonKey + `":1,"mem` + backslash + `u005f0123456789abcdef_feedfacefeedface":2,"other":3}`
	got, err := newJSONRedactor().JSON([]byte(raw), "")
	if err != nil {
		t.Fatal(err)
	}
	var decoded map[string]int
	if err := json.Unmarshal(got, &decoded); err != nil {
		t.Fatal(err)
	}
	if len(decoded) != 3 || decoded["other"] != 3 {
		t.Fatalf("members collapsed: %s", got)
	}
	if strings.Contains(string(got), "feedface") {
		t.Fatalf("got %s", got)
	}
}

func TestLiteralPlaceholderMembersDoNotCollide(t *testing.T) {
	for _, raw := range []string{
		`{"` + Placeholder + `-2":1,"` + jsonKey + `":2,"mem_0123456789abcdef_feedfacefeedface` + backslash + `u0020":3,"` + Placeholder + `":4}`,
		`{"` + jsonKey + `":1,"` + Placeholder + `":2,"` + Placeholder + `-2":3,"` + Placeholder + `-3":4}`,
	} {
		got, err := newJSONRedactor().JSON([]byte(raw), "")
		if err != nil {
			t.Fatal(err)
		}
		var decoded map[string]int
		if err := json.Unmarshal(got, &decoded); err != nil {
			t.Fatal(err)
		}
		if len(decoded) != 4 {
			t.Errorf("members collapsed: %s", got)
		}
	}
}

func TestJSONKeepsTheClientsOwnID(t *testing.T) {
	raw := `{"id":"` + jsonKey + `","result":{"id":"` + jsonKey + `","text":"` + jsonKey + `"}}`
	got, err := newJSONRedactor().JSON([]byte(raw), jsonKey)
	if err != nil {
		t.Fatal(err)
	}
	var frame struct {
		ID     string `json:"id"`
		Result struct {
			ID   string `json:"id"`
			Text string `json:"text"`
		} `json:"result"`
	}
	if err := json.Unmarshal(got, &frame); err != nil {
		t.Fatal(err)
	}
	if frame.ID != jsonKey {
		t.Fatalf("correlation broke: %s", got)
	}
	if frame.Result.ID != Placeholder || frame.Result.Text != Placeholder {
		t.Fatalf("nested values must still be scrubbed: %s", got)
	}
}

func TestJSONRejectsInvalidDocuments(t *testing.T) {
	for _, raw := range []string{`{not json`, `{"a":1} trailing`, `{"a":1}{"b":2}`, `[1,`, `{"a"`, ``} {
		if _, err := newJSONRedactor().JSON([]byte(raw), ""); err == nil {
			t.Errorf("JSON(%q) should fail", raw)
		}
	}
}

func TestJSONScalars(t *testing.T) {
	for _, raw := range []string{`null`, `true`, `3`, `"x"`, `[]`, `{}`} {
		got, err := newJSONRedactor().JSON([]byte(raw), "")
		if err != nil || string(got) != raw {
			t.Errorf("JSON(%q) = %s, %v", raw, got, err)
		}
	}
}

func TestSecretHalfIsRedactedEverywhere(t *testing.T) {
	r := newJSONRedactor()
	if got := r.String("pasted feedfacefeedface here"); strings.Contains(got, "feedface") {
		t.Fatalf("got %q", got)
	}
	if got := r.String("key id 0123456789abcdef is public"); !strings.Contains(got, "0123456789abcdef") {
		t.Fatalf("the public id should survive: %q", got)
	}
}
