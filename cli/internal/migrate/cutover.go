package migrate

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const (
	cutoverDir   = "migrate-cutover"
	manifestName = "manifest.json"
	mcpOld       = "qmd"
	mcpNew       = "memoria"

	codexNewEntry   = "[mcp_servers.memoria]\ncommand = \"memoria\"\nargs = [\"mcp\"]\n"
	claudeNewEntry  = `{"type":"stdio","command":"memoria","args":["mcp"]}`
	alreadyNote     = ": already uses memoria"
	notFoundNote    = ": has no qmd entry"
	maxLinkDepth    = 5
	permissionsKey  = "permissions"
	allowKey        = "allow"
	enabledKey      = "enabledMcpjsonServers"
	serversKey      = "mcpServers"
	notChangedSufix = ", so nothing was changed"
)

// The kinds of file a cutover edits, which say how to undo just its own change.
const (
	kindMCPJSON  = "mcp_json"
	kindCodex    = "codex_toml"
	kindSettings = "settings"
	kindSkill    = "skill"
)

// ClientPaths is where the clients keep what a cutover changes.
type ClientPaths struct {
	Home                string
	MCPConfig           string
	ClaudeConfig        string
	ClaudeSettings      string
	ClaudeLocalSettings string
	ClaudeSkills        string
	CodexConfig         string
	CodexSkills         string
	AgentsSkills        string
}

// PathsUnder works the paths out from a home folder, and each client's own setting for where it keeps its files.
func PathsUnder(home, claudeDir, codexHome string) ClientPaths {
	claudeConfig := filepath.Join(home, ".claude.json")
	if claudeDir != "" {
		claudeConfig = filepath.Join(claudeDir, ".claude.json")
	} else {
		claudeDir = filepath.Join(home, ".claude")
	}
	if codexHome == "" {
		codexHome = filepath.Join(home, ".codex")
	}
	return ClientPaths{
		Home:                home,
		MCPConfig:           filepath.Join(home, ".mcp.json"),
		ClaudeConfig:        claudeConfig,
		ClaudeSettings:      filepath.Join(claudeDir, "settings.json"),
		ClaudeLocalSettings: filepath.Join(claudeDir, "settings.local.json"),
		ClaudeSkills:        filepath.Join(claudeDir, "skills"),
		CodexConfig:         filepath.Join(codexHome, "config.toml"),
		CodexSkills:         filepath.Join(codexHome, "skills"),
		AgentsSkills:        filepath.Join(home, ".agents", "skills"),
	}
}

// FileEdit is one file a cutover would change, what to show for it, and what undoing it means. Summary never holds a value from an entry.
type FileEdit struct {
	Path    string
	Kind    string
	Existed bool
	Before  []byte
	After   []byte
	Summary []string

	RemovedServer bool
	AddedServer   bool
	AddedRules    []string
	AddedEnabled  bool
}

// CutoverOptions is what a cutover was asked to do beyond the MCP swap.
type CutoverOptions struct {
	Paths ClientPaths
	Skill []byte
}

// CutoverPlan is what a cutover would do. Found says qmd or memoria turned up in at least one MCP config: without that there is no
// swap, and the owner must hear so rather than read success.
type CutoverPlan struct {
	Edits []FileEdit
	Notes []string
	Found bool
	// Searched is where qmd was looked for, for saying so when it is nowhere.
	Searched []string
}

func readIfPresent(path string) ([]byte, bool, error) {
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, false, nil
	}
	if err != nil {
		return nil, false, fmt.Errorf("%s cannot be read", path)
	}
	return data, true, nil
}

type planStep func() (*FileEdit, string, error)

// PlanCutover works out every file's change, and writes nothing. Each MCP config holding qmd has it swapped where it is defined.
func PlanCutover(o CutoverOptions) (*CutoverPlan, error) {
	paths := o.Paths
	plan := &CutoverPlan{Searched: []string{paths.MCPConfig, paths.ClaudeConfig, paths.CodexConfig}}
	for _, step := range []planStep{
		func() (*FileEdit, string, error) { return planMCPJSON(paths.MCPConfig) },
		func() (*FileEdit, string, error) { return planMCPJSON(paths.ClaudeConfig) },
		func() (*FileEdit, string, error) { return planCodex(paths.CodexConfig) },
	} {
		edit, note, err := step()
		if err != nil {
			return nil, err
		}
		plan.Found = plan.Found || edit != nil || strings.HasSuffix(note, alreadyNote)
		plan.add(edit, note)
	}
	if err := plan.addRest(o); err != nil {
		return nil, err
	}
	return plan, plan.refuseUnsafe(paths)
}

