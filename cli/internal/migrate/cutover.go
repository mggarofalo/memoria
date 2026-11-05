package migrate

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const (
	cutoverDir      = "migrate-cutover"
	manifestName    = "manifest.json"
	mcpOld          = "qmd"
	mcpNew          = "memoria"
	codexOldSection = "mcp_servers.qmd"
	codexNewEntry   = "[mcp_servers.memoria]\ncommand = \"memoria\"\nargs = [\"mcp\"]\n"
)

// ClientPaths is where the clients keep what a cutover changes.
type ClientPaths struct {
	ClaudeConfig   string
	ClaudeSettings string
	ClaudeSkills   string
	CodexConfig    string
	CodexSkills    string
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
		ClaudeConfig:   claudeConfig,
		ClaudeSettings: filepath.Join(claudeDir, "settings.json"),
		ClaudeSkills:   filepath.Join(claudeDir, "skills"),
		CodexConfig:    filepath.Join(codexHome, "config.toml"),
		CodexSkills:    filepath.Join(codexHome, "skills"),
	}
}

// FileEdit is one file a cutover would change, and what to show for it. Summary never holds a value from an env or headers block.
type FileEdit struct {
	Path    string
	Existed bool
	Before  []byte
	After   []byte
	Summary []string
}

// CutoverOptions is what a cutover was asked to do beyond the MCP swap.
type CutoverOptions struct {
	Paths       ClientPaths
	Skill       []byte
	HookCommand string
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

// PlanCutover works out every file's change, and writes nothing. A client with no config file is left alone.
func PlanCutover(o CutoverOptions) ([]FileEdit, []string, error) {
	var edits []FileEdit
	var notes []string
	steps := []func() (*FileEdit, string, error){
		func() (*FileEdit, string, error) { return planClaudeMCP(o.Paths.ClaudeConfig) },
		func() (*FileEdit, string, error) { return planCodexMCP(o.Paths.CodexConfig) },
		func() (*FileEdit, string, error) { return planHook(o.Paths.ClaudeSettings, o.HookCommand) },
		func() (*FileEdit, string, error) {
			return planSkill(o.Paths.ClaudeConfig, o.Paths.ClaudeSkills, o.Skill)
		},
		func() (*FileEdit, string, error) { return planSkill(o.Paths.CodexConfig, o.Paths.CodexSkills, o.Skill) },
	}
	for _, step := range steps {
		edit, note, err := step()
		if err != nil {
			return nil, nil, err
		}
		if edit != nil {
			edits = append(edits, *edit)
		}
		if note != "" {
			notes = append(notes, note)
		}
	}
	return edits, notes, nil
}

func marshalConfig(value any) ([]byte, error) {
	var out bytes.Buffer
	encoder := json.NewEncoder(&out)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(value); err != nil {
		return nil, err
	}
	return out.Bytes(), nil
}

func planClaudeMCP(path string) (*FileEdit, string, error) {
	before, existed, err := readIfPresent(path)
	if err != nil || !existed {
		return nil, noteFor(path, "not found, so Claude Code is left alone", err), err
	}
	var top map[string]json.RawMessage
	if err := json.Unmarshal(before, &top); err != nil {
		return nil, "", fmt.Errorf("%s is not valid JSON, so nothing was changed", path)
	}
	servers := map[string]json.RawMessage{}
	if raw, found := top["mcpServers"]; found {
		if err := json.Unmarshal(raw, &servers); err != nil {
			return nil, "", fmt.Errorf("%s has an mcpServers that is not an object, so nothing was changed", path)
		}
	}
	return swapClaudeServers(path, before, top, servers)
}

func noteFor(path, text string, err error) string {
	if err != nil {
		return ""
	}
	return path + ": " + text
}

func swapClaudeServers(path string, before []byte, top, servers map[string]json.RawMessage) (*FileEdit, string, error) {
	old, hasOld := servers[mcpOld]
	_, hasNew := servers[mcpNew]
	if !hasOld && hasNew {
		return nil, path + ": already uses memoria", nil
	}
	if !hasOld {
		return nil, path + ": has no qmd entry, so none was swapped", nil
	}
	delete(servers, mcpOld)
	summary := []string{"- mcpServers.qmd " + entrySummary(old)}
	if !hasNew {
		servers[mcpNew] = json.RawMessage(`{"type":"stdio","command":"memoria","args":["mcp"]}`)
		summary = append(summary, `+ mcpServers.memoria command "memoria" args ["mcp"]`)
	}
	raw, err := marshalConfig(servers)
	if err != nil {
		return nil, "", err
	}
	top["mcpServers"] = raw
	after, err := marshalConfig(top)
	if err != nil {
		return nil, "", err
	}
	return &FileEdit{Path: path, Existed: true, Before: before, After: after, Summary: summary}, "", nil
}

// entrySummary shows what an entry runs. Anything else it holds, such as its env, can hold a key.
func entrySummary(raw json.RawMessage) string {
	var entry struct {
		Command string   `json:"command"`
		Args    []string `json:"args"`
		URL     string   `json:"url"`
	}
	if err := json.Unmarshal(raw, &entry); err != nil {
		return "(an entry this cannot read)"
	}
	if entry.Command == "" {
		return "(" + redactURL(entry.URL) + ")"
	}
	return fmt.Sprintf("command %q args %q (any other fields not shown)", entry.Command, entry.Args)
}

func redactURL(url string) string {
	if url == "" {
		return "no command"
	}
	before, _, _ := strings.Cut(url, "?")
	return "url " + before
}

func planCodexMCP(path string) (*FileEdit, string, error) {
	before, existed, err := readIfPresent(path)
	if err != nil || !existed {
		return nil, noteFor(path, "not found, so Codex is left alone", err), err
	}
	return swapCodexText(path, before)
}

func tomlSection(line string) string {
	trimmed := strings.TrimSpace(line)
	if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
		return strings.Trim(trimmed, "[] ")
	}
	return ""
}

