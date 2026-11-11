package selfupdate

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

const (
	oldBinary = "old binary contents"
	newBinary = "new binary contents"
	testOS    = "linux"
	testArch  = "amd64"
)

// TestMain lets a copy of this test binary act as an installed memoria:
// MEMORIA_TEST_FAKE=version prints a version, unless it can see a secret in its
// environment; sleep stays running to hold the file open; fail exits non-zero.
func TestMain(m *testing.M) {
	switch os.Getenv("MEMORIA_TEST_FAKE") {
	case "version":
		fakeVersion()
	case "sleep":
		time.Sleep(time.Minute)
		os.Exit(0)
	case "orphan":
		fakeOrphan()
	case "fail":
		os.Exit(3)
	}
	versionEnvAllowlist = append(versionEnvAllowlist, "MEMORIA_TEST_FAKE")
	os.Exit(m.Run())
}

// fakeOrphan reports the right version but leaves a child holding stdout.
func fakeOrphan() {
	self, err := os.Executable()
	if err != nil {
		os.Exit(4)
	}
	child := exec.Command(self)
	child.Env = append(os.Environ(), "MEMORIA_TEST_FAKE=sleep")
	child.Stdout = os.Stdout
	if child.Start() != nil {
		os.Exit(5)
	}
	fmt.Println("memoria 1.2.0")
	os.Exit(0)
}

func fakeVersion() {
	if os.Getenv("MEMORIA_API_KEY") != "" || os.Getenv("GITHUB_TOKEN") != "" {
		os.Exit(9)
	}
	fmt.Println("memoria 1.2.0")
	os.Exit(0)
}

// fakeRelease describes one release on the fake server.
type fakeRelease struct {
	tag         string
	prerelease  bool
	draft       bool
	platform    string // defaults to the test platform
	payload     []byte // the binary inside the archive
	archive     []byte // overrides the built archive
	checksums   string // overrides the computed file
	noChecksums bool
	truncate    bool // die partway through the archive download
}

type fakeServer struct {
	*httptest.Server
}

func archiveFor(t *testing.T, goos string, payload []byte) []byte {
	t.Helper()
	if goos == "windows" {
		return zipOf(t, zipEntry{"memoria.exe", payload})
	}
	return tarGzOf(t, tarEntry{name: "memoria", body: payload})
}

type tarEntry struct {
	name     string
	body     []byte
	typeflag byte
	linkname string
}

