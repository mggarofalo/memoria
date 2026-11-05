package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"math/rand/v2"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

const (
	defaultVerifySample = 20
	overlapTop          = 10
	localModeSearch     = "search"
	localModeQuery      = "query"
)

type migrateVerifyOptions struct {
	qmdConfig  string
	targets    []string
	queries    string
	sample     int
	seed       uint64
	qmdCommand string
	localMode  string
	noRerank   bool
}

func newMigrateVerifyCmd(a *app) *cobra.Command {
	var o migrateVerifyOptions
	cmd := &cobra.Command{
		Use:   "verify --qmd-config <index.yml>",
		Short: "Check what a server holds against a local QMD setup. Read-only",
		Long: `Compare what was pushed with the local setup, and say plainly what that shows. It changes
nothing on the server or here. It needs a key with both the ingest and read scopes, and costs
one request a file against the key's rate limit.

It compares, per collection:
  - the count of files a plan selects with the count of documents the server indexes
  - each file's state on the server, and its version against the local file's time
  - a sample of documents, fetched and compared with the local text, apart from redaction
  - with --queries, a file of one query a line, the top 10 from local QMD and from the server

It exits 1 if a file is not held, is held at another version, or a sampled document does not
match. Overlap never fails it: local QMD ranks differently and may use another embedder, so
100% is not expected, and no threshold has been measured. It reports the number.

It does not prove every document's text is right: only the sample is fetched. A file removed
from the server's disk behind its ledger shows only if it is in the sample. Documents a session
took over are counted on their state, which says nothing of their text.

Local results come from 'qmd search', which only reads and uses no model, so it is keyword
search compared with the server's hybrid search, and overlap will be lower for it. --local-mode
query compares like with like, but it loads models and writes to the LLM cache in QMD's own index.

Files and queries that need a look are named only in a private local report.`,
		Example: `  memoria migrate verify --qmd-config ~/.config/qmd/index.yml --queries queries.txt`,
		Args:    noArgs,
		RunE:    func(cmd *cobra.Command, _ []string) error { return a.migrateVerify(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringArrayVar(&o.targets, "target", nil, "A collection that went to another name, as local=target; repeatable")
	f.StringVar(&o.queries, "queries", "", "A file of queries, one a line, to compare top results with local QMD")
	f.IntVar(&o.sample, "sample", defaultVerifySample, "Documents to fetch and compare, per collection, at least 1")
	f.Uint64Var(&o.seed, "seed", 0, "Which sample to take, to repeat one. Each run picks its own and prints it")
	f.StringVar(&o.qmdCommand, "qmd-command", "qmd", "The local QMD command")
	f.StringVar(&o.localMode, "local-mode", localModeSearch, "How local QMD searches: search, or query, which loads models and writes to its LLM cache")
	f.BoolVar(&o.noRerank, "no-rerank", false, "Search the server without its reranker")
	return cmd
}

func readQueries(path string) ([]string, error) {
	if path == "" {
		return nil, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, apperr.New(apperr.KindUsage, "the file of queries cannot be read")
	}
	var queries []string
	for _, line := range strings.Split(string(data), "\n") {
		if query := strings.TrimSpace(line); query != "" {
			queries = append(queries, query)
		}
	}
	return queries, nil
}

func (o migrateVerifyOptions) validate() error {
	if o.sample < 1 {
		return apperr.New(apperr.KindUsage, "--sample must be at least 1, as a check that fetches nothing shows nothing about the text")
	}
	if o.localMode != localModeSearch && o.localMode != localModeQuery {
		return apperr.New(apperr.KindUsage, "--local-mode must be search or query")
	}
	return nil
}

func (a *app) migrateVerify(ctx context.Context, o migrateVerifyOptions) error {
	if err := o.validate(); err != nil {
		return err
	}
	jobs, queries, err := a.verifyInputs(o)
	if err != nil {
		return err
	}
	s, err := a.connect()
	if err != nil {
		return err
	}
	counts, err := serverCounts(ctx, s)
	if err != nil {
		return err
	}
	run, cancel := newPushRun(ctx, a, s, migratePushOptions{})
	defer cancel()
	if o.seed == 0 {
		o.seed = rand.Uint64()
	}
	result, problems := run.verifyAll(jobs, verifyInputs{options: o, queries: queries, counts: counts})
	report, err := verifyReportPath(s)
	if err != nil {
		return err
	}
	result.ReportFile = report
	written := writeVerifyReport(report, problems)
	return firstOf(run.failure(), a.presentVerify(ctx, result), written, verifyOutcome(result))
}

func (a *app) verifyInputs(o migrateVerifyOptions) ([]pushJob, []string, error) {
	targets, err := parseTargets(o.targets)
	if err != nil {
		return nil, nil, err
	}
	collections, err := loadCollections(o.qmdConfig, targets)
	if err != nil {
		return nil, nil, err
	}
	queries, err := readQueries(o.queries)
	if err != nil {
		return nil, nil, err
	}
	jobs, err := selectForPush(collections, targets)
	return jobs, queries, err
}

func serverCounts(ctx context.Context, s *session) (map[string]int, error) {
	resp, err := s.client.ListCollectionsWithResponse(ctx)
	if err != nil {
		return nil, apperr.FromTransport(err)
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return nil, err
	}
	if resp.JSON200 == nil {
		return nil, errBadResponse
	}
	counts := make(map[string]int, len(resp.JSON200.Collections))
	for _, collection := range resp.JSON200.Collections {
		counts[collection.Name] = int(collection.Documents)
	}
	return counts, nil
}

func verifyReportPath(s *session) (string, error) {
	path, err := migrate.PushReportPath(transport.Origin(s.base))
	if err != nil {
		return "", err
	}
	return strings.TrimSuffix(path, "-report.json") + "-verify.json", nil
}

func writeVerifyReport(path string, problems []migrate.VerifyProblem) error {
	if problems == nil {
		problems = []migrate.VerifyProblem{}
	}
	data, err := json.MarshalIndent(problems, "", "  ")
	if err != nil {
		return err
	}
	return auth.WritePrivateFile(path, append(data, '\n'))
}

func (a *app) presentVerify(ctx context.Context, result *migrate.VerifyResult) error {
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigrateVerifyMarkdown(w, result) },
		output.Table:    func(w io.Writer) error { return output.MigrateVerifyTable(w, result) },
	})
}

