package auth

import (
	"bufio"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestMain lets this test binary act as /usr/bin/security when the fake's state
// directory is set. It logs each call's arguments and stdin for the tests to
// inspect, and keeps entries in a file so that a write can be read back.
func TestMain(m *testing.M) {
	if dir := os.Getenv("MEMORIA_FAKE_SECURITY"); dir != "" {
		os.Exit(fakeSecurity(dir, os.Args[1:]))
	}
	os.Exit(m.Run())
}

type fakeCall struct {
	Args  []string `json:"args"`
	Stdin string   `json:"stdin"`
	Key   string   `json:"key"`
}

func fakeSecurity(dir string, args []string) int {
	stdin := ""
	if len(args) == 1 && args[0] == "-i" {
		var b strings.Builder
		scanner := bufio.NewScanner(os.Stdin)
		for scanner.Scan() {
			b.WriteString(scanner.Text() + "\n")
		}
		stdin = b.String()
	}
	appendFakeCall(dir, fakeCall{Args: args, Stdin: stdin, Key: os.Getenv(EnvAPIKey)})
	if code, handled := fakeSecurityFailure(dir, stdin); handled {
		return code
	}
	return fakeSecurityCommand(dir, args, stdin)
}

// fakeSecurityFailure plays the mode named by the test, if any.
func fakeSecurityFailure(dir, stdin string) (code int, handled bool) {
	switch os.Getenv("MEMORIA_FAKE_SECURITY_MODE") {
	case "locked":
		fmt.Fprintln(os.Stderr, "security: SecKeychainSearchCopyNext: User interaction is not allowed.")
		return 36, true
	case "crash":
		fmt.Fprintln(os.Stderr, "security: leaked-stderr-marker")
		return 1, true
	case "swallow":
		return 0, stdin != "" // "security -i" exits 0 even when its command failed.
	case "mangle":
		if stdin != "" {
			writeFakeEntries(dir, map[string]string{"profile/api-key": "something else"})
		}
		return 0, stdin != ""
	}
	return 0, false
}

func fakeSecurityCommand(dir string, args []string, stdin string) int {
	entries := readFakeEntries(dir)
	if stdin != "" {
		fields := strings.Fields(stdin)
		value, err := hex.DecodeString(fields[len(fields)-1])
		if err != nil {
			return 1
		}
		entries[fields[5]] = string(value)
		writeFakeEntries(dir, entries)
		return 0
	}
	account := args[len(args)-1]
	if args[0] == "find-generic-password" {
		account = args[len(args)-2]
	}
	value, ok := entries[account]
	if !ok {
		fmt.Fprintln(os.Stderr, "security: The specified item could not be found in the keychain.")
		return securityNotFound
	}
	if args[0] == "find-generic-password" {
		fmt.Println(value)
		return 0
	}
	delete(entries, account)
	writeFakeEntries(dir, entries)
	return 0
}

func appendFakeCall(dir string, call fakeCall) {
	data, _ := json.Marshal(call)
	f, err := os.OpenFile(filepath.Join(dir, "calls.jsonl"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err == nil {
		_, _ = f.Write(append(data, '\n'))
		_ = f.Close()
	}
}

func readFakeEntries(dir string) map[string]string {
	entries := map[string]string{}
	data, err := os.ReadFile(filepath.Join(dir, "entries.json"))
	if err == nil {
		_ = json.Unmarshal(data, &entries)
	}
	return entries
}

func writeFakeEntries(dir string, entries map[string]string) {
	data, _ := json.Marshal(entries)
	_ = os.WriteFile(filepath.Join(dir, "entries.json"), data, 0o600)
}

// fakeSecurityStore returns a store whose tool is the fake, and its state dir.
func fakeSecurityStore(t *testing.T, mode string) (*SecurityStore, string) {
	t.Helper()
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	t.Setenv("MEMORIA_FAKE_SECURITY", dir)
	t.Setenv("MEMORIA_FAKE_SECURITY_MODE", mode)
	return &SecurityStore{path: self, service: "memoria-cli"}, dir
}

func fakeCalls(t *testing.T, dir string) []fakeCall {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(dir, "calls.jsonl"))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		t.Fatal(err)
	}
	var calls []fakeCall
	for _, line := range strings.Split(strings.TrimSpace(string(data)), "\n") {
		var call fakeCall
		if err := json.Unmarshal([]byte(line), &call); err != nil {
			t.Fatal(err)
		}
		calls = append(calls, call)
	}
	return calls
}

func TestSecurityStoreContract(t *testing.T) {
	store, _ := fakeSecurityStore(t, "")
	exerciseStore(t, store, "profile/api-key", fileKey)
}

func TestSecurityStoreDeleteOfAnAbsentEntry(t *testing.T) {
	store, _ := fakeSecurityStore(t, "")
	if err := store.Delete("profile/api-key"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("err = %v", err)
	}
}

// The requirement this store exists for: other local processes can read argv.
func TestSecurityStoreNeverPutsTheKeyInArguments(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	exerciseStore(t, store, "profile/api-key", fileKey)

	encoded := hex.EncodeToString([]byte(fileKey))
	calls := fakeCalls(t, dir)
	if len(calls) < 3 {
		t.Fatalf("calls = %+v", calls)
	}
	for _, call := range calls {
		joined := strings.Join(call.Args, " ")
		if strings.Contains(joined, fileKey) || strings.Contains(joined, encoded) {
			t.Fatalf("the key is in the arguments: %q", joined)
		}
	}
}

func TestSecurityStoreWritesThroughStdin(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	if err := store.Set("profile/api-key", fileKey); err != nil {
		t.Fatal(err)
	}
	calls := fakeCalls(t, dir)
	write := calls[0]
	if strings.Join(write.Args, " ") != "-i" {
		t.Fatalf("write args = %v", write.Args)
	}
	want := "add-generic-password -U -s memoria-cli -a profile/api-key -X " + hex.EncodeToString([]byte(fileKey)) + "\n"
	if write.Stdin != want {
		t.Fatalf("stdin = %q", write.Stdin)
	}
	if got := strings.Join(calls[1].Args, " "); got != "find-generic-password -s memoria-cli -a profile/api-key -w" {
		t.Fatalf("the write should be read back, got %q", got)
	}
}

// Hex leaves nothing in a key that the tool's line parser could interpret.
func TestSecurityStoreKeepsKeysWithShellCharacters(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	key := `mem_a'b"c\d$(e)f;g|h&i` + "`j`"
	if err := store.Set("profile/api-key", key); err != nil {
		t.Fatal(err)
	}
	if got, err := store.Get("profile/api-key"); err != nil || got != key {
		t.Fatalf("got %q, err = %v", got, err)
	}
	for _, field := range strings.Fields(fakeCalls(t, dir)[0].Stdin)[6:] {
		if strings.ContainsAny(field, `'"\$();|&`+"`") {
			t.Fatalf("stdin field %q is not inert", field)
		}
	}
}

func TestSecurityStoreRefusesNamesThatCouldInjectACommand(t *testing.T) {
	for _, name := range []string{
		"x -s other",
		"x\ndelete-keychain login.keychain",
		"x\rdelete-keychain",
		`x"y`,
		"x'y",
		`x\y`,
		"-a",
		"--help",
		"",
		"https://memoria.example.com:8443/?a=b c",
		"x;y",
		"$(id)",
	} {
		t.Run(name, func(t *testing.T) {
			store, dir := fakeSecurityStore(t, "")
			if err := store.Set(name, fileKey); err == nil {
				t.Error("Set should refuse")
			}
			if _, err := store.Get(name); err == nil || errors.Is(err, ErrNotFound) {
				t.Errorf("Get = %v", err)
			}
			if err := store.Delete(name); err == nil || errors.Is(err, ErrNotFound) {
				t.Errorf("Delete = %v", err)
			}
			if calls := fakeCalls(t, dir); len(calls) != 0 {
				t.Fatalf("the tool ran: %+v", calls)
			}
		})
	}
}

func TestSecurityStoreRefusesABadServiceName(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	store.service = "memoria cli"
	if err := store.Set("a", fileKey); err == nil {
		t.Fatal("expected a refusal")
	}
	if len(fakeCalls(t, dir)) != 0 {
		t.Fatal("the tool ran")
	}
}

func TestSecurityStoreRefusesAnOverlongLine(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	if err := store.Set("a", strings.Repeat("k", securityLineLimit)); err == nil {
		t.Fatal("expected a refusal")
	}
	if len(fakeCalls(t, dir)) != 0 {
		t.Fatal("the tool ran")
	}
}

// "security -i" can exit 0 after a failed command, so only reading back tells.
func TestSecurityStoreDetectsAWriteThatDidNotHappen(t *testing.T) {
	store, _ := fakeSecurityStore(t, "swallow")
	if err := store.Set("profile/api-key", fileKey); err == nil || !strings.Contains(err.Error(), "did not keep") {
		t.Fatalf("err = %v", err)
	}
}

func TestSecurityStoreDetectsAMangledWrite(t *testing.T) {
	store, _ := fakeSecurityStore(t, "mangle")
	err := store.Set("profile/api-key", fileKey)
	if err == nil || !strings.Contains(err.Error(), "read back differently") || strings.Contains(err.Error(), fileKey) {
		t.Fatalf("err = %v", err)
	}
}

func TestSecurityStoreLockedKeychain(t *testing.T) {
	store, _ := fakeSecurityStore(t, "locked")
	_, err := store.Get("profile/api-key")
	if err == nil || !strings.Contains(err.Error(), "locked") {
		t.Fatalf("err = %v", err)
	}
	if errors.Is(err, ErrNotFound) {
		t.Fatal("a locked keychain is not a missing entry")
	}
}

func TestSecurityStoreFailuresDoNotRepeatToolOutput(t *testing.T) {
	store, _ := fakeSecurityStore(t, "crash")
	_, getErr := store.Get("a")
	setErr := store.Set("a", fileKey)
	deleteErr := store.Delete("a")
	for _, err := range []error{getErr, setErr, deleteErr} {
		if err == nil || strings.Contains(err.Error(), "leaked-stderr-marker") || strings.Contains(err.Error(), fileKey) {
			t.Errorf("err = %v", err)
		}
		if !strings.Contains(err.Error(), "status 1") {
			t.Errorf("err = %v", err)
		}
	}
}

func TestSecurityStoreMissingToolIsUnavailable(t *testing.T) {
	store := &SecurityStore{path: filepath.Join(t.TempDir(), "no-such-tool"), service: "memoria-cli"}
	if _, err := store.Get("a"); !errors.Is(err, ErrKeyringUnavailable) {
		t.Fatalf("err = %v", err)
	}
}

func TestSecurityStoreDoesNotPassTheEnvironmentKey(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	t.Setenv(EnvAPIKey, envKey)
	_, _ = store.Get("a")
	calls := fakeCalls(t, dir)
	if len(calls) != 1 || calls[0].Key != "" {
		t.Fatalf("calls = %+v", calls)
	}
}

func TestSecurityStoreGetTrimsOnlyTheLineEnding(t *testing.T) {
	store, dir := fakeSecurityStore(t, "")
	writeFakeEntries(dir, map[string]string{"a": "  value  "})
	if got, err := store.Get("a"); err != nil || got != "  value  " {
		t.Fatalf("got %q, err = %v", got, err)
	}
}