func tarGzOf(t *testing.T, entries ...tarEntry) []byte {
	t.Helper()
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	for _, entry := range entries {
		writeTarEntry(t, tw, entry)
	}
	if err := tw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := gz.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func writeTarEntry(t *testing.T, tw *tar.Writer, entry tarEntry) {
	t.Helper()
	header := &tar.Header{Name: entry.name, Mode: 0o755, Size: int64(len(entry.body)), Typeflag: entry.typeflag, Linkname: entry.linkname}
	if entry.typeflag == 0 {
		header.Typeflag = tar.TypeReg
	}
	if err := tw.WriteHeader(header); err != nil {
		t.Fatal(err)
	}
	if _, err := tw.Write(entry.body); err != nil {
		t.Fatal(err)
	}
}

type zipEntry struct {
	name string
	body []byte
}

func zipOf(t *testing.T, entries ...zipEntry) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for _, entry := range entries {
		f, err := zw.Create(entry.name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := f.Write(entry.body); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func sum(data []byte) string {
	h := sha256.Sum256(data)
	return hex.EncodeToString(h[:])
}

// resolve fills in defaults and returns the archive and its name.
func (r *fakeRelease) resolve(t *testing.T, goos, arch string) (name string, archive []byte) {
	t.Helper()
	platformOS, platformArch := goos, arch
	if r.platform != "" {
		platformOS, platformArch, _ = strings.Cut(r.platform, "/")
	}
	name = assetName(strings.TrimPrefix(r.tag, "v"), platformOS, platformArch)
	archive = r.archive
	if archive == nil {
		payload := r.payload
		if payload == nil {
			payload = []byte(newBinary)
		}
		archive = archiveFor(t, platformOS, payload)
	}
	return name, archive
}

// serveFake runs a TLS server speaking enough of GitHub's API.
func serveFake(t *testing.T, goos, arch string, releases ...fakeRelease) *fakeServer {
	t.Helper()
	fake := &fakeServer{}
	mux := http.NewServeMux()
	fake.Server = httptest.NewTLSServer(mux)
	t.Cleanup(fake.Close)

	var listed []string
	for i := range releases {
		listed = append(listed, fake.addRelease(t, mux, &releases[i], goos, arch))
	}
	mux.HandleFunc("/repos/"+repoSlug+"/releases", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, "["+strings.Join(listed, ",")+"]")
	})
	return fake
}

func (f *fakeServer) addRelease(t *testing.T, mux *http.ServeMux, r *fakeRelease, goos, arch string) string {
	t.Helper()
	name, archive := r.resolve(t, goos, arch)
	checksums := r.checksums
	if checksums == "" {
		checksums = sum(archive) + "  " + name + "\n"
	}
	archivePath := fmt.Sprintf("/download/%s/%s", r.tag, name)
	checksumsPath := fmt.Sprintf("/download/%s/%s", r.tag, checksumsAsset)
	mux.HandleFunc(archivePath, func(w http.ResponseWriter, _ *http.Request) {
		if r.truncate {
			w.Header().Set("Content-Length", fmt.Sprint(len(archive)))
			_, _ = w.Write(archive[:len(archive)/2])
			return
		}
		_, _ = w.Write(archive)
	})
	mux.HandleFunc(checksumsPath, func(w http.ResponseWriter, _ *http.Request) {
		_, _ = io.WriteString(w, checksums)
	})
	assets := fmt.Sprintf(`{"name":%q,"browser_download_url":%q}`, name, f.URL+archivePath)
	if !r.noChecksums {
		assets += fmt.Sprintf(`,{"name":%q,"browser_download_url":%q}`, checksumsAsset, f.URL+checksumsPath)
	}
	return fmt.Sprintf(`{"tag_name":%q,"draft":%t,"prerelease":%t,"assets":[%s]}`, r.tag, r.draft, r.prerelease, assets)
}

func noVerify(context.Context, string, string) error { return nil }

type passingAttester struct{}

func (passingAttester) Verify(context.Context, Subject) error { return nil }

func updaterFor(f *fakeServer, opts Options) *Updater {
	opts.OS, opts.Arch = testOS, testArch
	if opts.Verify == nil {
		opts.Verify = noVerify
	}
	if opts.Attester == nil {
		opts.Attester = passingAttester{}
	}
	return newUpdater(newSourceWith(f.URL, f.Client()), opts)
}

func installedBinary(t *testing.T) string {
	t.Helper()
	exe := filepath.Join(t.TempDir(), "memoria")
	if err := os.WriteFile(exe, []byte(oldBinary), 0o755); err != nil {
		t.Fatal(err)
	}
	return exe
}

func assertBinary(t *testing.T, exe, want string) {
	t.Helper()
	got, err := os.ReadFile(exe)
	if err != nil {
		t.Fatal(err)
	}
	if string(got) != want {
		t.Fatalf("binary = %q, want %q", got, want)
	}
}

func assertDirHoldsOnly(t *testing.T, exe string, names ...string) {
	t.Helper()
	entries, err := os.ReadDir(filepath.Dir(exe))
	if err != nil {
		t.Fatal(err)
	}
	var got []string
	for _, e := range entries {
		got = append(got, e.Name())
	}
	if strings.Join(got, ",") != strings.Join(names, ",") {
		t.Fatalf("directory holds %v, want %v", got, names)
	}
}

func install(t *testing.T, fake *fakeServer, exe, tag string, opts Options) error {
	t.Helper()
	u := updaterFor(fake, opts)
	candidate, err := u.Find(context.Background(), tag)
	if err != nil {
		return err
	}
	return u.Install(context.Background(), candidate, exe)
}

func TestInstallReplacesTheBinary(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
	assertDirHoldsOnly(t, exe, "memoria")
}

func TestInstallWorksWhenTheBinaryWasRenamed(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := filepath.Join(t.TempDir(), "mem")
	if err := os.WriteFile(exe, []byte(oldBinary), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
}

func TestFindPicksTheNewestStableRelease(t *testing.T) {
	fake := serveFake(t, testOS, testArch,
		fakeRelease{tag: "v1.0.0"},
		fakeRelease{tag: "v3.0.0-rc.1", prerelease: true},
		fakeRelease{tag: "v9.0.0", draft: true},
		fakeRelease{tag: "not-a-version-9.9.9"},
		fakeRelease{tag: "v2.0.0"},
		fakeRelease{tag: "v1.5.0"},
	)
	c, err := updaterFor(fake, Options{}).Find(context.Background(), "")
	if err != nil {
		t.Fatal(err)
	}
	if c.Tag != "v2.0.0" || c.Version != "2.0.0" || c.Prerelease {
		t.Fatalf("candidate = %+v", c)
	}
	c, err = updaterFor(fake, Options{Prerelease: true}).Find(context.Background(), "")
	if err != nil {
		t.Fatal(err)
	}
	if c.Tag != "v3.0.0-rc.1" || !c.Prerelease {
		t.Fatalf("candidate = %+v", c)
	}
}

// A tag must not be able to block everyone from updating, or win forever.
func TestHostileTagsCannotWin(t *testing.T) {
	fake := serveFake(t, testOS, testArch,
		fakeRelease{tag: "v1.0.0"},
		fakeRelease{tag: "v9.9.9-evil"}, // No GitHub prerelease flag.
		fakeRelease{tag: "v18446744073709551615.0.0"},
		fakeRelease{tag: "v99999999999.0.0"},
		fakeRelease{tag: "v01.2.3"},
		fakeRelease{tag: "v1.02.3"},
	)
	c, err := updaterFor(fake, Options{}).Find(context.Background(), "")
	if err != nil {
		t.Fatal(err)
	}
	if c.Tag != "v1.0.0" {
		t.Fatalf("candidate = %+v", c)
	}
}

func TestFindPinned(t *testing.T) {
	fake := serveFake(t, testOS, testArch,
		fakeRelease{tag: "v1.0.0"},
		fakeRelease{tag: "v2.0.0"},
		fakeRelease{tag: "v3.0.0-rc.1", prerelease: true},
	)
	c, err := updaterFor(fake, Options{}).Find(context.Background(), "v1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if c.Tag != "v1.0.0" {
		t.Fatalf("candidate = %+v", c)
	}
	// A pinned prerelease is found and marked; Decide is what refuses it.
	c, err = updaterFor(fake, Options{}).Find(context.Background(), "v3.0.0-rc.1")
	if err != nil {
		t.Fatal(err)
	}
	if !c.Prerelease {
		t.Fatalf("candidate = %+v", c)
	}
	_, err = updaterFor(fake, Options{}).Find(context.Background(), "v7.7.7")
	if !errors.Is(err, ErrNoRelease) || !strings.Contains(err.Error(), "v7.7.7") {
		t.Fatalf("err = %v", err)
	}
}

func TestWrongPlatformIsRefused(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0", platform: "darwin/arm64"})
	_, err := updaterFor(fake, Options{}).Find(context.Background(), "")
	if !errors.Is(err, ErrNoRelease) || !strings.Contains(err.Error(), "linux/amd64") {
		t.Fatalf("err = %v", err)
	}
}

func TestMissingChecksumsFileIsRefused(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0", noChecksums: true})
	_, err := updaterFor(fake, Options{}).Find(context.Background(), "")
	if err == nil || !strings.Contains(err.Error(), checksumsAsset) {
		t.Fatalf("err = %v", err)
	}
}

func TestNothingChangesOnBadReleases(t *testing.T) {
	goodName := assetName("1.2.0", testOS, testArch)
	zeros := strings.Repeat("0", 64)
	corrupt := []byte("this is not a gzip stream")
	duplicate := tarGzOf(t, tarEntry{name: "memoria", body: []byte("one")}, tarEntry{name: "memoria", body: []byte("two")})
	link := tarGzOf(t, tarEntry{name: "memoria", typeflag: tar.TypeSymlink, linkname: "/etc/passwd"})
	missing := tarGzOf(t, tarEntry{name: "other", body: []byte("x")})
	cases := []struct {
		name    string
		release fakeRelease
		want    string
	}{
		{"corrupted archive with a matching checksum", fakeRelease{archive: corrupt, checksums: sum(corrupt) + "  " + goodName + "\n"}, "unpack"},
		{"tampered checksum", fakeRelease{checksums: zeros + "  " + goodName + "\n"}, "does not match"},
		{"missing checksum entry", fakeRelease{checksums: zeros + "  memoria_1.2.0_other_arch.tar.gz\n"}, "not listed"},
		{"substring of a checksum entry", fakeRelease{checksums: zeros + "  " + goodName + ".sbom\n"}, "not listed"},
		{"malformed checksum file", fakeRelease{checksums: "garbage\n"}, "not listed"},
		{"network failure mid-download", fakeRelease{truncate: true}, "EOF"},
		{"duplicate binary entries", fakeRelease{archive: duplicate}, "more than once"},
		{"binary is a symlink", fakeRelease{archive: link}, "not a regular file"},
		{"no binary", fakeRelease{archive: missing}, "no binary"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			c.release.tag = "v1.2.0"
			fake := serveFake(t, testOS, testArch, c.release)
			exe := installedBinary(t)
			verified := false
			err := install(t, fake, exe, "", Options{Verify: func(context.Context, string, string) error {
				verified = true
				return nil
			}})
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("err = %v, want something mentioning %q", err, c.want)
			}
			if verified {
				t.Fatal("a bad release must not reach the swap")
			}
			assertBinary(t, exe, oldBinary)
			assertDirHoldsOnly(t, exe, "memoria")
		})
	}
}

