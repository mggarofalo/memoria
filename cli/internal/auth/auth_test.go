package auth

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const (
	flagKey    = "mem_flag000000000000_aaaaaaaaaaaaaaaa"
	envKey     = "mem_env0000000000000_bbbbbbbbbbbbbbbb"
	keyringKey = "mem_keyring000000000_cccccccccccccccc"
	fileKey    = "mem_file000000000000_dddddddddddddddd"
)

type memoryStore struct {
	values map[string]string
	getErr error
	gets   int
}

func newMemoryStore(pairs ...string) *memoryStore {
	m := &memoryStore{values: map[string]string{}}
	for i := 0; i+1 < len(pairs); i += 2 {
		m.values[pairs[i]] = pairs[i+1]
	}
	return m
}

func (m *memoryStore) Get(name string) (string, error) {
	m.gets++
	if m.getErr != nil {
		return "", m.getErr
	}
	v, ok := m.values[name]
	if !ok {
		return "", ErrNotFound
	}
	return v, nil
}

func (m *memoryStore) Set(name, value string) error {
	m.values[name] = value
	return nil
}

func (m *memoryStore) Delete(name string) error {
	if _, ok := m.values[name]; !ok {
		return ErrNotFound
	}
	delete(m.values, name)
	return nil
}

const testOrigin = "https://memoria.example.com:443"

// entry is what a store holds for a key.
func entry(key string) string {
	value, err := EncodeStoredKey(testOrigin, key)
	if err != nil {
		panic(err)
	}
	return value
}

func emptyConfig() *Config {
	return &Config{ActiveProfile: DefaultProfile, Profiles: map[string]Profile{}}
}

func envOf(pairs ...string) func(string) string {
	m := map[string]string{}
	for i := 0; i+1 < len(pairs); i += 2 {
		m[pairs[i]] = pairs[i+1]
	}
	return func(name string) string { return m[name] }
}

func TestResolverPrecedence(t *testing.T) {
	name := KeyName(DefaultProfile)
	ring := func() (SecretStore, error) { return newMemoryStore(name, entry(keyringKey)), nil }
	noRing := func() (SecretStore, error) { return newMemoryStore(), nil }
	file := newMemoryStore(name, entry(fileKey))
	empty := newMemoryStore()

	cases := []struct {
		name       string
		resolver   Resolver
		wantKey    string
		wantSource string
	}{
		{"flag beats everything", Resolver{FlagKey: flagKey, Getenv: envOf(EnvAPIKey, envKey), Keyring: ring, File: file}, flagKey, SourceFlag},
		{"env beats stores", Resolver{Getenv: envOf(EnvAPIKey, envKey), Keyring: ring, File: file}, envKey, SourceEnv},
		{"keyring beats file", Resolver{Getenv: envOf(), Keyring: ring, File: file}, keyringKey, SourceKeyring},
		{"file is the last tier", Resolver{Getenv: envOf(), Keyring: noRing, File: file}, fileKey, SourceFile},
		{"flag is normalized", Resolver{FlagKey: "  " + flagKey + "\n", Getenv: envOf(), File: empty}, flagKey, SourceFlag},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			c.resolver.Profile = DefaultProfile
			c.resolver.Config = emptyConfig()
			got, err := c.resolver.Resolve()
			if err != nil {
				t.Fatal(err)
			}
			if got.Credential.Reveal() != c.wantKey || got.Source != c.wantSource {
				t.Fatalf("source = %s", got.Source)
			}
		})
	}
}

func TestResolverDoesNotOpenTheKeyringWhenAFlagOrEnvKeyIsGiven(t *testing.T) {
	opened := false
	keyring := func() (SecretStore, error) {
		opened = true
		return newMemoryStore(), nil
	}
	for _, r := range []Resolver{
		{FlagKey: flagKey, Getenv: envOf(), Keyring: keyring},
		{Getenv: envOf(EnvAPIKey, envKey), Keyring: keyring},
	} {
		r.Profile, r.Config = DefaultProfile, emptyConfig()
		if _, err := r.Resolve(); err != nil {
			t.Fatal(err)
		}
	}
	if opened {
		t.Fatal("the keyring can prompt, so it should stay closed")
	}
}

