package api

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"slices"
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

func newTestClient(t *testing.T, handler http.HandlerFunc) *ClientWithResponses {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	client, err := NewClientWithResponses(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	return client
}

func assertSearchRequest(t *testing.T, got *http.Request) {
	t.Helper()
	if got.URL.Path != "/v1/search" {
		t.Fatalf("path = %q", got.URL.Path)
	}
	q := got.URL.Query()
	if q.Get("query") != "api key auth" || q.Get("limit") != "5" || q.Get("rerank") != "false" {
		t.Fatalf("query string = %q", got.URL.RawQuery)
	}
	// Repeated parameters must stay separate.
	if !slices.Equal(q["collection"], []string{"claude-sessions", "codex-sessions"}) {
		t.Fatalf("collection params = %v", q["collection"])
	}
}

func assertSearchResult(t *testing.T, result *SearchResult) {
	t.Helper()
	if result == nil || result.Total != 1 || len(result.Results) != 1 {
		t.Fatalf("unexpected result %+v", result)
	}
	hit := result.Results[0]
	if hit.Docid != "f6feba" || hit.Score != 0.91 || hit.Line != 42 {
		t.Fatalf("unexpected hit %+v", hit)
	}
	assertProject(t, hit.Metadata, "Receipts")
	if !result.Query.Rerank {
		t.Fatal("rerank should decode as true")
	}
}

func assertProject(t *testing.T, metadata *DocumentMetadata, want string) {
	t.Helper()
	if metadata == nil || metadata.Project == nil || *metadata.Project != want {
		t.Fatalf("metadata not decoded: %+v", metadata)
	}
}

func TestSearchWithResponse(t *testing.T) {
	var got *http.Request
	client := newTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		got = r
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(searchBody))
	})

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
	assertSearchRequest(t, got)
	if resp.StatusCode() != http.StatusOK {
		t.Fatalf("status %d, body %s", resp.StatusCode(), resp.Body)
	}
	assertSearchResult(t, resp.JSON200)
}

func TestGetDocumentWithResponse_ProblemDetails(t *testing.T) {
	var got *http.Request
	client := newTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		got = r
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write([]byte(`{"title":"Not found","status":404,"detail":"Document not found","code":"not_found","traceId":"abc"}`))
	})

	from := int32(10)
	resp, err := client.GetDocumentWithResponse(context.Background(), "claude-sessions/missing.md", &GetDocumentParams{FromLine: &from})
	if err != nil {
		t.Fatal(err)
	}
	if got.URL.Path != "/v1/docs/claude-sessions/missing.md" || got.URL.Query().Get("fromLine") != "10" {
		t.Errorf("request = %q ? %q", got.URL.Path, got.URL.RawQuery)
	}
	if resp.StatusCode() != http.StatusNotFound {
		t.Fatalf("status %d", resp.StatusCode())
	}
	problem := resp.ApplicationproblemJSON404
	if problem == nil || problem.Title == nil || *problem.Title != "Not found" {
		t.Fatalf("problem details not decoded: %s", resp.Body)
	}
	var raw map[string]any
	if err := json.Unmarshal(resp.Body, &raw); err != nil || raw["code"] != "not_found" {
		t.Fatalf("raw body should keep extensions: %s", resp.Body)
	}
}

func TestGetDocumentsWithResponse_PostsBody(t *testing.T) {
	var got *http.Request
	var body struct {
		IDs []string `json:"ids"`
	}
	client := newTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		got = r
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"docs":[],"skipped":[{"file":"a.md","reason":"too large"}],"errors":[]}`))
	})

	ids := []string{"f6feba", "claude-sessions/b.md"}
	resp, err := client.GetDocumentsWithResponse(context.Background(), GetDocumentsJSONRequestBody{Ids: &ids})
	if err != nil {
		t.Fatal(err)
	}
	if got.Method != http.MethodPost || got.URL.Path != "/v1/docs:multiGet" {
		t.Errorf("%s %s", got.Method, got.URL.Path)
	}
	if !slices.Equal(body.IDs, ids) {
		t.Errorf("ids = %v", body.IDs)
	}
	if resp.JSON200 == nil || len(resp.JSON200.Skipped) != 1 || resp.JSON200.Skipped[0].Reason != "too large" {
		t.Fatalf("unexpected response %s", resp.Body)
	}
}

func ptr[T any](v T) *T { return &v }