func TestChecksumFormats(t *testing.T) {
	digest := sha256.Sum256([]byte("x"))
	text := hex.EncodeToString(digest[:])
	for _, line := range []string{
		text + "  a.tar.gz\n",
		text + " *a.tar.gz\n",
		strings.ToUpper(text) + "  a.tar.gz\r\n",
		"junk line here\n" + text + "  a.tar.gz\n",
	} {
		if err := verifyChecksum([]byte(line), "a.tar.gz", digest[:]); err != nil {
			t.Errorf("verifyChecksum(%q) = %v", line, err)
		}
	}
}

// A 128 MiB+ binary compresses to a few hundred KB, which is the point of the cap.
func TestDecompressionBombsAreRefused(t *testing.T) {
	huge := make([]byte, maxUnpackedBytes+1)
	for name, archive := range map[string]struct {
		goos string
		data []byte
	}{
		"tar.gz":                          {"linux", tarGzOf(t, tarEntry{name: "memoria", body: huge})},
		"zip":                             {"windows", zipOf(t, zipEntry{"memoria.exe", huge})},
		"padding entry before the binary": {"linux", tarGzOf(t, tarEntry{name: "pad", body: huge}, tarEntry{name: "memoria", body: []byte("x")})},
	} {
		t.Run(name, func(t *testing.T) {
			err := extractBinary(archive.data, archive.goos, io.Discard)
			if !errors.Is(err, errTooLarge) {
				t.Fatalf("err = %v", err)
			}
		})
	}
}