func swapCodexText(path string, before []byte) (*FileEdit, string, error) {
	text := string(before)
	newline := "\n"
	if strings.Contains(text, "\r\n") {
		newline = "\r\n"
	}
	kept, removed := withoutQMDSections(strings.Split(strings.ReplaceAll(text, "\r\n", "\n"), "\n"))
	hasNew := strings.Contains(text, "[mcp_servers.memoria]")
	switch {
	case removed == 0 && hasNew:
		return nil, path + ": already uses memoria", nil
	case removed == 0:
		return nil, path + ": has no qmd entry, so none was swapped", nil
	}
	result := strings.TrimRight(strings.Join(kept, "\n"), "\n") + "\n"
	summary := []string{"- [mcp_servers.qmd], and any table under it"}
	if !hasNew {
		result += "\n" + codexNewEntry
		summary = append(summary, `+ [mcp_servers.memoria] command "memoria" args ["mcp"]`)
	}
	after := []byte(strings.ReplaceAll(result, "\n", newline))
	return &FileEdit{Path: path, Existed: true, Before: before, After: after, Summary: summary}, "", nil
}

// withoutQMDSections drops the qmd table and tables under it, up to the next other table.
func withoutQMDSections(lines []string) ([]string, int) {
	kept := make([]string, 0, len(lines))
	removed, inside := 0, false
	for _, line := range lines {
		if section := tomlSection(line); section != "" {
			inside = section == codexOldSection || strings.HasPrefix(section, codexOldSection+".")
			if inside {
				removed++
			}
		}
		if !inside {
			kept = append(kept, line)
		}
	}
	return kept, removed
}

func planHook(path, command string) (*FileEdit, string, error) {
	if command == "" {
		return nil, "hook push: not enabled, as no --hook-command was given", nil
	}
	before, existed, err := readIfPresent(path)
	if err != nil {
		return nil, "", err
	}
	settings := map[string]any{}
	if existed {
		decoder := json.NewDecoder(bytes.NewReader(before))
		decoder.UseNumber()
		if err := decoder.Decode(&settings); err != nil {
			return nil, "", fmt.Errorf("%s is not valid JSON, so nothing was changed", path)
		}
	}
	return addHook(path, before, existed, settings, command)
}

func addHook(path string, before []byte, existed bool, settings map[string]any, command string) (*FileEdit, string, error) {
	hooks, _ := settings["hooks"].(map[string]any)
	if hooks == nil {
		hooks = map[string]any{}
	}
	ending, _ := hooks["SessionEnd"].([]any)
	entry, err := json.Marshal(ending)
	if err == nil && bytes.Contains(entry, mustJSON(command)) {
		return nil, path + ": the hook is already there", nil
	}
	hooks["SessionEnd"] = append(ending, map[string]any{"hooks": []any{map[string]any{"type": "command", "command": command}}})
	settings["hooks"] = hooks
	after, err := marshalConfig(settings)
	if err != nil {
		return nil, "", err
	}
	return &FileEdit{Path: path, Existed: existed, Before: before, After: after, Summary: []string{"+ hooks.SessionEnd runs the given hook command"}}, "", nil
}

func mustJSON(value string) []byte {
	data, _ := json.Marshal(value)
	return data
}