func TestResolverSkipsTheKeyringForAFileProfile(t *testing.T) {
	store := newMemoryStore(KeyName(DefaultProfile), entry(keyringKey))
	cfg := emptyConfig()
	cfg.Profiles[DefaultProfile] = Profile{Backend: BackendFile}
	r := Resolver{
		Profile: DefaultProfile, Config: cfg, Getenv: envOf(),
		Keyring: func() (SecretStore, error) { return store, nil },
		File:    newMemoryStore(KeyName(DefaultProfile), entry(fileKey)),
	}
	got, err := r.Resolve()
	if err != nil {
		t.Fatal(err)
	}
	if got.Source != SourceFile || store.gets != 0 {
		t.Fatalf("source = %s, keyring reads = %d", got.Source, store.gets)
	}
}

func TestResolverUsesTheNamedProfile(t *testing.T) {
	r := Resolver{
		Profile: "work", Config: emptyConfig(), Getenv: envOf(),
		File: newMemoryStore(KeyName("home"), entry(fileKey), KeyName("work"), entry(keyringKey)),
	}
	got, err := r.Resolve()
	if err != nil {
		t.Fatal(err)
	}
	if got.Credential.Reveal() != keyringKey {
		t.Fatal("wrong profile's key")
	}
}

func TestResolverNothingStored(t *testing.T) {
	r := Resolver{Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(), File: newMemoryStore()}
	_, err := r.Resolve()
	if apperr.ExitCode(err) != apperr.ExitAuth || !strings.Contains(err.Error(), "auth login") {
		t.Fatalf("err = %v", err)
	}
}

func TestResolverReportsKeyringFailure(t *testing.T) {
	broken := newMemoryStore()
	broken.getErr = errors.New("keyring is locked")
	r := Resolver{
		Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(), File: newMemoryStore(),
		Keyring: func() (SecretStore, error) { return broken, nil },
	}
	_, err := r.Resolve()
	if apperr.ExitCode(err) != apperr.ExitAuth || !strings.Contains(err.Error(), "locked") {
		t.Fatalf("err = %v", err)
	}
}

func TestResolverUnavailableKeyringFallsThroughToFile(t *testing.T) {
	r := Resolver{
		Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(),
		File:    newMemoryStore(KeyName(DefaultProfile), entry(fileKey)),
		Keyring: func() (SecretStore, error) { return nil, ErrKeyringUnavailable },
	}
	got, err := r.Resolve()
	if err != nil || got.Source != SourceFile {
		t.Fatalf("got %s, err = %v", got.Source, err)
	}
}

func TestResolverKeyringProfileWithoutAKeyring(t *testing.T) {
	cfg := emptyConfig()
	cfg.Profiles[DefaultProfile] = Profile{Backend: BackendKeyring}
	r := Resolver{
		Profile: DefaultProfile, Config: cfg, Getenv: envOf(), File: newMemoryStore(),
		Keyring: func() (SecretStore, error) { return nil, ErrKeyringUnavailable },
	}
	_, err := r.Resolve()
	if apperr.ExitCode(err) != apperr.ExitAuth || !strings.Contains(err.Error(), "keyring") {
		t.Fatalf("err = %v", err)
	}
}

func TestResolverFileFailureIsReported(t *testing.T) {
	broken := newMemoryStore()
	broken.getErr = errors.New("permissions too open")
	r := Resolver{Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(), File: broken}
	_, err := r.Resolve()
	if apperr.ExitCode(err) != apperr.ExitAuth || !strings.Contains(err.Error(), "too open") {
		t.Fatalf("err = %v", err)
	}
}