// sparseTarGz builds a PAX 1.0 sparse member by hand, since archive/tar will not
// write one. It is a few hundred bytes and claims to expand to realSize.
func sparseTarGz(t *testing.T, realSize int64) []byte {
	t.Helper()
	var pax strings.Builder
	for _, record := range []string{"GNU.sparse.major=1", "GNU.sparse.minor=0", "GNU.sparse.name=memoria", fmt.Sprintf("GNU.sparse.realsize=%d", realSize)} {
		pax.WriteString(paxRecord(record))
	}
	mapBlock := make([]byte, 512)
	copy(mapBlock, "1\n0\n1\n")
	body := append(mapBlock, 'x')

	var tarball bytes.Buffer
	writeRawEntry(&tarball, "PaxHeaders/memoria", 'x', []byte(pax.String()))
	writeRawEntry(&tarball, "GNUSparseFile.0/memoria", '0', body)
	tarball.Write(make([]byte, 1024))

	var out bytes.Buffer
	gz := gzip.NewWriter(&out)
	if _, err := gz.Write(tarball.Bytes()); err != nil {
		t.Fatal(err)
	}
	if err := gz.Close(); err != nil {
		t.Fatal(err)
	}
	return out.Bytes()
}

func paxRecord(record string) string {
	size := len(record) + 3
	for len(fmt.Sprintf("%d %s\n", size, record)) != size {
		size++
	}
	return fmt.Sprintf("%d %s\n", size, record)
}

func writeRawEntry(w *bytes.Buffer, name string, typeflag byte, body []byte) {
	header := make([]byte, 512)
	copy(header, name)
	copy(header[100:], "0000755\x00")
	copy(header[108:], "0000000\x00")
	copy(header[116:], "0000000\x00")
	copy(header[124:], fmt.Sprintf("%011o\x00", len(body)))
	copy(header[136:], "00000000000\x00")
	header[156] = typeflag
	copy(header[257:], "ustar\x0000")
	copy(header[148:], "        ")
	checksum := 0
	for _, b := range header {
		checksum += int(b)
	}
	copy(header[148:], fmt.Sprintf("%06o\x00 ", checksum))
	w.Write(header)
	w.Write(body)
	if extra := len(body) % 512; extra != 0 {
		w.Write(make([]byte, 512-extra))
	}
}

// A sparse member expands while reading almost nothing, so the cap on the archive
// side never trips. The review wrote 314 MB from 218 bytes.
func TestSparseMemberIsRefusedOnItsHeader(t *testing.T) {
	var out bytes.Buffer
	err := extractBinary(sparseTarGz(t, maxUnpackedBytes+1), "linux", &out)
	if !errors.Is(err, errTooLarge) {
		t.Fatalf("err = %v", err)
	}
	if out.Len() != 0 {
		t.Fatalf("wrote %d bytes before refusing", out.Len())
	}
}

func TestSmallSparseMemberStillUnpacks(t *testing.T) {
	var out bytes.Buffer
	if err := extractBinary(sparseTarGz(t, 4096), "linux", &out); err != nil {
		t.Fatal(err)
	}
	if out.Len() != 4096 {
		t.Fatalf("len = %d", out.Len())
	}
}

type endlessZeros struct{}

func (endlessZeros) Read(p []byte) (int, error) {
	clear(p)
	return len(p), nil
}

func TestOutputIsCappedWhateverTheSource(t *testing.T) {
	counter := &countingWriter{}
	if err := copyCapped(counter, endlessZeros{}); !errors.Is(err, errTooLarge) {
		t.Fatalf("err = %v", err)
	}
	if counter.total > maxUnpackedBytes+1 {
		t.Fatalf("wrote %d", counter.total)
	}
}

type countingWriter struct{ total int64 }

func (c *countingWriter) Write(p []byte) (int, error) {
	c.total += int64(len(p))
	return len(p), nil
}