// planSkill installs into a client that is there, by its config file. Without a skill given, it says so.
func planSkill(clientConfig, skills string, skill []byte) (*FileEdit, string, error) {
	if skill == nil {
		return nil, skills + ": the skill was not installed, as no --skill was given", nil
	}
	if _, present, err := readIfPresent(clientConfig); err != nil || !present {
		return nil, "", err
	}
	path := filepath.Join(skills, "memoria", "SKILL.md")
	before, existed, err := readIfPresent(path)
	if err != nil {
		return nil, "", err
	}
	if existed && bytes.Equal(before, skill) {
		return nil, path + ": already installed", nil
	}
	return &FileEdit{Path: path, Existed: existed, Before: before, After: skill, Summary: []string{"+ the memoria skill"}}, "", nil
}

// CutoverEntry is one file a cutover changed. Backup is empty for a file that did not exist.
type CutoverEntry struct {
	Path    string `json:"path"`
	Existed bool   `json:"existed"`
	Backup  string `json:"backup,omitempty"`
	After   string `json:"afterSha256"`
}

// CutoverManifest is one cutover's record. Once rolled back it is kept and never reused, so nothing overwrites an original.
type CutoverManifest struct {
	RolledBack bool           `json:"rolledBack"`
	Entries    []CutoverEntry `json:"entries"`

	dir string
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
		return nil, false, fmt.Errorf("the cutover record at %s is damaged, so nothing was changed", dir)
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
func ActiveManifest() (*CutoverManifest, error) {
	latest, _, err := latestManifest()
	if err != nil || latest == nil || latest.RolledBack {
		return nil, err
	}
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

// note backs a file up the first time this cutover changes it. A later cutover keeps that backup, as it holds the original.
func (m *CutoverManifest) note(edit FileEdit) error {
	if known := m.entry(edit.Path); known != nil {
		known.After = hash(edit.After)
		return nil
	}
	entry := CutoverEntry{Path: edit.Path, Existed: edit.Existed, After: hash(edit.After)}
	if edit.Existed {
		entry.Backup = strconv.Itoa(len(m.Entries)) + ".bak"
		if err := auth.WritePrivateFile(filepath.Join(m.dir, entry.Backup), edit.Before); err != nil {
			return fmt.Errorf("the backup of %s could not be written, so nothing was changed", edit.Path)
		}
	}
	m.Entries = append(m.Entries, entry)
	return nil
}

// ApplyCutover backs up everything first and only then changes anything.
func ApplyCutover(edits []FileEdit) error {
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

// writeAtomic keeps a file's mode, and replaces it whole, so an interruption leaves one version or the other.
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
	_, writeErr := temp.Write(data)
	closeErr := temp.Close()
	if err := errors.Join(writeErr, closeErr, os.Chmod(name, mode), os.Rename(name, path)); err != nil {
		_ = os.Remove(name)
		return err
	}
	return nil
}

// RollbackStep is one file a rollback would put back.
type RollbackStep struct {
	Entry CutoverEntry
	// Changed means the file is neither the original nor what the cutover wrote, so someone edited it since.
	Changed bool
	backup  []byte
}

// PlanRollback works out what a rollback would do, and writes nothing. A file already back to its original needs nothing.
func PlanRollback(m *CutoverManifest) ([]RollbackStep, error) {
	var steps []RollbackStep
	for _, entry := range m.Entries {
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
	step := RollbackStep{Entry: entry}
	current, present, err := readIfPresent(entry.Path)
	if err != nil {
		return step, false, err
	}
	if entry.Existed {
		step.backup, err = auth.ReadPrivateFile(filepath.Join(m.dir, entry.Backup))
		if err != nil {
			return step, false, fmt.Errorf("the backup of %s cannot be read, so nothing was changed", entry.Path)
		}
		if present && bytes.Equal(current, step.backup) {
			return step, false, nil
		}
	}
	if !entry.Existed && !present {
		return step, false, nil
	}
	step.Changed = !present || hash(current) != entry.After
	return step, true, nil
}

// ApplyRollback puts each file back, and marks the record done. A file the cutover created is removed by its exact recorded
// path, and only that file.
func ApplyRollback(m *CutoverManifest, steps []RollbackStep) error {
	for _, step := range steps {
		if err := restore(step); err != nil {
			return fmt.Errorf("%s could not be restored. Run rollback again to finish", step.Entry.Path)
		}
	}
	m.RolledBack = true
	return m.save()
}

func restore(step RollbackStep) error {
	if step.Entry.Existed {
		return writeAtomic(step.Entry.Path, step.backup)
	}
	if err := os.Remove(step.Entry.Path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}
