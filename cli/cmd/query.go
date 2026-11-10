package cmd

import (
	"context"
	"fmt"
	"io"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/output"
)

type queryOptions struct {
	collections    []string
	limit          int32
	minScore       float64
	noRerank       bool
	expand         bool
	intent         string
	candidateLimit int32
	projects       []string
	branches       []string
	tags           []string
	since          string
	until          string
}

func newQueryCmd(a *app) *cobra.Command {
	var o queryOptions
	cmd := &cobra.Command{
		Use:   "query <text>...",
		Short: "Search the corpus",
		Long: `Hybrid search: BM25 and vectors, fused, then reranked unless --no-rerank.

Frontmatter filters (--project, --branch, --tag, --since, --until) apply after
retrieval, so a filtered result can be shorter than --limit.`,
		Example: `  memoria query "api key rotation" --collection claude-sessions --limit 5
  memoria query retries --project Receipts --since 2026-01-01 --format json`,
		Args: func(cmd *cobra.Command, args []string) error {
			if len(args) == 0 {
				return apperr.New(apperr.KindUsage, "query needs search text")
			}
			return nil
		},
		RunE: func(cmd *cobra.Command, args []string) error {
			return a.query(cmd.Context(), strings.Join(args, " "), buildSearchParams(cmd, &o))
		},
	}
	f := cmd.Flags()
	f.StringArrayVar(&o.collections, "collection", nil, "Collection to search; repeat for several")
	f.Int32Var(&o.limit, "limit", 0, "Maximum results")
	f.Float64Var(&o.minScore, "min-score", 0, "Drop results scoring below this, 0 to 1")
	f.BoolVar(&o.noRerank, "no-rerank", false, "Skip the reranker: sub-second lexical and vector fusion")
	f.BoolVar(&o.expand, "expand", false, "Let the server's model expand the query into more sub-queries first: slower, and no better on the measured corpus. Unset, the server decides")
	f.StringVar(&o.intent, "intent", "", "Background that steers reranking and snippets; does not search on its own")
	f.Int32Var(&o.candidateLimit, "candidate-limit", 0, "Candidates handed to the reranker")
	f.StringArrayVar(&o.projects, "project", nil, "Frontmatter filter: project equals any of these; repeatable")
	f.StringArrayVar(&o.branches, "branch", nil, "Frontmatter filter: git branch equals any of these; repeatable")
	f.StringArrayVar(&o.tags, "tag", nil, "Frontmatter filter: every listed tag must be present; repeatable")
	f.StringVar(&o.since, "since", "", "Frontmatter filter: start time on or after this date or timestamp")
	f.StringVar(&o.until, "until", "", "Frontmatter filter: start time on or before this; a bare date covers the day")
	return cmd
}

// buildSearchParams sends only what the user set, so server defaults stay in
// charge of everything else.
func buildSearchParams(cmd *cobra.Command, o *queryOptions) *api.SearchParams {
	changed := cmd.Flags().Changed
	params := &api.SearchParams{
		Collection: optionalList(o.collections),
		Project:    optionalList(o.projects),
		Branch:     optionalList(o.branches),
		Tag:        optionalList(o.tags),
		Intent:     optionalString(o.intent),
		Since:      optionalString(o.since),
		Until:      optionalString(o.until),
	}
	if changed("limit") {
		params.Limit = &o.limit
	}
	if changed("candidate-limit") {
		params.CandidateLimit = &o.candidateLimit
	}
	if changed("min-score") {
		params.MinScore = &o.minScore
	}
	if o.noRerank {
		rerank := false
		params.Rerank = &rerank
	}
	// Sent only when given, so --expand=false can also overrule a server that expands.
	if changed("expand") {
		params.Expand = &o.expand
	}
	return params
}

func optionalList(values []string) *[]string {
	if len(values) == 0 {
		return nil
	}
	return &values
}

func optionalString(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}

func (a *app) query(ctx context.Context, text string, params *api.SearchParams) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	params.Query = &text
	resp, err := s.client.SearchWithResponse(ctx, params)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if err := requireShape(resp.JSON200 != nil && resp.JSON200.Results != nil); err != nil {
		return err
	}
	if err := a.renderSearch(ctx, resp.Body, resp.JSON200); err != nil {
		return err
	}
	if resp.JSON200.Query.Filtered && len(resp.JSON200.Results) < int(resp.JSON200.Query.Limit) {
		a.info(fmt.Sprintf("Filters ran after retrieval over %d candidates; more matches may exist.", resp.JSON200.Query.Fetched))
	}
	return nil
}

func (a *app) renderSearch(ctx context.Context, raw []byte, result *api.SearchResult) error {
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.SearchMarkdown(w, result) },
		output.Table:    func(w io.Writer) error { return output.SearchTable(w, result) },
	})
}
