package cmd

import (
	"context"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

type getOptions struct {
	fromLine    int32
	maxLines    int32
	lineNumbers bool
	noBody      bool
}

func newGetCmd(a *app) *cobra.Command {
	var o getOptions
	cmd := &cobra.Command{
		Use:   "get <ref>",
		Short: "Fetch one document by path, qmd:// URI, or docid",
		Example: `  memoria get claude-sessions/2026-05-01.md
  memoria get f6feba --from-line 40 --max-lines 20 --line-numbers`,
		Args: func(_ *cobra.Command, args []string) error {
			if len(args) != 1 {
				return apperr.New(apperr.KindUsage, "get takes exactly one document reference")
			}
			return nil
		},
		RunE: func(cmd *cobra.Command, args []string) error {
			return a.get(cmd.Context(), args[0], buildGetParams(cmd, &o))
		},
	}
	f := cmd.Flags()
	f.Int32Var(&o.fromLine, "from-line", 0, "First line of the body to return (1-indexed)")
	f.Int32Var(&o.maxLines, "max-lines", 0, "Number of lines to return")
	f.BoolVar(&o.lineNumbers, "line-numbers", false, "Prefix each line with its number")
	f.BoolVar(&o.noBody, "no-body", false, "Return metadata only")
	return cmd
}

func buildGetParams(cmd *cobra.Command, o *getOptions) *api.GetDocumentParams {
	changed := cmd.Flags().Changed
	params := &api.GetDocumentParams{}
	if changed("from-line") {
		params.FromLine = &o.fromLine
	}
	if changed("max-lines") {
		params.MaxLines = &o.maxLines
	}
	if o.lineNumbers {
		params.LineNumbers = &o.lineNumbers
	}
	if o.noBody {
		body := false
		params.Body = &body
	}
	return params
}

func (a *app) get(ctx context.Context, ref string, params *api.GetDocumentParams) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.GetDocumentWithResponse(ctx, ref, params)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if resp.JSON200 == nil {
		return errBadResponse
	}
	switch a.format(output.Markdown) {
	case output.JSON:
		return a.writeRaw(resp.Body)
	case output.Table:
		return output.DocumentTable(a.stdout, []api.Document{*resp.JSON200})
	}
	return output.DocumentMarkdown(a.stdout, resp.JSON200)
}

type multiGetOptions struct {
	pattern     string
	maxBytes    int32
	maxLines    int32
	lineNumbers bool
	noBody      bool
}

func newMultiGetCmd(a *app) *cobra.Command {
	var o multiGetOptions
	cmd := &cobra.Command{
		Use:   "multi-get [ref]...",
		Short: "Fetch several documents by reference, glob, or both",
		Long: `Fetch several documents. Bodies are capped at --max-bytes each (server default
64 KiB); larger documents are reported on stderr as skipped.

Exits 4 if any reference could not be resolved, or if nothing matched. Whatever
was found is still written first.`,
		Example: `  memoria multi-get f6feba claude-sessions/b.md
  memoria multi-get --pattern 'claude-sessions/2026-05*.md' --format json`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return a.multiGet(cmd.Context(), buildMultiGetRequest(cmd, &o, args))
		},
	}
	f := cmd.Flags()
	f.StringVar(&o.pattern, "pattern", "", "Glob selecting documents")
	f.Int32Var(&o.maxBytes, "max-bytes", 0, "Skip documents larger than this")
	f.Int32Var(&o.maxLines, "max-lines", 0, "Lines to return from each document")
	f.BoolVar(&o.lineNumbers, "line-numbers", false, "Prefix each line with its number")
	f.BoolVar(&o.noBody, "no-body", false, "Return metadata only")
	return cmd
}

func buildMultiGetRequest(cmd *cobra.Command, o *multiGetOptions, args []string) api.MultiGetRequest {
	changed := cmd.Flags().Changed
	request := api.MultiGetRequest{Ids: optionalList(args), Pattern: optionalString(o.pattern)}
	if changed("max-bytes") {
		request.MaxBytes = &o.maxBytes
	}
	if changed("max-lines") {
		request.MaxLines = &o.maxLines
	}
	if o.lineNumbers {
		request.LineNumbers = &o.lineNumbers
	}
	if o.noBody {
		include := false
		request.IncludeBody = &include
	}
	return request
}

func (a *app) multiGet(ctx context.Context, request api.MultiGetRequest) error {
	if request.Ids == nil && request.Pattern == nil {
		return apperr.New(apperr.KindUsage, "multi-get needs document references, --pattern, or both")
	}
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.GetDocumentsWithResponse(ctx, request)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if resp.JSON200 == nil {
		return errBadResponse
	}
	if err := a.renderMultiGet(resp.Body, resp.JSON200); err != nil {
		return err
	}
	return a.reportMultiGet(resp.JSON200)
}

func (a *app) renderMultiGet(raw []byte, result *api.MultiGetResult) error {
	switch a.format(output.Markdown) {
	case output.JSON:
		return a.writeRaw(raw)
	case output.Table:
		return output.DocumentTable(a.stdout, result.Docs)
	}
	return output.DocumentsMarkdown(a.stdout, result.Docs)
}

func (a *app) reportMultiGet(result *api.MultiGetResult) error {
	for _, skipped := range result.Skipped {
		a.warn("skipped " + termsafe.Line(skipped.File) + ": " + termsafe.Line(skipped.Reason))
	}
	for _, problem := range result.Errors {
		a.warn(termsafe.Line(problem))
	}
	switch {
	case len(result.Errors) > 0:
		return apperr.New(apperr.KindNotFound, "%d of the requested documents could not be fetched", len(result.Errors))
	case len(result.Docs) == 0 && len(result.Skipped) == 0:
		return apperr.New(apperr.KindNotFound, "no documents matched")
	}
	return nil
}

func newCollectionsCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "collections",
		Short: "List the collections this key may search",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.collections(cmd.Context()) },
	}
}

func (a *app) collections(ctx context.Context) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.ListCollectionsWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if resp.JSON200 == nil {
		return errBadResponse
	}
	switch a.format(output.Table) {
	case output.JSON:
		return a.writeRaw(resp.Body)
	case output.Markdown:
		return output.CollectionsMarkdown(a.stdout, resp.JSON200)
	}
	return output.CollectionsTable(a.stdout, resp.JSON200)
}

func newStatusCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "status",
		Short: "Show index totals and per-collection counts",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.status(cmd.Context()) },
	}
}

func (a *app) status(ctx context.Context) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.GetStatusWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if resp.JSON200 == nil {
		return errBadResponse
	}
	switch a.format(output.Table) {
	case output.JSON:
		return a.writeRaw(resp.Body)
	case output.Markdown:
		return output.StatusMarkdown(a.stdout, resp.JSON200)
	}
	return output.StatusTable(a.stdout, resp.JSON200)
}