func verifyOutcome(result *migrate.VerifyResult) error {
	problems := 0
	for i := range result.Collections {
		problems += result.Collections[i].Problems()
	}
	if problems == 0 {
		return nil
	}
	return apperr.New(apperr.KindGeneric, "%d thing(s) need a look. The report at %s names them", problems, result.ReportFile)
}

type verifyInputs struct {
	options migrateVerifyOptions
	queries []string
	counts  map[string]int
}

// verifying is one collection's check.
type verifying struct {
	c         *pushCollection
	summary   migrate.VerifySummary
	problems  []migrate.VerifyProblem
	confirmed []string
}

func (v *verifying) note(kind, name, detail string) {
	v.problems = append(v.problems, migrate.VerifyProblem{Collection: v.c.job.collection.Name, Kind: kind, Name: name, Detail: detail})
}

func (r *pushRun) verifyAll(jobs []pushJob, in verifyInputs) (*migrate.VerifyResult, []migrate.VerifyProblem) {
	result := &migrate.VerifyResult{LocalMode: in.options.localMode, Seed: in.options.seed}
	var problems []migrate.VerifyProblem
	for _, job := range jobs {
		v := r.verifyCollection(job, in)
		result.Collections = append(result.Collections, v.summary)
		problems = append(problems, v.problems...)
	}
	return result, problems
}

func (r *pushRun) verifyCollection(job pushJob, in verifyInputs) *verifying {
	v := &verifying{summary: migrate.VerifySummary{
		Name: job.collection.Name, Target: job.target, LocalFiles: job.plan.Files, ServerDocuments: in.counts[job.target], FoldersSkipped: job.plan.Unreadable,
	}}
	v.c = &pushCollection{job: job}
	for _, name := range job.plan.UnreadableNames {
		v.note("skipped_before_checking", name, "")
	}
	root, err := os.OpenRoot(job.collection.Path)
	if err != nil {
		r.stop(apperr.New(apperr.KindUsage, "collection %s: its folder cannot be opened", job.collection.Name))
		return v
	}
	defer func() { _ = root.Close() }()
	v.c.root = root
	r.checkPaths(v)
	r.checkSample(v, in.options)
	v.noteCount()
	r.checkOverlap(v, in)
	return v
}