func TestResolverInvalidValuesDoNotEcho(t *testing.T) {
	const bad = "secret value with spaces"
	for _, r := range []Resolver{
		{FlagKey: bad, Getenv: envOf()},
		{Getenv: envOf(EnvAPIKey, bad)},
		{Getenv: envOf(), File: newMemoryStore(KeyName(DefaultProfile), bad)},
		{Getenv: envOf(), File: newMemoryStore(KeyName(DefaultProfile), `{"origin":"https://x:443","key":"`+bad+`"}`)},
	} {
		r.Profile, r.Config = DefaultProfile, emptyConfig()
		_, err := r.Resolve()
		if err == nil || strings.Contains(err.Error(), "secret") {
			t.Errorf("err = %v", err)
		}
	}
}

func TestResolverDefaultsToTheProcessEnvironment(t *testing.T) {
	t.Setenv(EnvAPIKey, envKey)
	r := Resolver{Profile: DefaultProfile, Config: emptyConfig()}
	got, err := r.Resolve()
	if err != nil || got.Source != SourceEnv || got.Credential.Reveal() != envKey {
		t.Fatalf("got %s, err = %v", got.Source, err)
	}
}

func TestResolveURLPrecedence(t *testing.T) {
	cfg := emptyConfig()
	cfg.Profiles[DefaultProfile] = Profile{APIURL: "https://config"}
	base := Resolver{Profile: DefaultProfile, Config: cfg}

	r := base
	r.FlagURL, r.Getenv = "https://flag", envOf(EnvURL, "https://env")
	if got := r.ResolveURL(); got != "https://flag" {
		t.Errorf("got %s", got)
	}
	r = base
	r.Getenv = envOf(EnvURL, "https://env")
	if got := r.ResolveURL(); got != "https://env" {
		t.Errorf("got %s", got)
	}
	r = base
	r.Getenv = envOf()
	if got := r.ResolveURL(); got != "https://config" {
		t.Errorf("got %s", got)
	}
}

func TestCredentialCannotBeFormatted(t *testing.T) {
	type wrapper struct {
		Exported   Credential
		unexported Credential
		Resolved   Resolved
	}
	c := NewCredential(flagKey)
	w := wrapper{Exported: c, unexported: c, Resolved: Resolved{Credential: c, Source: SourceFlag}}
	for _, format := range []string{"%v", "%+v", "%#v", "%s", "%q", "%x"} {
		if out := fmt.Sprintf(format, c); strings.Contains(out, flagKey) {
			t.Errorf("Sprintf(%q, credential) leaked: %s", format, out)
		}
		if out := fmt.Sprintf(format, w); strings.Contains(out, flagKey) {
			t.Errorf("Sprintf(%q, wrapper) leaked: %s", format, out)
		}
	}
	data, err := json.Marshal(w)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(data), flagKey) {
		t.Fatalf("JSON leaked: %s", data)
	}
	if c.Reveal() != flagKey {
		t.Fatal("Reveal should still return the key")
	}
	if !(Credential{}).IsEmpty() || c.IsEmpty() {
		t.Fatal("IsEmpty is wrong")
	}
}

func TestNormalizeKey(t *testing.T) {
	good := map[string]string{
		flagKey:                flagKey,
		"  " + flagKey + "\n":  flagKey,
		flagKey + "\r\n":       flagKey,
		utf8BOM + flagKey:      flagKey,
		"opaque-any.format~01": "opaque-any.format~01",
	}
	for in, want := range good {
		got, err := NormalizeKey(in)
		if err != nil || got != want {
			t.Errorf("NormalizeKey(%q) = %q, %v", in, got, err)
		}
	}
	for _, in := range []string{"", "   ", "abc12", "has space inside", "line\nbreak00", "ctrl\x01char0", "cafécafé", strings.Repeat("x", maxKeyLength+1)} {
		_, err := NormalizeKey(in)
		if err == nil {
			t.Errorf("NormalizeKey(%q) should fail", in)
			continue
		}
		if in != "" && strings.TrimSpace(in) != "" && strings.Contains(err.Error(), in) {
			t.Errorf("error repeats the value: %v", err)
		}
	}
}

