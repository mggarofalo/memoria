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
)

// TestMain lets a copy of this test binary act as an installed memoria:
// MEMORIA_TEST_FAKE=version prints a version, MEMORIA_TEST_FAKE=sleep stays
// running to hold the file open, and MEMORIA_TEST_FAKE=fail exits non-zero.
func TestMain(m *testing.M) {
	switch os.Getenv("MEMORIA_TEST_FAKE") {
	case "version":
		fmt.Println("memoria 1.2.0")
		os.Exit(0)
	case "sleep":
		time.Sleep(time.Minute)
		os.Exit(0)
	case "fail":
		os.Exit(3)
	}
	os.Exit(m.Run())
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
	downloads []string
}

func testPlatform() (goos, arch string) { return "linux", "amd64" }

func archiveFor(t *testing.T, goos string, payload []byte) []byte {
	t.Helper()
	if goos == "windows" {
		return zipOf(t, "memoria.exe", payload)
	}
	return tarGzOf(t, "memoria", payload)
}

func tarGzOf(t *testing.T, name string, payload []byte) []byte {
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

func zipOf(t *testing.T, name string, payload []byte) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	f, err := zw.Create(name)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := f.Write(payload); err != nil {
		t.Fatal(err)
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

func assetExtension(goos string) string {
	if goos == "windows" {
		return "zip"
	}
	return "tar.gz"
}

// release fills in defaults and returns the archive and its name.
func (r *fakeRelease) resolve(t *testing.T, goos, arch string) (name string, archive []byte) {
	t.Helper()
	platformOS, platformArch := goos, arch
	if r.platform != "" {
		platformOS, platformArch, _ = strings.Cut(r.platform, "/")
	}
	name = fmt.Sprintf("memoria_%s_%s_%s.%s", strings.TrimPrefix(r.tag, "v"), platformOS, platformArch, assetExtension(platformOS))
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
		listed = append(listed, fake.addRelease(t, mux, &releases[i], int64(i+1), goos, arch))
	}
	mux.HandleFunc("/repos/"+repoSlug+"/releases", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, "["+strings.Join(listed, ",")+"]")
	})
	return fake
}