func TestZipEntryRules(t *testing.T) {
	var out bytes.Buffer
	if err := extractBinary(zipOf(t, zipEntry{"memoria.exe", []byte("good")}), "windows", &out); err != nil || out.String() != "good" {
		t.Fatalf("out = %q, err = %v", out.String(), err)
	}
	cases := map[string]struct {
		archive []byte
		want    error
	}{
		"no binary": {zipOf(t, zipEntry{"other.exe", []byte("x")}), errNoBinary},
		"duplicate": {zipOf(t, zipEntry{"memoria.exe", []byte("1")}, zipEntry{"memoria.exe", []byte("2")}), errDuplicateBinary},
		"symlink":   {zipWithSymlink(t), errBinaryNotFile},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			if err := extractBinary(c.archive, "windows", io.Discard); !errors.Is(err, c.want) {
				t.Fatalf("err = %v", err)
			}
		})
	}
	if err := extractBinary([]byte("not a zip"), "windows", io.Discard); err == nil {
		t.Fatal("expected an error")
	}
}

func zipWithSymlink(t *testing.T) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	header := &zip.FileHeader{Name: "memoria.exe"}
	header.SetMode(os.ModeSymlink | 0o777)
	f, err := zw.CreateHeader(header)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.Write([]byte("C:/Windows/System32")); err != nil {
		t.Fatal(err)
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func TestInstallKeepsTheBinarysMode(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("file modes do not apply")
	}
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	if err := os.Chmod(exe, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(exe)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o700 {
		t.Fatalf("mode = %o", info.Mode().Perm())
	}
}

// A planted symlink at a predictable staging name once let an update overwrite
// whatever it pointed at.
func TestPlantedStagingSymlinkIsNotFollowed(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	victim := filepath.Join(t.TempDir(), "victim")
	if err := os.WriteFile(victim, []byte("untouched"), 0o600); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{".memoria.new", ".memoria.old"} {
		if err := os.Symlink(victim, filepath.Join(filepath.Dir(exe), name)); err != nil {
			t.Skip("symlinks unavailable:", err)
		}
	}
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
	assertBinary(t, victim, "untouched")
}

func TestManagedPaths(t *testing.T) {
	for path, want := range map[string]bool{
		"/usr/bin/memoria": true,
		"/opt/homebrew/Cellar/memoria/1.0/bin/memoria":                            true,
		"/usr/local/Cellar/memoria/1.0/bin/memoria":                               true,
		"/nix/store/abc-memoria/bin/memoria":                                      true,
		`C:\Program Files\memoria\memoria.exe`:                                    true,
		`C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\memoria\memoria.exe`: true,
		`C:\Users\me\scoop\apps\memoria\current\memoria.exe`:                      true,
		`C:\PROGRAM FILES\memoria\memoria.exe`:                                    true,
		`c:\users\me\appdata\local\microsoft\winget\packages\memoria\memoria.exe`: true,
		"/home/me/.linuxbrew/bin/memoria":                                         true,
		"/home/me/.asdf/installs/memoria/1.0/bin/memoria":                         true,
		"/home/me/.local/share/mise/installs/memoria/1/memoria":                   true,
		"/usr/local/bin/memoria":                                                  false,
		"/home/me/.local/bin/memoria":                                             false,
		`C:\Users\me\AppData\Local\memoria\memoria.exe`:                           false,
	} {
		if got := managedPath(path); got != want {
			t.Errorf("managedPath(%q) = %v", path, got)
		}
	}
}

func TestPackageManagedBinaryNeedsForce(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "Program Files")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	exe := filepath.Join(dir, "memoria")
	if err := os.WriteFile(exe, []byte(oldBinary), 0o755); err != nil {
		t.Fatal(err)
	}
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	if err := install(t, fake, exe, "", Options{}); !errors.Is(err, ErrManaged) {
		t.Fatalf("err = %v", err)
	}
	assertBinary(t, exe, oldBinary)
	if err := install(t, fake, exe, "", Options{Force: true}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
}

func TestServerDownWhileListing(t *testing.T) {
	fake := serveFake(t, testOS, testArch)
	u := updaterFor(fake, Options{})
	fake.Close()
	if _, err := u.Find(context.Background(), ""); err == nil {
		t.Fatal("expected an error")
	}
}

func TestServerGoneBeforeTheDownload(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	u := updaterFor(fake, Options{})
	c, err := u.Find(context.Background(), "")
	if err != nil {
		t.Fatal(err)
	}
	fake.Close()
	exe := installedBinary(t)
	if err := u.Install(context.Background(), c, exe); err == nil {
		t.Fatal("expected an error")
	}
	assertBinary(t, exe, oldBinary)
}

func TestBadListingResponses(t *testing.T) {
	for name, c := range map[string]struct {
		status int
		body   string
		want   string
	}{
		"bad listing body": {200, "{not a list", "not a release list"},
		"error status":     {403, "", "403"},
	} {
		t.Run(name, func(t *testing.T) {
			server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.WriteHeader(c.status)
				_, _ = io.WriteString(w, c.body)
			}))
			defer server.Close()
			_, err := newSourceWith(server.URL, server.Client()).list(context.Background())
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("err = %v", err)
			}
		})
	}
}

