package cmd

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// keyIDPattern has no underscore, so a whole key pasted where an id belongs is
// refused here, rather than going into a URL path and the server's logs.
var keyIDPattern = regexp.MustCompile(`^[A-Za-z0-9-]{1,64}$`)

func newKeysCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "keys",
		Short: "Manage API keys (needs a key with the admin scope)",
		Long: `Issue, list, rotate and revoke API keys on the server.

These commands need a key with the admin scope. The first one comes from running
the server with --bootstrap-admin-key. Scopes do not imply one another: an admin
key manages keys but cannot read the corpus, so issue a separate key to search with.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newKeysCreateCmd(a), newKeysListCmd(a), newKeysRotateCmd(a), newKeysRevokeCmd(a))
	return cmd
}

func oneKeyID(cmd *cobra.Command, args []string) error {
	if len(args) != 1 {
		return apperr.New(apperr.KindUsage, "%q takes exactly one key id", cmd.CommandPath())
	}
	if !keyIDPattern.MatchString(args[0]) {
		return apperr.New(apperr.KindUsage, "that is not a key id. Pass the id from 'memoria keys list', not the key itself; if you pasted a key, it has not been sent")
	}
	return nil
}

type keysCreateOptions struct {
	label          string
	scopes         []string
	collections    []string
	allCollections bool
	expiresAt      string
}

func newKeysCreateCmd(a *app) *cobra.Command {
	var o keysCreateOptions
	cmd := &cobra.Command{
		Use:   "create",
		Short: "Issue a key. Its plaintext goes to stdout, once",
		Long: `Issue an API key. The plaintext is written to stdout, this one time, and cannot be
recovered. Everything else goes to stderr, so stdout can be captured or piped.