func (f *fakeServer) addRelease(t *testing.T, mux *http.ServeMux, r *fakeRelease, id int64, goos, arch string) string {
	t.Helper()
	name, archive := r.resolve(t, goos, arch)
	checksums := r.checksums
	if checksums == "" {
		checksums = sum(archive) + "  " + name + "\n"
	}
	archivePath := fmt.Sprintf("/download/%s/%s", r.tag, name)
	checksumsPath := fmt.Sprintf("/download/%s/%s", r.tag, checksumsAsset)
	mux.HandleFunc(archivePath, func(w http.ResponseWriter, _ *http.Request) {
		f.downloads = append(f.downloads, name)
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
	assets := fmt.Sprintf(`{"id":%d,"name":%q,"size":%d,"browser_download_url":%q}`, id*10, name, len(archive), f.URL+archivePath)
	if !r.noChecksums {
		assets += fmt.Sprintf(`,{"id":%d,"name":%q,"size":%d,"browser_download_url":%q}`, id*10+1, checksumsAsset, len(checksums), f.URL+checksumsPath)
	}
	return fmt.Sprintf(`{"id":%d,"tag_name":%q,"draft":%t,"prerelease":%t,"assets":[%s]}`, id, r.tag, r.draft, r.prerelease, assets)
}

func noVerify(context.Context, string, string) error { return nil }

type passingAttester struct{}

func (passingAttester) Verify(context.Context, []byte, string) error { return nil }

func updaterFor(f *fakeServer, opts Options) *Updater {
	opts.OS, opts.Arch = testPlatform()
	if opts.Verify == nil {
		opts.Verify = noVerify
	}
	if opts.Attester == nil && !opts.ChecksumOnly {
		opts.Attester = passingAttester{}
	}
	return newUpdater(newSourceWith(f.URL, f.Client()), opts)
}

func installedBinary(t *testing.T) string {
	t.Helper()
	// The library looks in the archive for the file named like the target.
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
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	if err := install(t, fake, exe, "", Options{}); err != nil {
		t.Fatal(err)
	}
	assertBinary(t, exe, newBinary)
	assertDirHoldsOnly(t, exe, "memoria")
}

func TestFindPicksTheNewestStableRelease(t *testing.T) {
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch,
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

func TestFindPinned(t *testing.T) {
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch,
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
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0", platform: "darwin/arm64"})
	_, err := updaterFor(fake, Options{}).Find(context.Background(), "")
	if !errors.Is(err, ErrNoRelease) || !strings.Contains(err.Error(), "linux/amd64") {
		t.Fatalf("err = %v", err)
	}
}

func TestNothingChangesOnBadReleases(t *testing.T) {
	goos, arch := testPlatform()
	goodName := fmt.Sprintf("memoria_1.2.0_%s_%s.tar.gz", goos, arch)
	corrupt := []byte("this is not a gzip stream")
	cases := []struct {
		name    string
		release fakeRelease
		want    string
	}{
		{"corrupted archive with a matching checksum", fakeRelease{archive: corrupt, checksums: sum(corrupt) + "  " + goodName + "\n"}, "decompress"},
		{"tampered checksum", fakeRelease{checksums: strings.Repeat("0", 64) + "  " + goodName + "\n"}, "sha256"},
		{"missing checksum entry", fakeRelease{checksums: strings.Repeat("0", 64) + "  memoria_1.2.0_other_arch.tar.gz\n"}, "hash"},
		{"malformed checksum file", fakeRelease{checksums: "garbage\n"}, "checksum"},
		{"no checksums file", fakeRelease{noChecksums: true}, "checksums.txt"},
		{"network failure mid-download", fakeRelease{truncate: true}, "EOF"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			c.release.tag = "v1.2.0"
			fake := serveFake(t, goos, arch, c.release)
			exe := installedBinary(t)
			verified := false
			err := install(t, fake, exe, "", Options{Verify: func(context.Context, string, string) error {
				verified = true
				return nil
			}})
			if err == nil || !strings.Contains(strings.ToLower(err.Error()), strings.ToLower(c.want)) {
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

func TestServerFailures(t *testing.T) {
	goos, arch := testPlatform()
	t.Run("server down while listing", func(t *testing.T) {
		fake := serveFake(t, goos, arch)
		u := updaterFor(fake, Options{})
		fake.Close()
		if _, err := u.Find(context.Background(), ""); err == nil {
			t.Fatal("expected an error")
		}
	})
	t.Run("server gone before the download", func(t *testing.T) {
		fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0"})
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
	})
	t.Run("bad listing body", func(t *testing.T) {
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, "{not a list")
		}))
		defer server.Close()
		src := newSourceWith(server.URL, server.Client())
		if _, err := src.ListReleases(context.Background(), nil); err == nil {
			t.Fatal("expected an error")
		}
	})
	t.Run("error status", func(t *testing.T) {
		server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			w.WriteHeader(http.StatusForbidden)
		}))
		defer server.Close()
		src := newSourceWith(server.URL, server.Client())
		_, err := src.ListReleases(context.Background(), nil)
		if err == nil || !strings.Contains(err.Error(), "403") {
			t.Fatalf("err = %v", err)
		}
	})
}

func TestHTTPSOnly(t *testing.T) {
	var hits int
	plain := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { hits++ }))
	defer plain.Close()

	t.Run("plain http api", func(t *testing.T) {
		src := newSourceWith(plain.URL, plain.Client())
		if _, err := src.ListReleases(context.Background(), nil); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("plain http asset", func(t *testing.T) {
		src := newSourceWith("https://unused.invalid", http.DefaultClient)
		src.assets[1] = plain.URL + "/asset"
		if _, err := src.DownloadReleaseAsset(context.Background(), nil, 1); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("redirect down to http", func(t *testing.T) {
		tlsServer := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, plain.URL+"/asset", http.StatusFound)
		}))
		defer tlsServer.Close()
		src := newSourceWith(tlsServer.URL, tlsServer.Client())
		src.assets[1] = tlsServer.URL + "/asset"
		if _, err := src.DownloadReleaseAsset(context.Background(), nil, 1); !errors.Is(err, ErrInsecureURL) {
			t.Fatalf("err = %v", err)
		}
	})
	t.Run("transport refuses http directly", func(t *testing.T) {
		req := httptest.NewRequest(http.MethodGet, plain.URL, nil)
		if _, err := (httpsOnlyTransport{base: http.DefaultTransport}).RoundTrip(req); !errors.Is(err, ErrInsecureURL) {
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

func TestRedirectLimitAndUnknownAsset(t *testing.T) {
	var server *httptest.Server
	server = httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, server.URL+"/again", http.StatusFound)
	}))
	defer server.Close()
	src := newSourceWith(server.URL, server.Client())
	if _, err := src.fetch(context.Background(), server.URL); err == nil {
		t.Fatal("expected a redirect limit")
	}
	if _, err := src.DownloadReleaseAsset(context.Background(), nil, 42); err == nil {
		t.Fatal("expected an unknown asset error")
	}
}