func TestChecksumsDownloadFailureIsReported(t *testing.T) {
	mux := http.NewServeMux()
	server := httptest.NewTLSServer(mux)
	defer server.Close()
	mux.HandleFunc("/archive", func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("x")) })
	mux.HandleFunc("/sums", func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusNotFound) })
	u := newUpdater(newSourceWith(server.URL, server.Client()), Options{OS: testOS, Arch: testArch, Attester: passingAttester{}, Verify: noVerify})
	candidate := &Candidate{Tag: "v1.2.0", Asset: "a", assetURL: server.URL + "/archive", checksumsURL: server.URL + "/sums"}
	err := u.Install(context.Background(), candidate, installedBinary(t))
	if err == nil || !strings.Contains(err.Error(), checksumsAsset) {
		t.Fatalf("err = %v", err)
	}
}

func TestHTTPSOnly(t *testing.T) {
	var hits int
	plain := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits++ }))
	defer plain.Close()

	t.Run("plain http api", func(t *testing.T) {
		if _, err := newSourceWith(plain.URL, plain.Client()).list(context.Background()); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("redirect down to http", func(t *testing.T) {
		tlsServer := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, plain.URL+"/asset", http.StatusFound)
		}))
		defer tlsServer.Close()
		src := newSourceWith(tlsServer.URL, tlsServer.Client())
		if _, err := src.fetchAll(context.Background(), tlsServer.URL+"/asset", 1024); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("transport refuses http directly", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodGet, plain.URL, nil)
		if _, err := (httpsOnlyTransport{base: http.DefaultTransport}).RoundTrip(req); !errors.Is(err, ErrInsecureURL) { //nolint:bodyclose // refused before any request, so there is no response
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("unparseable url", func(t *testing.T) {
		src := newSourceWith("https://unused.invalid", http.DefaultClient)
		if _, err := src.fetch(context.Background(), "https://bad host/\x7f"); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	if hits != 0 {
		t.Fatalf("the plain http server was contacted %d times", hits)
	}
}

func TestNoCredentialsAreSent(t *testing.T) {
	t.Setenv("GITHUB_TOKEN", "must-not-be-sent")
	var got http.Header
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Clone()
		_, _ = io.WriteString(w, "[]")
	}))
	defer server.Close()
	if _, err := newSourceWith(server.URL, server.Client()).list(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got.Get("Authorization") != "" {
		t.Fatalf("Authorization = %q", got.Get("Authorization"))
	}
}

func TestRedirectLimit(t *testing.T) {
	var server *httptest.Server
	server = httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, server.URL+"/again", http.StatusFound)
	}))
	defer server.Close()
	src := newSourceWith(server.URL, server.Client())
	if _, err := src.fetch(context.Background(), server.URL); err == nil {
		t.Fatal("expected a redirect limit")
	}
}

func TestRollbackWhenTheNewBinaryFails(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	var ran string
	err := install(t, fake, exe, "", Options{Verify: func(_ context.Context, path, version string) error {
		ran = path + "@" + version
		assertBinary(t, path, newBinary) // The new binary is in place when verified.
		return errors.New("exit status 1")
	}})
	if err == nil || !strings.Contains(err.Error(), "restored") {
		t.Fatalf("err = %v", err)
	}
	if ran != exe+"@1.2.0" {
		t.Fatalf("verified %q", ran)
	}
	assertBinary(t, exe, oldBinary)
	assertDirHoldsOnly(t, exe, "memoria")
}

func TestRollbackFailureNamesTheBackup(t *testing.T) {
	exe := installedBinary(t)
	backup := backupPath(exe)
	// With no backup on disk the restore cannot complete.
	err := rollback(exe, backup, errors.New("boom"))
	if err == nil || !strings.Contains(err.Error(), backup) || !strings.Contains(err.Error(), "boom") {
		t.Fatalf("err = %v", err)
	}
}