A read or ingest key must list --collection, or pass --all-collections to reach
every collection, including any added later. An admin-only key needs neither.`,
		Example: `  memoria keys create --label laptop --scope read --collection claude-sessions
  memoria keys create --label backup --scope read --all-collections
  memoria keys create --label ops --scope admin --expires-at 2027-01-01T00:00:00Z`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.keysCreate(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.label, "label", "", "Name for the key (required)")
	f.StringArrayVar(&o.scopes, "scope", nil, "read, ingest or admin; repeatable (required)")
	f.StringArrayVar(&o.collections, "collection", nil, "Collection this key may reach; repeatable")
	f.BoolVar(&o.allCollections, "all-collections", false, "Reach every collection, now and in future. Never the default")
	f.StringVar(&o.expiresAt, "expires-at", "", "Expiry as an RFC 3339 time, such as 2027-01-01T00:00:00Z")
	return cmd
}

// build checks what the server would refuse anyway, for a better message, and maps
// flags to the request. An unset collection list stays null: the server reads an
// empty list as a mistake, and only --all-collections asks for everything.
func (o keysCreateOptions) build() (api.CreateApiKeyRequest, error) {
	request := api.CreateApiKeyRequest{Label: &o.label, Scopes: &o.scopes, Collections: optionalList(o.collections)}
	if strings.TrimSpace(o.label) == "" || len(o.scopes) == 0 {
		return request, apperr.New(apperr.KindUsage, "--label and at least one --scope are required")
	}
	if o.allCollections && len(o.collections) > 0 {
		return request, apperr.New(apperr.KindUsage, "use --collection or --all-collections, not both")
	}
	if o.allCollections {
		request.AllCollections = &o.allCollections
	}
	if o.expiresAt == "" {
		return request, nil
	}
	expires, err := time.Parse(time.RFC3339, o.expiresAt)
	if err != nil {
		return request, apperr.New(apperr.KindUsage, "--expires-at must be an RFC 3339 time, such as 2027-01-01T00:00:00Z")
	}
	request.ExpiresAt = &expires
	return request, nil
}

func (a *app) keysCreate(ctx context.Context, o keysCreateOptions) error {
	request, err := o.build()
	if err != nil {
		return err
	}
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.CreateApiKeyWithResponse(ctx, request)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusCreated, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	return a.emitIssuedKey(resp.JSON201, resp.Body, "Created")
}

// emitIssuedKey is the one place a plaintext key is written. The description goes
// to stderr first, because if stdout then fails the key exists and has been lost.
func (a *app) emitIssuedKey(issued *api.IssuedApiKey, raw []byte, verb string) error {
	if err := requireShape(issued != nil && issued.Key != "" && issued.Info.Id != ""); err != nil {
		return err
	}
	a.info(fmt.Sprintf("%s key %s (%s). This is the only time its plaintext is available.", verb, issued.Info.Id, scopeSummary(&issued.Info)))
	if err := a.writeIssuedKey(issued, raw); err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, fmt.Sprintf("key %s exists, but its plaintext could not be written; revoke it", termsafe.Line(issued.Info.Id)))
	}
	// From here on, nothing else this process writes may repeat it.
	a.redactor.Add(issued.Key)
	return nil
}

func (a *app) writeIssuedKey(issued *api.IssuedApiKey, raw []byte) error {
	if a.format(output.Table) == output.JSON {
		return a.writeRaw(raw)
	}
	_, err := fmt.Fprintln(a.stdout, issued.Key)
	return err
}

func scopeSummary(info *api.ApiKeyInfo) string {
	return termsafe.Line(strings.Join(info.Scopes, ",")) + "; " + output.KeyCollections(info)
}

func expectStatus(want int, resp *http.Response, body []byte) error {
	if resp.StatusCode == want {
		return nil
	}
	if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		return errBadResponse
	}
	return apperr.FromResponse(resp.StatusCode, resp.Header, body)
}

func newKeysListCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "list",
		Short: "List keys, including revoked and expired ones. Never shows key material",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.keysList(cmd.Context()) },
	}
}

func (a *app) keysList(ctx context.Context) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.ListApiKeysWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusOK, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	if err := requireShape(resp.JSON200 != nil && resp.JSON200.Keys != nil); err != nil {
		return err
	}
	list := resp.JSON200
	return a.present(ctx, output.Table, resp.Body, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.KeysMarkdown(w, list) },
		output.Table:    func(w io.Writer) error { return output.KeysTable(w, list) },
	})
}

func newKeysRotateCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "rotate <id>",
		Short: "Replace a key's secret. The new plaintext goes to stdout, once",
		Long:  "Replace a key's secret, keeping its id, scopes, collections and expiry. The old token stops working at once. A revoked or expired key cannot be rotated.",
		Args:  oneKeyID,
		RunE:  func(cmd *cobra.Command, args []string) error { return a.keysRotate(cmd.Context(), args[0]) },
	}
}

func (a *app) keysRotate(ctx context.Context, id string) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.RotateApiKeyWithResponse(ctx, id)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusOK, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	if err := a.emitIssuedKey(resp.JSON200, resp.Body, "Rotated"); err != nil {
		return err
	}
	if s.keyID == id {
		a.warn("that was the key this command used, and it has stopped working. Run 'memoria auth login' with the new one")
	}
	return nil
}

func newKeysRevokeCmd(a *app) *cobra.Command {
	var force bool
	cmd := &cobra.Command{
		Use:   "revoke <id>",
		Short: "Revoke a key. It stops working on its next request",
		Long: `Revoke a key. Revoking a revoked key succeeds.

Revoking the key this command is using needs --force. If it is your only admin key,
you would need the server's --bootstrap-admin-key to get another.`,
		Args: oneKeyID,
		RunE: func(cmd *cobra.Command, args []string) error { return a.keysRevoke(cmd.Context(), args[0], force) },
	}
	cmd.Flags().BoolVar(&force, "force", false, "Allow revoking the key this command is using")
	return cmd
}

func (a *app) keysRevoke(ctx context.Context, id string, force bool) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	if s.keyID == id && !force {
		return apperr.New(apperr.KindUsage, "that is the key this command is using; pass --force to revoke it anyway")
	}
	resp, err := s.client.RevokeApiKeyWithResponse(ctx, id)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusNoContent, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	a.info("Revoked key " + id + ".")
	return nil
}