// noteCount flags counts that differ. A session that took over documents writes its own parts, so its count is not comparable, and
// a file not held is already reported and explains a lower count.
func (v *verifying) noteCount() {
	if v.summary.TakenOver == 0 && v.summary.ServerDocuments != v.summary.LocalFiles-v.summary.NotHeld {
		v.summary.CountDiffers = true
		v.note("count_differs", v.summary.Target, strconv.Itoa(v.summary.LocalFiles)+" local, "+strconv.Itoa(v.summary.ServerDocuments)+" on the server")
	}
}

func (r *pushRun) checkPaths(v *verifying) {
	for i, name := range v.c.job.paths {
		if r.ctx.Err() != nil {
			v.summary.NotChecked += len(v.c.job.paths) - i
			return
		}
		r.checkPath(v, name)
	}
}

func (r *pushRun) checkPath(v *verifying, name string) {
	if v.c.job.secondSpellings[name] {
		v.summary.SecondSpellings++
		v.note("second_spelling", name, "")
		return
	}
	updated, _, err := fileVersion(v.c.root, name)
	if err != nil {
		v.summary.LocalUnreadable++
		v.note("unreadable_here", name, "")
		return
	}
	state, held, answered := r.serverStatus(v.c, name)
	switch {
	case !answered:
		v.summary.NotChecked++
	case state == migrate.StateTakenOver:
		v.summary.TakenOver++
	case state == migrate.StateComplete && held != nil && held.Equal(updated):
		v.summary.Confirmed++
		v.confirmed = append(v.confirmed, name)
	case state == migrate.StateComplete:
		v.summary.VersionDiffers++
		v.note("version_differs", name, "")
	default:
		v.summary.NotHeld++
		v.note("not_held", name, state)
	}
}

// sampleOf picks across the whole list, and differently each run unless given the same seed.
func sampleOf(names []string, size int, seed uint64) []string {
	if len(names) <= size {
		return names
	}
	picked := rand.New(rand.NewPCG(seed, seed)).Perm(len(names))[:size]
	slices.Sort(picked)
	sample := make([]string, 0, size)
	for _, index := range picked {
		sample = append(sample, names[index])
	}
	return sample
}

func (r *pushRun) checkSample(v *verifying, o migrateVerifyOptions) {
	for _, name := range sampleOf(v.confirmed, o.sample, o.seed) {
		if r.ctx.Err() != nil {
			return
		}
		v.summary.Sampled++
		r.compareOne(v, name)
	}
}

func (r *pushRun) compareOne(v *verifying, name string) {
	local, err := v.c.root.ReadFile(filepath.FromSlash(name))
	if err != nil {
		v.summary.SampleFailed++
		v.note("sample_unreadable_here", name, "")
		return
	}
	body, fetched := r.fetchBody(v.c, name)
	if !fetched {
		v.summary.SampleFailed++
		v.note("sample_not_fetched", name, "")
		return
	}
	switch migrate.CompareText(string(local), body) {
	case migrate.TextSame:
		v.summary.SampleSame++
	case migrate.TextRedacted:
		v.summary.SampleRedacted++
	default:
		v.summary.SampleDifferent++
		v.note("sample_differs", name, "")
	}
}

func (r *pushRun) fetchBody(c *pushCollection, name string) (string, bool) {
	var answer *api.GetDocumentResponse
	wanted := true
	// A 404 here is this document, which may not be indexed yet, and not a sign the route is missing.
	status, answered := r.askServer(c, true, func() (*http.Response, []byte, error) {
		got, err := r.session.client.GetDocumentWithResponse(r.ctx, c.job.target+"/"+migrate.ServerPath(name), &api.GetDocumentParams{Body: &wanted})
		if err != nil {
			return nil, nil, err
		}
		answer = got
		return got.HTTPResponse, got.Body, nil
	})
	if !answered || status != http.StatusOK || answer.JSON200 == nil || answer.JSON200.Body == nil {
		return "", false
	}
	return *answer.JSON200.Body, true
}

func (r *pushRun) checkOverlap(v *verifying, in verifyInputs) {
	for i, query := range in.queries {
		if r.ctx.Err() != nil {
			return
		}
		r.compareQuery(v, in.options, i+1, query)
	}
}