func TestStaleBackupIsReplaced(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	if err := os.WriteFile(backupPath(exe), []byte("stale"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
	assertDirHoldsOnly(t, exe, "memoria")
}

func TestInstallOfAMissingBinaryFails(t *testing.T) {
	u := updaterFor(serveFake(t, testOS, testArch), Options{})
	if err := u.Install(context.Background(), &Candidate{}, filepath.Join(t.TempDir(), "nope")); err == nil {
		t.Fatal("expected a missing-binary error")
	}
}

func TestStageFailsWhenTheDirectoryIsGone(t *testing.T) {
	if _, err := stageBinary(nil, testOS, filepath.Join(t.TempDir(), "gone", "memoria")); err == nil {
		t.Fatal("expected an error")
	}
}

func TestSwapFailureLeavesTheBinaryInPlace(t *testing.T) {
	exe := installedBinary(t)
	err := swapIn(filepath.Join(t.TempDir(), "no-such-staged-file"), exe, backupPath(exe))
	if err == nil {
		t.Fatal("expected an error")
	}
	assertBinary(t, exe, oldBinary)
}

func TestInstallFollowsSymlinks(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	link := filepath.Join(t.TempDir(), "memoria-link")
	if err := os.Symlink(exe, link); err != nil {
		t.Skip("symlinks unavailable:", err)
	}
	if err := install(t, fake, link, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
	info, err := os.Lstat(link)
	if err != nil || info.Mode()&os.ModeSymlink == 0 {
		t.Fatalf("the symlink should survive: %v", err)
	}
}

func copyOfThisBinary(t *testing.T, dir, name string) string {
	t.Helper()
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, data, 0o755); err != nil {
		t.Fatal(err)
	}
	return path
}

// TestReplacesARunningBinary is the real thing: a live process holds the file,
// which on Windows forbids overwriting or deleting it, and the verification step
// actually executes the new binary, with secrets in the parent's environment.
func TestReplacesARunningBinary(t *testing.T) {
	dir := t.TempDir()
	exe := copyOfThisBinary(t, dir, binaryName(runtime.GOOS))

	running := exec.Command(exe)
	running.Env = append(os.Environ(), "MEMORIA_TEST_FAKE=sleep")
	if err := running.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		_ = running.Process.Kill()
		_ = running.Wait()
	})

	payload, err := os.ReadFile(exe)
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("MEMORIA_TEST_FAKE", "version")
	t.Setenv("MEMORIA_API_KEY", "must-not-reach-the-child")
	t.Setenv("GITHUB_TOKEN", "must-not-reach-the-child")

	goos, arch := runtime.GOOS, runtime.GOARCH
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0", payload: payload})
	u := newUpdater(newSourceWith(fake.URL, fake.Client()), Options{OS: goos, Arch: arch, Attester: passingAttester{}})
	c, err := u.Find(context.Background(), "")
	if err != nil {
		t.Fatal(err)
	}
	if err := u.Install(context.Background(), c, exe); err != nil {
		t.Fatal(err)
	}
	if running.ProcessState != nil {
		t.Fatal("the running process should be unaffected")
	}
	if _, err := os.Stat(backupPath(exe)); runtime.GOOS != "windows" && !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("backup left behind: %v", err)
	}

	// The next swap clears a backup the running process kept alive.
	_ = running.Process.Kill()
	_ = running.Wait()
	_ = os.Remove(backupPath(exe))
	if _, err := os.Stat(backupPath(exe)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stale backup not removed: %v", err)
	}
}

func TestRunVersion(t *testing.T) {
	exe := copyOfThisBinary(t, t.TempDir(), binaryName(runtime.GOOS))

	t.Setenv("MEMORIA_TEST_FAKE", "version")
	if err := RunVersion(context.Background(), exe, "1.2.0"); err != nil {
		t.Fatal(err)
	}
	if err := RunVersion(context.Background(), exe, "9.9.9"); err == nil {
		t.Fatal("a different version should fail")
	}
	t.Setenv("MEMORIA_TEST_FAKE", "fail")
	if err := RunVersion(context.Background(), exe, "1.2.0"); err == nil {
		t.Fatal("a failing binary should fail")
	}
}

// Waiting on a pipe a leftover child still holds once blocked the rollback.
func TestLeftoverChildCannotBlockTheCheck(t *testing.T) {
	// Not t.TempDir: the leftover child keeps the binary open past the test's end,
	// and on Windows that makes the directory impossible to remove.
	dir, err := os.MkdirTemp("", "memoria-orphan")
	if err != nil {
		t.Fatal(err)
	}
	exe := copyOfThisBinary(t, dir, binaryName(runtime.GOOS))
	t.Setenv("MEMORIA_TEST_FAKE", "orphan")
	start := time.Now()
	if err := RunVersion(context.Background(), exe, "1.2.0"); err == nil {
		t.Fatal("a binary leaving a child behind should fail the check")
	}
	if elapsed := time.Since(start); elapsed > 20*time.Second {
		t.Fatalf("took %s", elapsed)
	}
}

func TestReportsVersion(t *testing.T) {
	for output, want := range map[string]bool{
		"memoria 1.2.0\n":  true,
		"memoria v1.2.0\n": true,
		"memoria 1.2.01\n": false,
		"memoria 11.2.0\n": false,
		"memoria dev\n":    false,
		"":                 false,
	} {
		if got := reportsVersion(output, "1.2.0"); got != want {
			t.Errorf("reportsVersion(%q) = %v", output, got)
		}
	}
}

func TestScrubbedEnvironmentIsAnAllowlist(t *testing.T) {
	got := scrubbedEnviron([]string{
		"PATH=/bin", "Path=C:/x", "SystemRoot=C:/Windows",
		"MEMORIA_API_KEY=secret", "GITHUB_TOKEN=secret", "AWS_SECRET_ACCESS_KEY=secret", "HOME=/home/me", "SSL_CERT_FILE=/x",
	})
	if strings.Join(got, ",") != "PATH=/bin,Path=C:/x,SystemRoot=C:/Windows" {
		t.Fatalf("got %v", got)
	}
}

func TestAssetName(t *testing.T) {
	if got := assetName("1.2.0", "linux", "amd64"); got != "memoria_1.2.0_linux_amd64.tar.gz" {
		t.Errorf("got %s", got)
	}
	if got := assetName("1.2.0", "windows", "arm64"); got != "memoria_1.2.0_windows_arm64.zip" {
		t.Errorf("got %s", got)
	}
}