func (p *CutoverPlan) add(edit *FileEdit, note string) {
	if edit != nil {
		p.Edits = append(p.Edits, *edit)
	}
	if note != "" {
		p.Notes = append(p.Notes, note)
	}
}

func (p *CutoverPlan) addRest(o CutoverOptions) error {
	paths := o.Paths
	claude := anyPresent(paths.MCPConfig, paths.ClaudeConfig, paths.ClaudeSkills)
	codex := anyPresent(paths.CodexConfig, paths.CodexSkills)
	agents := anyPresent(paths.AgentsSkills)
	for _, step := range []planStep{
		func() (*FileEdit, string, error) { return planPermissions(paths, paths.ClaudeSettings) },
		func() (*FileEdit, string, error) { return planPermissions(paths, paths.ClaudeLocalSettings) },
		func() (*FileEdit, string, error) { return planSkill(paths, paths.ClaudeSkills, claude, o.Skill) },
		func() (*FileEdit, string, error) { return planSkill(paths, paths.CodexSkills, codex, o.Skill) },
		func() (*FileEdit, string, error) { return planSkill(paths, paths.AgentsSkills, agents, o.Skill) },
		func() (*FileEdit, string, error) { return nil, hookNote(paths.ClaudeSettings), nil },
	} {
		edit, note, err := step()
		if err != nil {
			return err
		}
		p.add(edit, note)
	}
	return nil
}

// refuseUnsafe stops before anything is written if a file could not be replaced safely.
func (p *CutoverPlan) refuseUnsafe(paths ClientPaths) error {
	for i := range p.Edits {
		if err := refuseUnsafe(paths.Home, p.Edits[i].Path); err != nil {
			return err
		}
	}
	return nil
}

func anyPresent(paths ...string) bool {
	for _, path := range paths {
		if _, err := os.Lstat(path); err == nil {
			return true
		}
	}
	return false
}

// linkOnPath finds a link at a path or a folder above it, up to the home folder. Writing there would change whatever it points
// to, such as a repository, and replacing it would leave a regular file where the link was.
func linkOnPath(home, path string) bool {
	current := path
	for range maxLinkDepth {
		if current == home || current == filepath.Dir(current) {
			return false
		}
		if info, err := os.Lstat(current); err == nil && info.Mode()&os.ModeType != 0 && !info.IsDir() {
			return true
		}
		current = filepath.Dir(current)
	}
	return false
}

func refuseUnsafe(home, path string) error {
	if linkOnPath(home, path) {
		return fmt.Errorf("%s is a link, or is under one, so nothing was changed. Change what it points to by hand", path)
	}
	if info, err := os.Stat(path); err == nil && info.Mode().Perm()&0o200 == 0 {
		return fmt.Errorf("%s is read-only%s", path, notChangedSufix)
	}
	return nil
}

func planMCPJSON(path string) (*FileEdit, string, error) {
	before, existed, err := readIfPresent(path)
	if err != nil {
		return nil, "", err
	}
	if !existed {
		return nil, path + ": not found", nil
	}
	top, err := parseObject(before)
	if err != nil {
		return nil, "", fmt.Errorf("%s is not a valid JSON object%s", path, notChangedSufix)
	}
	servers, err := top.childObject(serversKey)
	if err != nil {
		return nil, "", fmt.Errorf("%s has an mcpServers that is not an object%s", path, notChangedSufix)
	}
	if servers == nil {
		return nil, path + notFoundNote + projectNote(top), nil
	}
	return swapMCPJSON(path, before, top, servers)
}

// projectNote says when one project's own entry names qmd. A cutover changes user-level entries only.
func projectNote(top *jsonObject) string {
	projects, err := top.childObject("projects")
	if err != nil || projects == nil {
		return ""
	}
	count := 0
	for _, raw := range projects.values {
		project, err := parseObject(raw)
		if err != nil {
			continue
		}
		if servers, err := project.childObject(serversKey); err == nil && servers != nil && servers.index(mcpOld) >= 0 {
			count++
		}
	}
	if count == 0 {
		return ""
	}
	return fmt.Sprintf(". %d project(s) name qmd in their own settings, and those were left alone", count)
}

