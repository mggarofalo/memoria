package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

// The generated client must round-trip the shapes the committed spec
// declares. These tests pin the parts the CLI will build on: query-string
// encoding of repeated parameters, typed 200 bodies, and problem details.

const searchBody = `{
  "results": [{
    "docid": "f6feba", "file": "claude-sessions/a.md", "collection": "claude-sessions", "path": "a.md",
    "title": "A", "score": 0.91, "line": 42, "snippet": "...", "context": null,
    "metadata": {"project": "Receipts", "branch": null, "date": "2026-02-11", "startTime": null, "endTime": null, "tags": ["project/Receipts"], "sessionId": null, "source": null}
  }],
  "total": 1, "tookMs": 3700,
  "query": {"collections": ["claude-sessions"], "rerank": true, "filtered": false, "fetched": 40, "limit": 10}
}`

func TestSearchWithResponse(t *testing.T) {
	var got *http.Request
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(searchBody))
	}))
	defer server.Close()

	client, err := NewClientWithResponses(server.URL)
	if err != nil {
		t.Fatal(err)
	}

	limit := int32(5)
	rerank := false
	collections := []string{"claude-sessions", "codex-sessions"}
	resp, err := client.SearchWithResponse(context.Background(), &SearchParams{
		Query:      ptr("api key auth"),
		Collection: &collections,
		Limit:      &limit,
		Rerank:     &rerank,
	})
	if err != nil {
		t.Fatal(err)
	}

	if got.URL.Path != "/v1/search" {
		t.Fatalf("path = %q", got.URL.Path)
	}
	q := got.URL.Query()
	if q.Get("query") != "api key auth" || q.Get("limit") != "5" || q.Get("rerank") != "false" {
		t.Fatalf("query string = %q", got.URL.RawQuery)
	}
	if c := q["collection"]; len(c) != 2 || c[0] != "claude-sessions" || c[1] != "codex-sessions" {
		t.Fatalf("collection params = %v (repeated parameters must stay separate)", c)
	}

	if resp.StatusCode() != http.StatusOK || resp.JSON200 == nil {
		t.Fatalf("status %d, body %s", resp.StatusCode(), resp.Body)
	}
	if resp.JSON200.Total != 1 || len(resp.JSON200.Results) != 1 {
		t.Fatalf("unexpected result %+v", resp.JSON200)
	}
	hit := resp.JSON200.Results[0]
	if hit.Docid != "f6feba" || hit.Score != 0.91 || hit.Line != 42 {
		t.Fatalf("unexpected hit %+v", hit)
	}
	if hit.Metadata == nil || hit.Metadata.Project == nil || *hit.Metadata.Project != "Receipts" {
		t.Fatalf("metadata not decoded: %+v", hit.Metadata)
	}
	if !resp.JSON200.Query.Rerank {
		t.Fatal("rerank should decode as true")
	}
}

func TestGetDocumentWithResponse_ProblemDetails(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/docs/claude-sessions/missing.md" {
			t.Errorf("path = %q", r.URL.Path)
		}
		if r.URL.Query().Get("fromLine") != "10" {
			t.Errorf("fromLine missing from %q", r.URL.RawQuery)
		}
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write([]byte(`{"title":"Not found","status":404,"detail":"Document not found","code":"not_found","traceId":"abc"}`))
	}))
	defer server.Close()

	client, err := NewClientWithResponses(server.URL)
	if err != nil {
		t.Fatal(err)
	}

	from := int32(10)
	resp, err := client.GetDocumentWithResponse(context.Background(), "claude-sessions/missing.md", &GetDocumentParams{FromLine: &from})
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode() != http.StatusNotFound {
		t.Fatalf("status %d", resp.StatusCode())
	}
	if resp.ApplicationproblemJSON404 == nil || resp.ApplicationproblemJSON404.Title == nil || *resp.ApplicationproblemJSON404.Title != "Not found" {
		t.Fatalf("problem details not decoded: %s", resp.Body)
	}
	var raw map[string]any
	if err := json.Unmarshal(resp.Body, &raw); err != nil || raw["code"] != "not_found" {
		t.Fatalf("raw body should keep extensions: %s", resp.Body)
	}
}

func TestGetDocumentsWithResponse_PostsBody(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.Path != "/v1/docs:multiGet" {
			t.Errorf("%s %s", r.Method, r.URL.Path)
		}
		var body map[string]any
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if ids, ok := body["ids"].([]any); !ok || len(ids) != 2 {
			t.Errorf("ids = %v", body["ids"])
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"docs":[],"skipped":[{"file":"a.md","reason":"too large"}],"errors":[]}`))
	}))
	defer server.Close()

	client, err := NewClientWithResponses(server.URL)
	if err != nil {
		t.Fatal(err)
	}

	ids := []string{"f6feba", "claude-sessions/b.md"}
	resp, err := client.GetDocumentsWithResponse(context.Background(), GetDocumentsJSONRequestBody{Ids: &ids})
	if err != nil {
		t.Fatal(err)
	}
	if resp.JSON200 == nil || len(resp.JSON200.Skipped) != 1 || resp.JSON200.Skipped[0].Reason != "too large" {
		t.Fatalf("unexpected response %s", resp.Body)
	}
}

func ptr[T any](v T) *T { return &v }