func TestDecide(t *testing.T) {
	cases := []struct {
		name               string
		current, candidate string
		policy             Policy
		want               Action
	}{
		{"newer", "1.0.0", "v1.1.0", Policy{}, ActionUpdate},
		{"same", "1.1.0", "v1.1.0", Policy{}, ActionUpToDate},
		{"same with a v", "v1.1.0", "v1.1.0", Policy{}, ActionUpToDate},
		{"numeric not lexical", "1.9.0", "v1.10.0", Policy{}, ActionUpdate},
		{"latest older than this build", "2.0.0", "v1.0.0", Policy{}, ActionUpToDate},
		{"pinned older", "2.0.0", "v1.0.0", Policy{Pinned: true}, ActionRefuse},
		{"pinned older allowed", "2.0.0", "v1.0.0", Policy{Pinned: true, AllowDowngrade: true}, ActionUpdate},
		{"pinned same", "1.0.0", "v1.0.0", Policy{Pinned: true}, ActionUpToDate},
		{"prerelease refused", "1.0.0", "v2.0.0-rc.1", Policy{}, ActionRefuse},
		{"pinned prerelease refused", "1.0.0", "v2.0.0-rc.1", Policy{Pinned: true}, ActionRefuse},
		{"prerelease allowed", "1.0.0", "v2.0.0-rc.1", Policy{AllowPrerelease: true}, ActionUpdate},
		{"from prerelease to its final", "2.0.0-rc.1", "v2.0.0", Policy{}, ActionUpdate},
		{"dev refused", "dev", "v1.0.0", Policy{}, ActionRefuse},
		{"dev forced", "dev", "v1.0.0", Policy{Force: true}, ActionUpdate},
		{"unreadable version refused", "garbage", "v1.0.0", Policy{}, ActionRefuse},
		{"unreadable candidate", "1.0.0", "vnope", Policy{Force: true}, ActionRefuse},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := Decide(c.current, c.candidate, c.policy)
			if got.Action != c.want {
				t.Fatalf("Decide = %+v, want %v", got, c.want)
			}
			if c.want == ActionRefuse && got.Reason == "" {
				t.Fatal("a refusal needs a reason")
			}
		})
	}
}

func TestDecideCandidateHonorsTheGitHubFlag(t *testing.T) {
	flagged := &Candidate{Tag: "v2.0.0", Prerelease: true}
	if got := DecideCandidate("1.0.0", flagged, Policy{}); got.Action != ActionRefuse {
		t.Fatalf("decision = %+v", got)
	}
	if got := DecideCandidate("1.0.0", flagged, Policy{AllowPrerelease: true}); got.Action != ActionUpdate {
		t.Fatalf("decision = %+v", got)
	}
}

func TestDevRefusalExplainsForce(t *testing.T) {
	got := Decide(DevVersion, "v1.0.0", Policy{})
	if !strings.Contains(got.Reason, "--force") || !strings.Contains(got.Reason, "development build") {
		t.Fatalf("reason = %q", got.Reason)
	}
}

func TestValidTag(t *testing.T) {
	for _, tag := range []string{"v1.2.3", "v0.0.1", "v1.2.3-rc.1", "v999999999.0.0"} {
		if !ValidTag(tag) {
			t.Errorf("%q should be valid", tag)
		}
	}
	for _, tag := range []string{
		"", "1.2.3", "v1.2", "cli-v1.2.3", "v1.2.3;rm", "v1.2.3\n", "v01.2.3", "v1.02.3", "v1.2.03",
		"v18446744073709551615.0.0", "v1000000000.0.0", "v1.2.3-01", "v1.2.3-" + strings.Repeat("a", 65),
	} {
		if ValidTag(tag) {
			t.Errorf("%q should be invalid", tag)
		}
	}
}

func TestNewerThan(t *testing.T) {
	older, newer := &Candidate{Tag: "v1.0.0"}, &Candidate{Tag: "v1.10.0"}
	bad := &Candidate{Tag: "nope"}
	if !newerThan(newer, older) || newerThan(older, newer) || !newerThan(older, nil) {
		t.Fatal("ordering is wrong")
	}
	if newerThan(bad, older) || !newerThan(older, bad) {
		t.Fatal("an unparseable version should lose")
	}
}

func TestNewUsesTheRealAPI(t *testing.T) {
	u := New(Options{})
	if u.source.apiBase != defaultAPIBase || !strings.HasPrefix(defaultAPIBase, "https://") {
		t.Fatalf("apiBase = %q", u.source.apiBase)
	}
	if u.opts.OS != runtime.GOOS || u.opts.Arch != runtime.GOARCH {
		t.Fatal("platform should default to the running one")
	}
	if u.opts.Attester == nil || u.opts.Verify == nil {
		t.Fatal("verification must default to on")
	}
}