func useTempConfig(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	return filepath.Join(dir, configDirName)
}

func TestConfigRoundTripAndPath(t *testing.T) {
	dir := useTempConfig(t)
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ActiveProfile != DefaultProfile {
		t.Fatalf("active = %q", cfg.ActiveProfile)
	}
	cfg.Profiles["home"] = Profile{APIURL: "https://memoria.example.com", Backend: BackendKeyring}
	cfg.ActiveProfile = "home"
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}
	path, _ := ConfigPath()
	if filepath.Dir(path) != dir {
		t.Fatalf("path = %s", path)
	}
	loaded, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if loaded.ActiveProfile != "home" || loaded.Profile("home").APIURL != "https://memoria.example.com" {
		t.Fatalf("loaded = %+v", loaded)
	}
}

func TestConfigNeverCarriesAKey(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, configFileName)
	body := `{"active_profile":"default","api_key":"` + flagKey + `","profiles":{"default":{"api_url":"https://x","token":"` + flagKey + `"}}}`
	if err := writePrivateFile(path, []byte(body)); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if !cfg.HasStraySecret {
		t.Fatal("a key in the config should be noticed")
	}
	r := Resolver{Profile: DefaultProfile, Config: cfg, Getenv: envOf(), File: newMemoryStore()}
	if _, err := r.Resolve(); apperr.ExitCode(err) != apperr.ExitAuth {
		t.Fatalf("a key in the config must not be used: %v", err)
	}
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(data), flagKey) {
		t.Fatalf("saving should drop the stray key: %s", data)
	}
}

func TestConfigParseErrorsDoNotQuoteTheFile(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(filepath.Join(dir, configFileName), []byte(flagKey)); err != nil {
		t.Fatal(err)
	}
	_, err := LoadConfig()
	if err == nil || strings.Contains(err.Error(), flagKey) {
		t.Fatalf("err = %v", err)
	}
}

func TestValidateProfileName(t *testing.T) {
	for _, name := range []string{"default", "work-1", "a.b_c"} {
		if err := ValidateProfileName(name); err != nil {
			t.Errorf("%q: %v", name, err)
		}
	}
	for _, name := range []string{"", "../evil", "a/b", ".hidden", "with space", strings.Repeat("x", 65)} {
		if err := ValidateProfileName(name); err == nil {
			t.Errorf("%q should be rejected", name)
		}
	}
}

func TestFileStoreRoundTrip(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	exerciseStore(t, store, "a", fileKey)
}

func TestFileStoreDeleteKeepsOtherEntriesAndRemovesAnEmptyFile(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	for name, value := range map[string]string{"a": fileKey, "b": flagKey} {
		if err := store.Set(name, value); err != nil {
			t.Fatal(err)
		}
	}
	if err := store.Delete("a"); err != nil {
		t.Fatal(err)
	}
	if got, _ := store.Get("b"); got != flagKey {
		t.Fatal("other entries should survive a delete")
	}
	if err := store.Delete("b"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(store.Path()); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("an empty store should remove its file: %v", err)
	}
}

func TestFileStoreLeavesNoTempFiles(t *testing.T) {
	dir := useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 || entries[0].Name() != credentialFileName {
		t.Fatalf("entries = %v", entries)
	}
}

func TestFileStoreCorruptFileDoesNotQuoteContent(t *testing.T) {
	dir := useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, credentialFileName), []byte(fileKey), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err = store.Get("a")
	if err == nil || strings.Contains(err.Error(), fileKey) {
		t.Fatalf("err = %v", err)
	}
}

func TestFileStorePermissionsAreVerifiedPrivate(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	if err := verifyPrivate(store.Path()); err != nil {
		t.Fatalf("freshly written file is not private: %v", err)
	}
}

func TestFileWarningSaysWhatItIs(t *testing.T) {
	for _, phrase := range []string{"unencrypted", "weaker"} {
		if !strings.Contains(FileWarning, phrase) {
			t.Errorf("warning does not say %q", phrase)
		}
	}
}