func swapMCPJSON(path string, before []byte, top, servers *jsonObject) (*FileEdit, string, error) {
	old, hasOld := servers.get(mcpOld)
	hasNew := servers.index(mcpNew) >= 0
	note := projectNote(top)
	if !hasOld && hasNew {
		return nil, path + note + alreadyNote, nil
	}
	if !hasOld {
		return nil, path + notFoundNote + note, nil
	}
	servers.remove(mcpOld)
	edit := FileEdit{Path: path, Kind: kindMCPJSON, Existed: true, Before: before, RemovedServer: true, Summary: []string{"- mcpServers.qmd " + entrySummary(old)}}
	if !hasNew {
		servers.set(mcpNew, json.RawMessage(claudeNewEntry))
		edit.AddedServer = true
		edit.Summary = append(edit.Summary, `+ mcpServers.memoria, which runs "memoria"`)
	}
	top.set(serversKey, servers.raw())
	after, err := top.file()
	if err != nil {
		return nil, "", err
	}
	edit.After = after
	return &edit, strings.TrimPrefix(note, ". "), nil
}

// entrySummary says what an entry runs and nothing it was given. Arguments, an env and a URL's user or query can all hold a key.
func entrySummary(raw json.RawMessage) string {
	var entry struct {
		Command string `json:"command"`
		URL     string `json:"url"`
	}
	if err := json.Unmarshal(raw, &entry); err != nil {
		return "(an entry this cannot read)"
	}
	if entry.Command != "" {
		return fmt.Sprintf("(runs %q; its arguments and other fields are not shown)", baseName(entry.Command))
	}
	if parsed, err := url.Parse(entry.URL); err == nil && parsed.Host != "" {
		return fmt.Sprintf("(at %s; the rest is not shown)", parsed.Host)
	}
	return "(fields not shown)"
}