func (r *pushRun) compareQuery(v *verifying, o migrateVerifyOptions, number int, query string) {
	overlap := &v.summary.Overlap
	label := "query " + strconv.Itoa(number)
	local, err := r.localHits(v.c, o, query)
	if err != nil {
		overlap.Failed++
		overlap.Queries++
		v.note("local_qmd_failed", label, "")
		return
	}
	if len(local) == 0 {
		overlap.NoLocalResults++
		overlap.Queries++
		return
	}
	remote, answered := r.serverHits(v.c, o, query)
	if !answered {
		overlap.Failed++
		overlap.Queries++
		v.note("server_search_failed", label, "")
		return
	}
	overlap.Add(migrate.Share(local, remote))
}

func (r *pushRun) localHits(c *pushCollection, o migrateVerifyOptions, query string) ([]string, error) {
	out, err := r.a.localQMD(r.ctx, o.qmdCommand, []string{o.localMode, "--json", "-n", strconv.Itoa(overlapTop), "-c", c.job.collection.Name, "--", query})
	if err != nil {
		return nil, err
	}
	return migrate.ParseLocalHits(out, c.job.collection.Name)
}

func (r *pushRun) serverHits(c *pushCollection, o migrateVerifyOptions, query string) ([]string, bool) {
	limit, rerank := int32(overlapTop), !o.noRerank
	collections := []string{c.job.target}
	var answer *api.SearchResponse
	status, answered := r.askServer(c, false, func() (*http.Response, []byte, error) {
		got, err := r.session.client.SearchWithResponse(r.ctx, &api.SearchParams{Query: &query, Collection: &collections, Limit: &limit, Rerank: &rerank})
		if err != nil {
			return nil, nil, err
		}
		answer = got
		return got.HTTPResponse, got.Body, nil
	})
	if !answered || status != http.StatusOK || answer.JSON200 == nil {
		return nil, false
	}
	keys := make([]string, 0, len(answer.JSON200.Results))
	for _, hit := range answer.JSON200.Results {
		keys = append(keys, migrate.HitKey(hit.Path, c.job.target))
	}
	return keys, true
}

// runLocalQMD runs the local command. Its stderr is left behind, as a failure there could quote a path or a query.
func runLocalQMD(ctx context.Context, command string, args []string) ([]byte, error) {
	run, err := localCommand(ctx, command, args)
	if err != nil {
		return nil, err
	}
	var stdout bytes.Buffer
	run.Stdout = &stdout
	if err := run.Run(); err != nil {
		return nil, err
	}
	return stdout.Bytes(), nil
}

// cmdMetaCharacters are what cmd.exe gives a meaning to, even inside quotes.
const cmdMetaCharacters = "()[]%!^\"`<>&|;, *?"

// EscapeForCmd makes an argument one word that cmd.exe passes on untouched, the way cross-spawn does for the same problem. It
// escapes twice, as npm's .cmd shims hand their arguments to cmd.exe a second time through %*.
func EscapeForCmd(arg string) string {
	var quoted strings.Builder
	quoted.WriteByte('"')
	backslashes := 0
	for _, r := range arg {
		if r == '\\' {
			backslashes++
			continue
		}
		count := backslashes
		if r == '"' {
			count = backslashes*2 + 1
		}
		quoted.WriteString(strings.Repeat(`\`, count))
		backslashes = 0
		quoted.WriteRune(r)
	}
	quoted.WriteString(strings.Repeat(`\`, backslashes*2))
	quoted.WriteByte('"')
	return caretEscape(caretEscape(quoted.String()))
}

func caretEscape(text string) string {
	var out strings.Builder
	for _, r := range text {
		if strings.ContainsRune(cmdMetaCharacters, r) {
			out.WriteByte('^')
		}
		out.WriteRune(r)
	}
	return out.String()
}

// CmdLine is the line to give cmd.exe to run a batch file with these arguments, none of them interpreted.
func CmdLine(program string, args []string) string {
	parts := make([]string, 0, len(args)+1)
	parts = append(parts, caretEscape(`"`+program+`"`))
	for _, arg := range args {
		parts = append(parts, EscapeForCmd(arg))
	}
	return `/d /s /c "` + strings.Join(parts, " ") + `"`
}