func TestStoredKeyCarriesItsOrigin(t *testing.T) {
	r := Resolver{Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(), File: newMemoryStore(KeyName(DefaultProfile), entry(fileKey))}
	got, err := r.Resolve()
	if err != nil {
		t.Fatal(err)
	}
	if got.Origin != testOrigin || got.Credential.Reveal() != fileKey {
		t.Fatalf("origin = %q", got.Origin)
	}
}

func TestFlagAndEnvKeysHaveNoOrigin(t *testing.T) {
	r := Resolver{FlagKey: flagKey, Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf()}
	got, err := r.Resolve()
	if err != nil || got.Origin != "" || got.Stored() {
		t.Fatalf("got %+v, err = %v", got, err)
	}
}

// A bare key, as an earlier layout stored, has no origin and must not be used.
func TestStoredValueWithoutAnOriginIsRefused(t *testing.T) {
	for _, value := range []string{fileKey, `{"key":"` + fileKey + `"}`, `{"origin":"","key":"` + fileKey + `"}`, "null"} {
		r := Resolver{Profile: DefaultProfile, Config: emptyConfig(), Getenv: envOf(), File: newMemoryStore(KeyName(DefaultProfile), value)}
		_, err := r.Resolve()
		if apperr.ExitCode(err) != apperr.ExitAuth || strings.Contains(err.Error(), fileKey) {
			t.Errorf("value %q: err = %v", value, err)
		}
	}
}

func TestEncodeStoredKeyNeedsAnOrigin(t *testing.T) {
	if _, err := EncodeStoredKey("", fileKey); err == nil {
		t.Fatal("expected an error")
	}
}

func TestRecordedOriginPrefersTheKeyThenTheMarker(t *testing.T) {
	both := newMemoryStore(KeyName("p"), entry(fileKey), OriginMarkerName("p"), "https://marker:443")
	if got := RecordedOrigin(both, "p"); got != testOrigin {
		t.Errorf("got %q", got)
	}
	marker := newMemoryStore(OriginMarkerName("p"), "https://marker:443")
	if got := RecordedOrigin(marker, "p"); got != "https://marker:443" {
		t.Errorf("got %q", got)
	}
	if got := RecordedOrigin(newMemoryStore(), "p"); got != "" {
		t.Errorf("got %q", got)
	}
}

func TestStoredOrigin(t *testing.T) {
	store := newMemoryStore(KeyName("good"), entry(fileKey), KeyName("bad"), "not an entry")
	if got := StoredOrigin(store, "good"); got != testOrigin {
		t.Errorf("got %q", got)
	}
	for _, profile := range []string{"bad", "absent"} {
		if got := StoredOrigin(store, profile); got != "" {
			t.Errorf("%s: got %q", profile, got)
		}
	}
}

func TestRelativeXDGConfigHomeIsIgnored(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", filepath.Join("relative", "dir"))
	home, err := os.UserHomeDir()
	if err != nil {
		t.Skip("no home directory")
	}
	dir, err := ConfigDir()
	if err != nil {
		t.Fatal(err)
	}
	if dir != filepath.Join(home, ".config", configDirName) {
		t.Fatalf("dir = %s", dir)
	}
}

func TestConfigIsNotReadFromASymlink(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "elsewhere.json")
	if err := os.WriteFile(target, []byte(`{"profiles":{"default":{"api_url":"https://evil.test"}}}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, filepath.Join(dir, configFileName)); err != nil {
		t.Skip("symlinks unavailable:", err)
	}
	if _, err := LoadConfig(); !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("err = %v", err)
	}
}

func TestHardLinkedCredentialsAreRefused(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", entry(fileKey)); err != nil {
		t.Fatal(err)
	}
	if err := os.Link(store.Path(), filepath.Join(filepath.Dir(store.Path()), "copy")); err != nil {
		t.Skip("hard links unavailable:", err)
	}
	if _, err := store.Get("a"); !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("err = %v", err)
	}
}