func TestOversizedDownloadFails(t *testing.T) {
	body := &cappedBody{ReadCloser: io.NopCloser(strings.NewReader("0123456789")), remaining: 4}
	if _, err := io.ReadAll(body); err == nil || !strings.Contains(err.Error(), "size limit") {
		t.Fatalf("err = %v", err)
	}
}

func TestRollbackWhenTheNewBinaryFails(t *testing.T) {
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0"})
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
	assertBinary(t, exe, oldBinary) // The failed binary went back rather than vanishing.

	missing := filepath.Join(t.TempDir(), "memoria")
	if err := rollback(missing, backupPath(missing), errors.New("boom")); err == nil {
		t.Fatal("expected an error")
	}
}

func TestStaleBackupIsReplaced(t *testing.T) {
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0"})
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

func TestInstallErrors(t *testing.T) {
	u := updaterFor(serveFake(t, "linux", "amd64"), Options{})
	missing := filepath.Join(t.TempDir(), "nope")
	if err := u.Install(context.Background(), &Candidate{}, missing); err == nil {
		t.Fatal("expected a missing-binary error")
	}
}

func TestInstallFollowsSymlinks(t *testing.T) {
	goos, arch := testPlatform()
	fake := serveFake(t, goos, arch, fakeRelease{tag: "v1.2.0"})
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

func exeName() string {
	if runtime.GOOS == "windows" {
		return "memoria.exe"
	}
	return "memoria"
}

// TestReplacesARunningBinary is the real thing: a live process holds the file,
// which on Windows forbids overwriting or deleting it, and the verification step
// actually executes the new binary.
func TestReplacesARunningBinary(t *testing.T) {
	dir := t.TempDir()
	exe := copyOfThisBinary(t, dir, exeName())

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

	// The next update clears a backup the running process kept alive.
	_ = running.Process.Kill()
	_ = running.Wait()
	removeStale(backupPath(exe))
	if _, err := os.Stat(backupPath(exe)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("stale backup not removed: %v", err)
	}
}

func TestRunVersion(t *testing.T) {
	exe := copyOfThisBinary(t, t.TempDir(), exeName())

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

func TestEnvironWithoutRemovesTheKey(t *testing.T) {
	got := environWithout([]string{"PATH=/bin", "MEMORIA_API_KEY=secret", "memoria_api_key=secret", "MEMORIA_URL=x"}, "MEMORIA_API_KEY")
	if strings.Join(got, ",") != "PATH=/bin,MEMORIA_URL=x" {
		t.Fatalf("got %v", got)
	}
}

func TestContractAssets(t *testing.T) {
	cases := map[string]bool{
		"checksums.txt":                         true,
		"memoria_1.2.0_linux_amd64.tar.gz":      true,
		"memoria_1.2.0_darwin_arm64.tar.gz":     true,
		"memoria_1.2.0_windows_amd64.zip":       true,
		"memoria_1.2.0_windows_arm64.zip":       true,
		"memoria_1.2.0_windows_amd64.tar.gz":    false,
		"memoria_1.2.0_linux_amd64.zip":         false,
		"memoria_9.9.9_linux_amd64.tar.gz":      false,
		"memoria_1.2.0_linux_amd64.tar.gz.sbom": false,
		"other_1.2.0_linux_amd64.tar.gz":        false,
		"memoria_1.2.0_plan9_amd64.tar.gz":      false,
	}
	for name, want := range cases {
		if got := isContractAsset(name, "1.2.0"); got != want {
			t.Errorf("isContractAsset(%q) = %v", name, got)
		}
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
	for _, tag := range []string{"v1.2.3", "v0.0.1", "v1.2.3-rc.1"} {
		if !ValidTag(tag) {
			t.Errorf("%q should be valid", tag)
		}
	}
	for _, tag := range []string{"", "1.2.3", "v1.2", "cli-v1.2.3", "v1.2.3;rm", "v1.2.3\n"} {
		if ValidTag(tag) {
			t.Errorf("%q should be invalid", tag)
		}
	}
}

func TestNewUsesTheRealAPI(t *testing.T) {
	u := New(Options{})
	if u.source.apiBase != defaultAPIBase || !strings.HasPrefix(defaultAPIBase, "https://") {
		t.Fatalf("apiBase = %q", u.source.apiBase)
	}
	if u.platformOS() != runtime.GOOS || u.platformArch() != runtime.GOARCH {
		t.Fatal("platform should default to the running one")
	}
}
