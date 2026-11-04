package main

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// These tests run the real install scripts. Network access is replaced: a fake
// curl on PATH (sh) or stub cmdlets (PowerShell) serve a fixture directory and
// record every URL and flag, so nothing in the scripts needs a test-only override
// that could also redirect a real install.

const fakeBinary = "fake memoria binary"

const fakeCurl = `#!/bin/sh
# Serves files from $FIXTURES by the URL's basename and logs its arguments.
echo "$*" >> "$CURL_LOG"
out=""
url=""
while [ $# -gt 0 ]; do
    case "$1" in
        -o) out="$2"; shift 2 ;;
        https://*) url="$1"; shift ;;
        *) shift ;;
    esac
done
name="${url##*/}"
file="$FIXTURES/$name"
[ -f "$file" ] || exit 22
if [ -n "$out" ]; then cp "$file" "$out"; else cat "$file"; fi
`

const fakeUname = `#!/bin/sh
case "$1" in
    -s) echo "${FAKE_OS:-Linux}" ;;
    -m) echo "${FAKE_ARCH:-x86_64}" ;;
esac
`

func checksumOf(data []byte) string {
	h := sha256.Sum256(data)
	return hex.EncodeToString(h[:])
}

func tarGz(t *testing.T, name string, payload []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	if err := tw.WriteHeader(&tar.Header{Name: name, Mode: 0o755, Size: int64(len(payload))}); err != nil {
		t.Fatal(err)
	}
	if _, err := tw.Write(payload); err != nil {
		t.Fatal(err)
	}
	if err := tw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := gz.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

type tarMember struct {
	name, link string
	body       []byte
}

func tarGzEntries(t *testing.T, members ...tarMember) []byte {
	t.Helper()
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	for _, m := range members {
		header := &tar.Header{Name: m.name, Mode: 0o755, Size: int64(len(m.body)), Typeflag: tar.TypeReg}
		if m.link != "" {
			header.Typeflag, header.Linkname = tar.TypeSymlink, m.link
		}
		if err := tw.WriteHeader(header); err != nil {
			t.Fatal(err)
		}
		if _, err := tw.Write(m.body); err != nil {
			t.Fatal(err)
		}
	}
	if err := tw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := gz.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func zipArchive(t *testing.T, name string, payload []byte) []byte {
	t.Helper()
	return zipEntries(t, name, payload)
}

// zipEntries takes name, body pairs.
func zipEntries(t *testing.T, pairs ...any) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for i := 0; i+1 < len(pairs); i += 2 {
		f, err := zw.Create(pairs[i].(string))
		if err != nil {
			t.Fatal(err)
		}
		if _, err := f.Write(pairs[i+1].([]byte)); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func writeFile(t *testing.T, path string, data []byte, mode os.FileMode) {
	t.Helper()
	if err := os.WriteFile(path, data, mode); err != nil {
		t.Fatal(err)
	}
}

type shellRun struct {
	fixtures, prefix, curlLog string
	output                    string
	err                       error
}

// shellScenario sets up fixtures for one sh run.
type shellScenario struct {
	archive        []byte // nil builds a good archive
	checksums      string // empty computes a good file
	latest         string // releases/latest body
	prefix         string // empty uses a fresh directory
	relativePrefix bool
	args           []string
}

const shellArchiveName = "memoria_1.2.3_linux_amd64.tar.gz"

func runInstallSh(t *testing.T, s shellScenario) shellRun {
	t.Helper()
	shell, err := exec.LookPath("sh")
	if err != nil {
		t.Skip("sh is not available")
	}
	script, err := filepath.Abs(filepath.Join("..", "install.sh"))
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	bin := filepath.Join(dir, "bin")
	r := shellRun{
		fixtures: filepath.Join(dir, "fixtures"),
		prefix:   filepath.Join(dir, "install", "bin"),
		curlLog:  filepath.Join(dir, "curl.log"),
	}
	for _, d := range []string{bin, r.fixtures} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	writeFile(t, filepath.Join(bin, "curl"), []byte(fakeCurl), 0o755)
	writeFile(t, filepath.Join(bin, "uname"), []byte(fakeUname), 0o755)

	archive := s.archive
	if archive == nil {
		archive = tarGz(t, "memoria", []byte(fakeBinary))
	}
	checksums := s.checksums
	if checksums == "" {
		checksums = checksumOf(archive) + "  " + shellArchiveName + "\n"
	}
	writeFile(t, filepath.Join(r.fixtures, shellArchiveName), archive, 0o644)
	writeFile(t, filepath.Join(r.fixtures, "checksums.txt"), []byte(checksums), 0o644)
	if s.latest != "" {
		writeFile(t, filepath.Join(r.fixtures, "latest"), []byte(s.latest), 0o644)
	}

	if s.prefix != "" {
		r.prefix = s.prefix
	}
	if s.relativePrefix {
		r.prefix = "relative/bin"
	}
	args := append([]string{script, "--prefix", r.prefix}, s.args...)
	cmd := exec.Command(shell, args...)
	cmd.Env = append(os.Environ(),
		"PATH="+bin+string(os.PathListSeparator)+os.Getenv("PATH"),
		"FIXTURES="+r.fixtures,
		"CURL_LOG="+r.curlLog,
	)
	out, err := cmd.CombinedOutput()
	r.output, r.err = string(out), err
	return r
}

func (r shellRun) installed() string {
	data, err := os.ReadFile(filepath.Join(r.prefix, "memoria"))
	if err != nil {
		return ""
	}
	return string(data)
}

func TestInstallShInstallsAPinnedVersion(t *testing.T) {
	r := runInstallSh(t, shellScenario{args: []string{"--version", "v1.2.3"}})
	if r.err != nil {
		t.Fatalf("%v: %s", r.err, r.output)
	}
	if r.installed() != fakeBinary {
		t.Fatalf("installed = %q, output = %s", r.installed(), r.output)
	}
	if !strings.Contains(r.output, "Checksum verified") {
		t.Fatalf("output = %s", r.output)
	}
	log, err := os.ReadFile(r.curlLog)
	if err != nil {
		t.Fatal(err)
	}
	for _, line := range strings.Split(strings.TrimSpace(string(log)), "\n") {
		if !strings.HasPrefix(line, "-q ") {
			t.Errorf("curl call %q must start with -q, or a ~/.curlrc can undo the rest", line)
		}
		for _, flag := range []string{"--proto =https", "--proto-redir =https", "--tlsv1.2", "https://github.com/mggarofalo/memoria/releases/download/v1.2.3/"} {
			if !strings.Contains(line, flag) {
				t.Errorf("curl call %q lacks %q", line, flag)
			}
		}
		if strings.Contains(line, "http://") {
			t.Errorf("plain http: %q", line)
		}
	}
}

func TestInstallShFindsTheLatestRelease(t *testing.T) {
	r := runInstallSh(t, shellScenario{latest: "{\n  \"tag_name\": \"v1.2.3\",\n  \"prerelease\": false\n}\n"})
	if r.err != nil {
		t.Fatalf("%v: %s", r.err, r.output)
	}
	if r.installed() != fakeBinary {
		t.Fatalf("installed = %q", r.installed())
	}
	log, _ := os.ReadFile(r.curlLog)
	if !strings.Contains(string(log), "api.github.com/repos/mggarofalo/memoria/releases/latest") {
		t.Fatalf("log = %s", log)
	}
}

func TestInstallShRefusesBadDownloads(t *testing.T) {
	other := tarGz(t, "memoria", []byte("tampered"))
	wrongMember := tarGz(t, "something-else", []byte(fakeBinary))
	duplicate := tarGzEntries(t, tarMember{name: "memoria", body: []byte("one")}, tarMember{name: "memoria", body: []byte("two")})
	link := tarGzEntries(t, tarMember{name: "memoria", link: "/etc/passwd"})
	cases := map[string]struct {
		scenario shellScenario
		want     string
	}{
		"tampered archive": {shellScenario{archive: other, checksums: checksumOf([]byte("good")) + "  " + shellArchiveName + "\n"}, "checksum mismatch"},
		"missing entry":    {shellScenario{checksums: strings.Repeat("0", 64) + "  memoria_1.2.3_darwin_arm64.tar.gz\n"}, "not listed"},
		"substring entry":  {shellScenario{checksums: strings.Repeat("0", 64) + "  " + shellArchiveName + ".sbom\n"}, "not listed"},
		"no binary inside": {shellScenario{archive: wrongMember}, "no memoria binary"},
		"duplicate binary": {shellScenario{archive: duplicate}, "more than once"},
		"symlink member":   {shellScenario{archive: link}, "not a regular file"},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			c.scenario.args = []string{"--version", "v1.2.3"}
			r := runInstallSh(t, c.scenario)
			if r.err == nil {
				t.Fatalf("expected failure: %s", r.output)
			}
			if !strings.Contains(r.output, c.want) {
				t.Fatalf("output = %s", r.output)
			}
			if r.installed() != "" {
				t.Fatal("something was installed")
			}
		})
	}
}

func TestInstallShRejectsBadVersions(t *testing.T) {
	for _, version := range []string{"1.2.3", "latest", "v1.2", "v1.2.3;id", "../v1.2.3", "v1.2.3 x", "$(id)", "v01.2.3", "v1.2.3\nv1.2.3", "v1234567890.0.0"} {
		t.Run(version, func(t *testing.T) {
			r := runInstallSh(t, shellScenario{args: []string{"--version", version}})
			if r.err == nil || !strings.Contains(r.output, "not a release version") {
				t.Fatalf("err = %v, output = %s", r.err, r.output)
			}
			if _, err := os.Stat(r.curlLog); err == nil {
				t.Fatal("a bad version should not reach the network")
			}
		})
	}
}

func TestInstallShRejectsARelativePrefix(t *testing.T) {
	r := runInstallSh(t, shellScenario{relativePrefix: true, args: []string{"--version", "v1.2.3"}})
	if r.err == nil || !strings.Contains(r.output, "absolute") {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
}

func TestInstallShRefusesADecompressionBomb(t *testing.T) {
	bomb := tarGz(t, "memoria", make([]byte, 134217728+1))
	r := runInstallSh(t, shellScenario{archive: bomb, args: []string{"--version", "v1.2.3"}})
	if r.err == nil || !strings.Contains(r.output, "size limit") {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
}

// A planted symlink at the old predictable name must not be followed, and an
// existing binary keeps its mode.
func TestInstallShStagesSafelyAndKeepsTheMode(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("needs Unix modes and symlinks")
	}
	dir := t.TempDir()
	prefix := filepath.Join(dir, "bin")
	if err := os.MkdirAll(prefix, 0o755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(prefix, "memoria"), []byte("old"), 0o700)
	r := runInstallSh(t, shellScenario{prefix: prefix, args: []string{"--version", "v1.2.3"}})
	if r.err != nil {
		t.Fatalf("%v: %s", r.err, r.output)
	}
	info, err := os.Stat(filepath.Join(prefix, "memoria"))
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o700 {
		t.Fatalf("mode = %o", info.Mode().Perm())
	}
	entries, err := os.ReadDir(prefix)
	if err != nil || len(entries) != 1 {
		t.Fatalf("entries = %v, err = %v", entries, err)
	}
	data, err := os.ReadFile(filepath.Join(prefix, "memoria"))
	if err != nil || string(data) != fakeBinary {
		t.Fatalf("data = %q, err = %v", data, err)
	}
}

func TestInstallShRejectsUnknownOptions(t *testing.T) {
	r := runInstallSh(t, shellScenario{args: []string{"--nope"}})
	if r.err == nil || !strings.Contains(r.output, "unknown option") {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
}

func TestDocumentedInstallNeverPipesToAShell(t *testing.T) {
	for _, path := range []string{"../install.sh", "../install.ps1", "../docs/cli.md", "../README.md"} {
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		for _, line := range strings.Split(string(data), "\n") {
			compact := strings.ReplaceAll(line, " ", "")
			for _, bad := range []string{"|sh", "|bash", "|iex", "|Invoke-Expression"} {
				if strings.Contains(compact, bad) {
					t.Errorf("%s: %q", path, line)
				}
			}
		}
	}
}

const powershellStubs = `
function global:Invoke-RestMethod { param($Uri)
    Add-Content -Path $env:CURL_LOG -Value $Uri
    return (Get-Content -Raw (Join-Path $env:FIXTURES 'latest') | ConvertFrom-Json)
}
function global:Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing)
    Add-Content -Path $env:CURL_LOG -Value $Uri
    Copy-Item -LiteralPath (Join-Path $env:FIXTURES ($Uri -split '/')[-1]) -Destination $OutFile
}
`

type psRun struct {
	dir, curlLog string
	output       string
	err          error
}

const psArchiveName = "memoria_1.2.3_windows_amd64.zip"

// runInstallPs runs install.ps1 with -NoPathUpdate, so a test can never change
// the real user PATH.
func runInstallPs(t *testing.T, archive []byte, checksums string, extra ...string) psRun {
	t.Helper()
	return runInstallPsIn(t, archive, checksums, "", append([]string{"-NoPathUpdate"}, extra...)...)
}

// runInstallPsIn takes the InstallDir, or empty for a fresh directory.
func runInstallPsIn(t *testing.T, archive []byte, checksums, installDir string, extra ...string) psRun {
	t.Helper()
	shell, err := exec.LookPath("pwsh")
	if err != nil {
		t.Skip("pwsh is not available")
	}
	script, err := filepath.Abs(filepath.Join("..", "install.ps1"))
	if err != nil {
		t.Fatal(err)
	}
	base := t.TempDir()
	fixtures := filepath.Join(base, "fixtures")
	if err := os.MkdirAll(fixtures, 0o755); err != nil {
		t.Fatal(err)
	}
	r := psRun{dir: filepath.Join(base, "install"), curlLog: filepath.Join(base, "calls.log")}
	writeFile(t, filepath.Join(fixtures, psArchiveName), archive, 0o644)
	writeFile(t, filepath.Join(fixtures, "checksums.txt"), []byte(checksums), 0o644)
	writeFile(t, filepath.Join(fixtures, "latest"), []byte(`{"tag_name":"v1.2.3"}`), 0o644)

	wrapper := filepath.Join(base, "wrapper.ps1")
	if installDir != "" {
		r.dir = installDir
	}
	call := fmt.Sprintf("& '%s' -InstallDir '%s' %s\n", script, r.dir, strings.Join(extra, " "))
	writeFile(t, wrapper, []byte(powershellStubs+call), 0o644)

	cmd := exec.Command(shell, "-NoProfile", "-NonInteractive", "-File", wrapper)
	cmd.Env = append(os.Environ(), "FIXTURES="+fixtures, "CURL_LOG="+r.curlLog, "PROCESSOR_ARCHITECTURE=AMD64")
	out, err := cmd.CombinedOutput()
	r.output, r.err = string(out), err
	return r
}

func (r psRun) installed() string {
	data, err := os.ReadFile(filepath.Join(r.dir, "memoria.exe"))
	if err != nil {
		return ""
	}
	return string(data)
}

func goodPsFixture(t *testing.T) (archive []byte, checksums string) {
	t.Helper()
	archive = zipArchive(t, "memoria.exe", []byte(fakeBinary))
	return archive, checksumOf(archive) + "  " + psArchiveName + "\n"
}

func TestInstallPs1Pinned(t *testing.T) {
	good, sums := goodPsFixture(t)
	r := runInstallPs(t, good, sums, "-Version", "v1.2.3")
	if r.err != nil || r.installed() != fakeBinary {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
	log, _ := os.ReadFile(r.curlLog)
	if strings.Contains(string(log), "http://") || !strings.Contains(string(log), "https://github.com/mggarofalo/memoria/releases/download/v1.2.3/"+psArchiveName) {
		t.Fatalf("log = %s", log)
	}
	entries, err := os.ReadDir(r.dir)
	if err != nil || len(entries) != 1 {
		t.Fatalf("entries = %v, err = %v", entries, err)
	}
}

func TestInstallPs1Latest(t *testing.T) {
	good, sums := goodPsFixture(t)
	r := runInstallPs(t, good, sums)
	if r.err != nil || r.installed() != fakeBinary {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
}

func TestInstallPs1RefusesBadDownloads(t *testing.T) {
	good, sums := goodPsFixture(t)
	wrong := zipArchive(t, "other.exe", []byte(fakeBinary))
	duplicate := zipEntries(t, "memoria.exe", []byte("one"), "memoria.exe", []byte("two"))
	zeros := strings.Repeat("0", 64)
	cases := map[string]struct {
		archive []byte
		sums    string
		version string
		want    string
	}{
		"tampered":         {good, zeros + "  " + psArchiveName + "\n", "v1.2.3", "Checksum mismatch"},
		"missing entry":    {good, zeros + "  other.zip\n", "v1.2.3", "not listed"},
		"substring":        {good, zeros + "  " + psArchiveName + ".sbom\n", "v1.2.3", "not listed"},
		"bad version":      {good, sums, "1.2.3", "Not a release version"},
		"no binary inside": {wrong, checksumOf(wrong) + "  " + psArchiveName + "\n", "v1.2.3", "has no memoria.exe"},
		"duplicate binary": {duplicate, checksumOf(duplicate) + "  " + psArchiveName + "\n", "v1.2.3", "more than once"},
		"leading zero":     {good, sums, "v01.2.3", "Not a release version"},
		"newline suffix":   {good, sums, `"v1.2.3` + "`n" + `"`, "Not a release version"},
		"unicode digit":    {good, sums, "v" + string(rune(0x0661)) + ".2.3", "Not a release version"},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			r := runInstallPs(t, c.archive, c.sums, "-Version", c.version)
			if r.err == nil || !strings.Contains(r.output, c.want) {
				t.Fatalf("err = %v, output = %s", r.err, r.output)
			}
			if r.installed() != "" {
				t.Fatal("something was installed")
			}
		})
	}
}

func TestInstallPs1RejectsARelativeDirectory(t *testing.T) {
	good, sums := goodPsFixture(t)
	r := runInstallPsIn(t, good, sums, "relative"+string(filepath.Separator)+"dir", "-NoPathUpdate", "-Version", "v1.2.3")
	if r.err == nil || !strings.Contains(r.output, "absolute") {
		t.Fatalf("err = %v, output = %s", r.err, r.output)
	}
}