func baseName(path string) string {
	return filepath.Base(strings.ReplaceAll(path, `\`, "/"))
}

func planCodex(path string) (*FileEdit, string, error) {
	before, existed, err := readIfPresent(path)
	if err != nil {
		return nil, "", err
	}
	if !existed {
		return nil, path + ": not found, so Codex is left alone", nil
	}
	table, err := tomlTable(before)
	if err != nil {
		return nil, "", fmt.Errorf("%s is not valid TOML%s", path, notChangedSufix)
	}
	servers, err := tomlServers(table)
	if err != nil {
		return nil, "", fmt.Errorf("%s has an mcp_servers that is not a table%s", path, notChangedSufix)
	}
	_, hasOld := servers[mcpOld]
	_, hasNew := servers[mcpNew]
	switch {
	case !hasOld && hasNew:
		return nil, path + alreadyNote, nil
	case !hasOld:
		return nil, path + notFoundNote + ", so memoria was not added there. To use it from Codex, add: " + strings.ReplaceAll(strings.TrimSpace(codexNewEntry), "\n", " ; "), nil
	}
	return swapCodex(path, before, table, hasNew)
}

func swapCodex(path string, before []byte, table map[string]any, hasNew bool) (*FileEdit, string, error) {
	kept, _ := splitServerTable(string(before), mcpOld)
	edit := FileEdit{Path: path, Kind: kindCodex, Existed: true, Before: before, RemovedServer: true, Summary: []string{"- [mcp_servers.qmd], and any table under it (its fields are not shown)"}}
	if !hasNew {
		kept = appendTable(kept, codexNewEntry)
		edit.AddedServer = true
		edit.Summary = append(edit.Summary, `+ [mcp_servers.memoria], which runs "memoria"`)
	}
	edit.After = []byte(kept)
	after, err := tomlTable(edit.After)
	if err != nil || !sameExcept(table, after, mcpOld, mcpNew) || hasServer(after, mcpOld) {
		return nil, "", fmt.Errorf("%s defines qmd in a form this cannot edit safely%s. Change it by hand", path, notChangedSufix)
	}
	return &edit, "", nil
}

func hasServer(table map[string]any, name string) bool {
	servers, err := tomlServers(table)
	if err != nil {
		return false
	}
	_, found := servers[name]
	return found
}

// memoriaPermission is the rule that allows what a qmd rule allows, as the two servers name their tools differently. Empty for
// anything that is not a rule for a qmd tool this knows.
func memoriaPermission(rule string) string {
	switch rule {
	case "mcp__qmd":
		return "mcp__memoria"
	case "mcp__qmd__*":
		return "mcp__memoria__*"
	case "mcp__qmd__query":
		return "mcp__memoria__search"
	case "mcp__qmd__get":
		return "mcp__memoria__get_document"
	case "mcp__qmd__multi_get":
		return "mcp__memoria__get_documents"
	case "mcp__qmd__status":
		return "mcp__memoria__status"
	}
	return ""
}

// planPermissions lets memoria's tools do what qmd's could. The qmd rules stay, as they harm nothing, and a rollback removes what was added.
func planPermissions(paths ClientPaths, path string) (*FileEdit, string, error) {
	before, existed, err := readIfPresent(path)
	if err != nil || !existed {
		return nil, "", err
	}
	top, err := parseObject(before)
	if err != nil {
		return nil, "", fmt.Errorf("%s is not a valid JSON object%s", path, notChangedSufix)
	}
	edit := FileEdit{Path: path, Kind: kindSettings, Existed: true, Before: before}
	left, err := allowMemoria(top, &edit)
	if err != nil {
		return nil, "", fmt.Errorf("%s has a permissions block of the wrong shape%s", path, notChangedSufix)
	}
	if err := enableMemoria(top, &edit); err != nil {
		return nil, "", fmt.Errorf("%s has an enabledMcpjsonServers that is not a list%s", path, notChangedSufix)
	}
	return finishPermissions(paths, path, top, &edit, left)
}

func finishPermissions(paths ClientPaths, path string, top *jsonObject, edit *FileEdit, left int) (*FileEdit, string, error) {
	note := ""
	if left > 0 {
		note = fmt.Sprintf("%s: %d rule(s) naming qmd were left in place, as they harm nothing", path, left)
	}
	if len(edit.Summary) == 0 {
		return nil, note, nil
	}
	if linkOnPath(paths.Home, path) {
		return nil, path + ": is a link, so it was left alone. Allow memoria's tools there by hand: " + strings.Join(edit.Summary, ", "), nil
	}
	after, err := top.file()
	if err != nil {
		return nil, "", err
	}
	edit.After = after
	return edit, note, nil
}

func allowMemoria(top *jsonObject, edit *FileEdit) (int, error) {
	permissions, err := top.childObject(permissionsKey)
	if err != nil || permissions == nil {
		return 0, err
	}
	allow, err := permissions.listOf(allowKey)
	if err != nil {
		return 0, err
	}
	rules := listStrings(allow)
	left := 0
	for _, rule := range rules {
		if strings.Contains(rule, mcpOld) {
			left++
		}
		added := memoriaPermission(rule)
		if added == "" || containsString(rules, added) || containsString(edit.AddedRules, added) {
			continue
		}
		allow = append(allow, encodeString(added))
		edit.AddedRules = append(edit.AddedRules, added)
		edit.Summary = append(edit.Summary, "+ permissions.allow "+added)
	}
	if len(edit.AddedRules) > 0 {
		permissions.set(allowKey, encodeList(allow))
		top.set(permissionsKey, permissions.raw())
	}
	return left, nil
}

// enableMemoria approves memoria where qmd was approved, as Claude Code asks before using a server from a .mcp.json file.
func enableMemoria(top *jsonObject, edit *FileEdit) error {
	enabled, err := top.listOf(enabledKey)
	if err != nil {
		return err
	}
	names := listStrings(enabled)
	if containsString(names, mcpOld) && !containsString(names, mcpNew) {
		top.set(enabledKey, encodeList(append(enabled, encodeString(mcpNew))))
		edit.AddedEnabled = true
		edit.Summary = append(edit.Summary, "+ enabledMcpjsonServers memoria")
	}
	return nil
}

// hookNote shows the hooks already there. A cutover adds none: pushing sessions is a setting of the existing export hook, and a
// second one would export twice. It only reads, so a file of a shape it cannot follow just gets said.
func hookNote(path string) string {
	data, existed, err := readIfPresent(path)
	if err != nil || !existed {
		return ""
	}
	var settings struct {
		Hooks struct {
			SessionEnd []struct {
				Hooks []struct {
					Command string `json:"command"`
				} `json:"hooks"`
			} `json:"SessionEnd"`
		} `json:"hooks"`
	}
	if json.Unmarshal(data, &settings) != nil {
		return path + ": its hooks could not be read, so any SessionEnd hook there was not looked at"
	}
	var commands []string
	for _, group := range settings.Hooks.SessionEnd {
		for _, hook := range group.Hooks {
			commands = append(commands, commandName(hook.Command))
		}
	}
	if len(commands) == 0 {
		return path + ": no SessionEnd hook is set, so nothing pushes sessions yet. See docs/migration.md for what one needs"
	}
	return fmt.Sprintf("%s: a SessionEnd hook is already set (%s) and was left alone. Pushing sessions is that hook's own setting. See docs/migration.md for what it needs", path, strings.Join(commands, ", "))
}

// commandName shows what a hook runs and none of its arguments, which can hold anything: the program, and a script it is given.
func commandName(command string) string {
	fields := strings.Fields(command)
	if len(fields) == 0 {
		return "an empty command"
	}
	name := baseName(strings.Trim(fields[0], `"'`))
	for _, field := range fields[1:] {
		trimmed := strings.Trim(field, `"'`)
		extension := strings.ToLower(filepath.Ext(trimmed))
		if extension == ".py" || extension == ".sh" || extension == ".js" || extension == ".ps1" {
			return name + " " + baseName(trimmed)
		}
	}
	return name
}

// planSkill installs into a client that is there, and not through a link, which could write into a repository. Without a skill
// given, it says so.
func planSkill(paths ClientPaths, skills string, present bool, skill []byte) (*FileEdit, string, error) {
	if !present {
		return nil, "", nil
	}
	if skill == nil {
		return nil, skills + ": the skill was not installed, as no --skill was given", nil
	}
	path := filepath.Join(skills, "memoria", "SKILL.md")
	if linkOnPath(paths.Home, path) {
		return nil, path + ": is a link, or is under one, so the skill was not installed there. Add it to what it points to by hand", nil
	}
	before, existed, err := readIfPresent(path)
	if err != nil {
		return nil, "", err
	}
	if existed && bytes.Equal(before, skill) {
		return nil, path + ": already installed", nil
	}
	return &FileEdit{Path: path, Kind: kindSkill, Existed: existed, Before: before, After: skill, Summary: []string{"+ the memoria skill"}}, "", nil
}

// CutoverEntry is one file a cutover changed. Backup is empty for a file that did not exist.
type CutoverEntry struct {
	Path    string `json:"path"`
	Kind    string `json:"kind"`
	Existed bool   `json:"existed"`
	Backup  string `json:"backup,omitempty"`
	After   string `json:"afterSha256"`
	// EditedSince means someone changed the file between two cutovers, so its backup is no longer the whole story.
	EditedSince   bool     `json:"editedSince,omitempty"`
	RemovedServer bool     `json:"removedServer,omitempty"`
	AddedServer   bool     `json:"addedServer,omitempty"`
	AddedRules    []string `json:"addedRules,omitempty"`
	AddedEnabled  bool     `json:"addedEnabled,omitempty"`
	// CreatedFolders is what the cutover made for this file, deepest first.
	CreatedFolders []string `json:"createdFolders,omitempty"`
}

// CutoverManifest is one cutover's record. Once rolled back it is kept and never reused, so nothing overwrites an original.
type CutoverManifest struct {
	RolledBack bool           `json:"rolledBack"`
	Entries    []CutoverEntry `json:"entries"`

	home string
	dir  string
}

func hash(data []byte) string {
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

func cutoverRoot() (string, error) {
	dir, err := auth.ConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, cutoverDir), nil
}

// loadGeneration reads one numbered manifest. False means there is none by that number.
func loadGeneration(root string, number int) (*CutoverManifest, bool, error) {
	dir := filepath.Join(root, strconv.Itoa(number))
	data, err := auth.ReadPrivateFile(filepath.Join(dir, manifestName))
	if errors.Is(err, os.ErrNotExist) {
		return nil, false, nil
	}
	if err != nil {
		return nil, false, err
	}
	manifest := &CutoverManifest{dir: dir}
	if err := json.Unmarshal(data, manifest); err != nil {
		return nil, false, fmt.Errorf("the cutover record at %s is damaged%s", dir, notChangedSufix)
	}
	return manifest, true, nil
}

// latestManifest is the newest record, and the number the next one would take.
func latestManifest() (*CutoverManifest, int, error) {
	root, err := cutoverRoot()
	if err != nil {
		return nil, 0, err
	}
	var latest *CutoverManifest
	for number := 1; ; number++ {
		manifest, found, err := loadGeneration(root, number)
		if err != nil {
			return nil, 0, err
		}
		if !found {
			return latest, number, nil
		}
		latest = manifest
	}
}

// ActiveManifest is the cutover a rollback would undo, or nil.
func ActiveManifest(home string) (*CutoverManifest, error) {
	latest, _, err := latestManifest()
	if err != nil || latest == nil || latest.RolledBack {
		return nil, err
	}
	latest.home = home
	return latest, nil
}

func (m *CutoverManifest) save() error {
	data, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	return auth.WritePrivateFile(filepath.Join(m.dir, manifestName), append(data, '\n'))
}

func (m *CutoverManifest) entry(path string) *CutoverEntry {
	for i := range m.Entries {
		if m.Entries[i].Path == path {
			return &m.Entries[i]
		}
	}
	return nil
}

// note records a file the first time this cutover changes it, with its backup. A later cutover keeps that backup, as it holds the
// original, and notes if the file was edited in between, so a rollback will not silently lose that edit.
func (m *CutoverManifest) note(edit FileEdit) error {
	entry := m.entry(edit.Path)
	if entry == nil {
		fresh := CutoverEntry{Path: edit.Path, Kind: edit.Kind, Existed: edit.Existed, CreatedFolders: missingFolders(edit.Path)}
		if edit.Existed {
			fresh.Backup = strconv.Itoa(len(m.Entries)) + ".bak"
			if err := auth.WritePrivateFile(filepath.Join(m.dir, fresh.Backup), edit.Before); err != nil {
				return fmt.Errorf("the backup of %s could not be written%s", edit.Path, notChangedSufix)
			}
		}
		m.Entries = append(m.Entries, fresh)
		entry = &m.Entries[len(m.Entries)-1]
	} else if hash(edit.Before) != entry.After {
		entry.EditedSince = true
	}
	entry.After = hash(edit.After)
	entry.RemovedServer = entry.RemovedServer || edit.RemovedServer
	entry.AddedServer = entry.AddedServer || edit.AddedServer
	entry.AddedEnabled = entry.AddedEnabled || edit.AddedEnabled
	entry.AddedRules = append(entry.AddedRules, edit.AddedRules...)
	return nil
}

// missingFolders is the folders above a file that do not exist yet, deepest first.
func missingFolders(path string) []string {
	var missing []string
	for current := filepath.Dir(path); current != filepath.Dir(current); current = filepath.Dir(current) {
		if _, err := os.Stat(current); err == nil || !errors.Is(err, os.ErrNotExist) {
			break
		}
		missing = append(missing, current)
	}
	return missing
}

// stillAsShown stops if a file changed after it was shown. A client can write its config while a person reads the prompt.
func stillAsShown(edit FileEdit) error {
	current, present, err := readIfPresent(edit.Path)
	if err != nil {
		return err
	}
	if present != edit.Existed || !bytes.Equal(current, edit.Before) {
		return fmt.Errorf("%s changed after it was shown%s. Run this again", edit.Path, notChangedSufix)
	}
	return nil
}

// ApplyCutover backs up everything first and only then changes anything.
func ApplyCutover(edits []FileEdit) error {
	for _, edit := range edits {
		if err := stillAsShown(edit); err != nil {
			return err
		}
	}
	manifest, err := manifestToExtend()
	if err != nil {
		return err
	}
	for _, edit := range edits {
		if err := manifest.note(edit); err != nil {
			return err
		}
	}
	if err := manifest.save(); err != nil {
		return err
	}
	for _, edit := range edits {
		if err := writeAtomic(edit.Path, edit.After); err != nil {
			return fmt.Errorf("%s could not be written. Run rollback to restore what changed", edit.Path)
		}
	}
	return nil
}

func manifestToExtend() (*CutoverManifest, error) {
	latest, next, err := latestManifest()
	if err != nil {
		return nil, err
	}
	if latest != nil && !latest.RolledBack {
		return latest, nil
	}
	root, err := cutoverRoot()
	if err != nil {
		return nil, err
	}
	return &CutoverManifest{dir: filepath.Join(root, strconv.Itoa(next))}, nil
}

// writeAtomic keeps a file's mode, and replaces it whole, so an interruption leaves one version or the other. Any failure stops
// there, before the rename, and removes the temporary file by its exact path.
func writeAtomic(path string, data []byte) error {
	mode := os.FileMode(0o644)
	if info, err := os.Stat(path); err == nil {
		mode = info.Mode().Perm()
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	temp, err := os.CreateTemp(filepath.Dir(path), ".memoria-cutover-*")
	if err != nil {
		return err
	}
	name := temp.Name()
	if err := fillFile(temp, name, data, mode); err != nil {
		_ = os.Remove(name)
		return err
	}
	if err := os.Rename(name, path); err != nil {
		_ = os.Remove(name)
		return err
	}
	return nil
}

// tempFile is what writing a temporary file needs. Any of these can fail on a full or failing disk.
type tempFile interface {
	Write(data []byte) (int, error)
	Sync() error
	Close() error
}

// fillFile is a variable so a test can have a write fail, which a real disk will not do on request.
var fillFile = func(temp *os.File, name string, data []byte, mode os.FileMode) error {
	return fillTemp(temp, name, data, mode)
}

func fillTemp(temp tempFile, name string, data []byte, mode os.FileMode) error {
	if _, err := temp.Write(data); err != nil {
		_ = temp.Close()
		return err
	}
	if err := temp.Sync(); err != nil {
		_ = temp.Close()
		return err
	}
	if err := temp.Close(); err != nil {
		return err
	}
	return os.Chmod(name, mode)
}

// How a rollback treats one file.
const (
	// RestoreWhole puts the backup back: the file is exactly what the cutover wrote, so nothing is lost.
	RestoreWhole = iota
	// UndoOwnChange reverses only what the cutover did, as something else has changed the file since and that stays.
	UndoOwnChange
	// RemoveCreated removes a file the cutover created.
	RemoveCreated
)

// RollbackStep is one file a rollback would put back.
type RollbackStep struct {
	Entry  CutoverEntry
	Action int
	// Changed means the cutover's change cannot be undone on its own, so going back would lose an edit. It needs saying yes to.
	Changed bool

	current string
	content []byte
}

// PlanRollback works out what a rollback would do, and writes nothing. A file already back to its original needs nothing.
func PlanRollback(m *CutoverManifest) ([]RollbackStep, error) {
	var steps []RollbackStep
	for _, entry := range m.Entries {
		if err := refuseUnsafe(m.home, entry.Path); err != nil {
			return nil, err
		}
		step, needed, err := planRestore(m, entry)
		if err != nil {
			return nil, err
		}
		if needed {
			steps = append(steps, step)
		}
	}
	return steps, nil
}

func planRestore(m *CutoverManifest, entry CutoverEntry) (RollbackStep, bool, error) {
	current, present, err := readIfPresent(entry.Path)
	if err != nil {
		return RollbackStep{}, false, err
	}
	step := RollbackStep{Entry: entry, current: hash(current)}
	if !entry.Existed {
		step.Action = RemoveCreated
		step.Changed = present && hash(current) != entry.After
		return step, present, nil
	}
	backup, err := auth.ReadPrivateFile(filepath.Join(m.dir, entry.Backup))
	if err != nil {
		return step, false, fmt.Errorf("the backup of %s cannot be read%s", entry.Path, notChangedSufix)
	}
	return planExisting(step, current, present, backup)
}

func planExisting(step RollbackStep, current []byte, present bool, backup []byte) (RollbackStep, bool, error) {
	step.content = backup
	switch {
	case present && bytes.Equal(current, backup):
		return step, false, nil
	case present && hash(current) == step.Entry.After && !step.Entry.EditedSince:
		return step, true, nil
	case !present:
		step.Changed = true
		return step, true, nil
	}
	undone, err := undoChange(step.Entry, current, backup)
	if err != nil {
		step.Changed = true
		return step, true, nil
	}
	if bytes.Equal(undone, current) {
		return step, false, nil
	}
	step.Action, step.content = UndoOwnChange, undone
	return step, true, nil
}

// undoChange reverses the cutover's own change in a file something else has changed since. An error means it cannot.
func undoChange(entry CutoverEntry, current, backup []byte) ([]byte, error) {
	switch entry.Kind {
	case kindMCPJSON:
		return undoMCPJSON(entry, current, backup)
	case kindCodex:
		return undoCodex(entry, current, backup)
	case kindSettings:
		return undoSettings(entry, current)
	}
	return nil, errors.New("no way to undo this alone")
}

// serversIn reads a config and its servers, which an undo needs both of. An error means it cannot.
func serversIn(data []byte) (*jsonObject, *jsonObject, error) {
	top, err := parseObject(data)
	if err != nil {
		return nil, nil, err
	}
	servers, err := top.childObject(serversKey)
	if err != nil || servers == nil {
		return nil, nil, errors.New("it has no servers")
	}
	return top, servers, nil
}

func undoMCPJSON(entry CutoverEntry, current, backup []byte) ([]byte, error) {
	top, servers, err := serversIn(current)
	if err != nil {
		return nil, err
	}
	_, originalServers, err := serversIn(backup)
	if err != nil {
		return nil, err
	}
	if entry.AddedServer {
		servers.remove(mcpNew)
	}
	if old, had := originalServers.get(mcpOld); had && entry.RemovedServer && servers.index(mcpOld) < 0 {
		servers.set(mcpOld, old)
	}
	top.set(serversKey, servers.raw())
	return top.file()
}

func undoCodex(entry CutoverEntry, current, backup []byte) ([]byte, error) {
	if _, err := tomlTable(current); err != nil {
		return nil, err
	}
	text := string(current)
	if entry.AddedServer {
		text, _ = splitServerTable(text, mcpNew)
		text = strings.TrimRight(text, "\r\n") + newlineOf(text)
	}
	_, old := splitServerTable(string(backup), mcpOld)
	if entry.RemovedServer && old != "" {
		text = appendTable(text, old)
	}
	if _, err := tomlTable([]byte(text)); err != nil {
		return nil, err
	}
	return []byte(text), nil
}

func undoSettings(entry CutoverEntry, current []byte) ([]byte, error) {
	top, err := parseObject(current)
	if err != nil {
		return nil, err
	}
	permissions, err := top.childObject(permissionsKey)
	if err != nil {
		return nil, err
	}
	if permissions != nil && len(entry.AddedRules) > 0 {
		allow, err := permissions.listOf(allowKey)
		if err != nil {
			return nil, err
		}
		permissions.set(allowKey, encodeList(withoutStrings(allow, entry.AddedRules)))
		top.set(permissionsKey, permissions.raw())
	}
	if entry.AddedEnabled {
		enabled, err := top.listOf(enabledKey)
		if err != nil {
			return nil, err
		}
		top.set(enabledKey, encodeList(withoutStrings(enabled, []string{mcpNew})))
	}
	return top.file()
}

// ApplyRollback puts each file back, and marks the record done. A file the cutover created is removed by its exact recorded
// path, and only that file. A folder it created goes too, and only if nothing else is in it.
func ApplyRollback(m *CutoverManifest, steps []RollbackStep) error {
	for _, step := range steps {
		current, _, err := readIfPresent(step.Entry.Path)
		if err != nil {
			return err
		}
		if hash(current) != step.current {
			return fmt.Errorf("%s changed after it was shown. Run this again to finish", step.Entry.Path)
		}
		if err := restore(step); err != nil {
			return fmt.Errorf("%s could not be restored. Run rollback again to finish", step.Entry.Path)
		}
	}
	for _, entry := range m.Entries {
		for _, folder := range entry.CreatedFolders {
			_ = os.Remove(folder)
		}
	}
	m.RolledBack = true
	return m.save()
}

func restore(step RollbackStep) error {
	if step.Action != RemoveCreated {
		return writeAtomic(step.Entry.Path, step.content)
	}
	if err := os.Remove(step.Entry.Path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}
