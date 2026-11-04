package selfupdate

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/golang/snappy"
	"github.com/sigstore/sigstore-go/pkg/root"
	"github.com/sigstore/sigstore-go/pkg/testing/ca"
	"github.com/sigstore/sigstore-go/pkg/verify"
)

const (
	testIdentity = releaseIdentity + "v1.2.0"
	testDigest   = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
)

func decodedDigest(t *testing.T, value string) []byte {
	t.Helper()
	digest, err := hex.DecodeString(value)
	if err != nil {
		t.Fatal(err)
	}
	return digest
}

func statement(predicate, digest string) []byte {
	return []byte(fmt.Sprintf(`{"_type":"https://in-toto.io/Statement/v1","subject":[{"name":"memoria.tar.gz","digest":{"sha256":%q}}],"predicateType":%q,"predicate":{}}`, digest, predicate))
}

func signedBy(t *testing.T, signer *ca.VirtualSigstore, identity, issuer string, body []byte) verify.SignedEntity {
	t.Helper()
	entity, err := signer.Attest(identity, issuer, body)
	if err != nil {
		t.Fatal(err)
	}
	return entity
}

func releaseIdentityFor(t *testing.T, tag string) verify.CertificateIdentity {
	t.Helper()
	identity, err := releaseCertificateIdentity(tag)
	if err != nil {
		t.Fatal(err)
	}
	return identity
}

// virtualOptions drop the certificate timestamp, which the test CA cannot issue,
// so the cases below differ from success only in what each one changes. The real
// fixture covers the full production options.
func virtualOptions() []verify.VerifierOption {
	return []verify.VerifierOption{verify.WithTransparencyLog(1), verify.WithObserverTimestamps(1)}
}

func TestVerifyEntities(t *testing.T) {
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	stranger, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	good := statement(slsaProvenance, testDigest)
	otherDigest := strings.Repeat("a", 64)
	cases := []struct {
		name   string
		entity func() verify.SignedEntity
		ok     bool
	}{
		{"the release workflow for this tag", func() verify.SignedEntity { return signedBy(t, signer, testIdentity, githubOIDCIssuer, good) }, true},
		{"another tag, replaying an old build", func() verify.SignedEntity {
			return signedBy(t, signer, releaseIdentity+"v1.1.0", githubOIDCIssuer, good)
		}, false},
		{"another workflow in this repository", func() verify.SignedEntity {
			return signedBy(t, signer, "https://github.com/"+repoSlug+"/.github/workflows/evil.yml@refs/tags/v1.2.0", githubOIDCIssuer, good)
		}, false},
		{"another repository", func() verify.SignedEntity {
			return signedBy(t, signer, "https://github.com/attacker/memoria/.github/workflows/release.yml@refs/tags/v1.2.0", githubOIDCIssuer, good)
		}, false},
		{"another OIDC issuer", func() verify.SignedEntity {
			return signedBy(t, signer, testIdentity, "https://accounts.example.com", good)
		}, false},
		{"a different digest", func() verify.SignedEntity {
			return signedBy(t, signer, testIdentity, githubOIDCIssuer, statement(slsaProvenance, otherDigest))
		}, false},
		{"not provenance", func() verify.SignedEntity {
			return signedBy(t, signer, testIdentity, githubOIDCIssuer, statement("https://spdx.dev/Document/v2.3", testDigest))
		}, false},
		{"signed outside the trust root", func() verify.SignedEntity { return signedBy(t, stranger, testIdentity, githubOIDCIssuer, good) }, false},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			err := verifyEntities([]verify.SignedEntity{c.entity()}, decodedDigest(t, testDigest), signer, releaseIdentityFor(t, "v1.2.0"), virtualOptions()...)
			if (err == nil) != c.ok {
				t.Fatalf("err = %v, want ok = %v", err, c.ok)
			}
		})
	}
}

func TestVerifyEntitiesPassesIfAnyOneIsValid(t *testing.T) {
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	sbom := signedBy(t, signer, testIdentity, githubOIDCIssuer, statement("https://spdx.dev/Document/v2.3", testDigest))
	provenance := signedBy(t, signer, testIdentity, githubOIDCIssuer, statement(slsaProvenance, testDigest))
	err = verifyEntities([]verify.SignedEntity{sbom, provenance}, decodedDigest(t, testDigest), signer, releaseIdentityFor(t, "v1.2.0"), virtualOptions()...)
	if err != nil {
		t.Fatal(err)
	}
}

func TestProductionOptionsRequireACertificateTimestamp(t *testing.T) {
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	entity := signedBy(t, signer, testIdentity, githubOIDCIssuer, statement(slsaProvenance, testDigest))
	err = verifyEntities([]verify.SignedEntity{entity}, decodedDigest(t, testDigest), signer, releaseIdentityFor(t, "v1.2.0"), publicGoodOptions()...)
	if err == nil {
		t.Fatal("expected a refusal")
	}
}

func TestBadVerifierOptionsAreAnError(t *testing.T) {
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	// With no timestamps required, the verifier refuses to be built at all.
	if err := verifyEntities(nil, decodedDigest(t, testDigest), signer, releaseIdentityFor(t, "v1.2.0")); err == nil {
		t.Fatal("expected an error")
	}
}

func TestVerifyEntitiesRefusesNone(t *testing.T) {
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	err = verifyEntities(nil, decodedDigest(t, testDigest), signer, releaseIdentityFor(t, "v1.2.0"), virtualOptions()...)
	if err == nil || !strings.Contains(err.Error(), "no attestation") {
		t.Fatalf("err = %v", err)
	}
}

// realFixture holds attestations GitHub really issued for a gh CLI release, and
// a snapshot of Sigstore's trust root. It proves the wire format and the real
// trust chain with no network.
type realFixture struct {
	digest  []byte
	trusted root.TrustedMaterial
}

func loadRealFixture(t *testing.T) realFixture {
	t.Helper()
	digest, err := os.ReadFile(filepath.Join("testdata", "gh-digest.txt"))
	if err != nil {
		t.Fatal(err)
	}
	trusted, err := root.NewTrustedRootFromPath(filepath.Join("testdata", "trusted_root.json"))
	if err != nil {
		t.Fatal(err)
	}
	return realFixture{digest: decodedDigest(t, strings.TrimSpace(string(digest))), trusted: trusted}
}

func readFixtureBundle(t *testing.T, index int) []byte {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("testdata", fmt.Sprintf("gh-attestation-%d.bundle.snappy", index)))
	if err != nil {
		t.Fatal(err)
	}
	return data
}

// fixtureServer serves the fixture's attestations the way GitHub does: a list
// with null bundles, and compressed downloads.
func fixtureServer(t *testing.T) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	server := httptest.NewTLSServer(mux)
	t.Cleanup(server.Close)
	mux.HandleFunc("/repos/cli/cli/attestations/", func(w http.ResponseWriter, _ *http.Request) {
		_, _ = fmt.Fprintf(w, `{"attestations":[{"bundle":null,"bundle_url":%q},{"bundle":null,"bundle_url":%q}]}`, server.URL+"/blob/0", server.URL+"/blob/1")
	})
	for i := range 2 {
		data := readFixtureBundle(t, i)
		mux.HandleFunc(fmt.Sprintf("/blob/%d", i), func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write(data) })
	}
	return server
}

func ghAttester(t *testing.T, server *httptest.Server, trusted root.TrustedMaterial, sanRegex string) *sigstoreAttester {
	t.Helper()
	return &sigstoreAttester{
		source:  newSourceWith(server.URL, server.Client()),
		repo:    "cli/cli",
		trusted: func() (root.TrustedMaterial, error) { return trusted, nil },
		identity: func(string) (verify.CertificateIdentity, error) {
			return verify.NewShortCertificateIdentity(githubOIDCIssuer, "", "", sanRegex)
		},
	}
}

func TestRealAttestationVerifiesOffline(t *testing.T) {
	fixture := loadRealFixture(t)
	server := fixtureServer(t)
	attester := ghAttester(t, server, fixture.trusted, `^https://github\.com/cli/cli/`)
	if err := attester.Verify(context.Background(), fixture.digest, "v2.103.3"); err != nil {
		t.Fatal(err)
	}
}

func TestRealAttestationRefusesOurIdentity(t *testing.T) {
	fixture := loadRealFixture(t)
	server := fixtureServer(t)
	attester := ghAttester(t, server, fixture.trusted, `^https://github\.com/`+repoSlug+`/`)
	if err := attester.Verify(context.Background(), fixture.digest, "v2.103.3"); !errors.Is(err, ErrAttestation) {
		t.Fatalf("err = %v", err)
	}
}

func TestRealAttestationRefusesAnotherDigest(t *testing.T) {
	fixture := loadRealFixture(t)
	server := fixtureServer(t)
	attester := ghAttester(t, server, fixture.trusted, `^https://github\.com/cli/cli/`)
	if err := attester.Verify(context.Background(), decodedDigest(t, testDigest), "v2.103.3"); !errors.Is(err, ErrAttestation) {
		t.Fatalf("err = %v", err)
	}
}

// TestLiveTrustRoot fetches the real trust root over TUF. It needs a network, so
// it is opt-in.
func TestLiveTrustRoot(t *testing.T) {
	if os.Getenv("MEMORIA_TEST_LIVE_SIGSTORE") != "1" {
		t.Skip("set MEMORIA_TEST_LIVE_SIGSTORE=1 to fetch the Sigstore trust root")
	}
	fixture := loadRealFixture(t)
	server := fixtureServer(t)
	trusted, err := fetchTrustedRoot()
	if err != nil {
		t.Fatal(err)
	}
	attester := ghAttester(t, server, trusted, `^https://github\.com/cli/cli/`)
	if err := attester.Verify(context.Background(), fixture.digest, "v2.103.3"); err != nil {
		t.Fatal(err)
	}
}

// attesterFailureCase serves one bad response at the attestation list URL.
func attesterAgainst(t *testing.T, handler http.HandlerFunc) *sigstoreAttester {
	t.Helper()
	server := httptest.NewTLSServer(handler)
	t.Cleanup(server.Close)
	signer, err := ca.NewVirtualSigstore()
	if err != nil {
		t.Fatal(err)
	}
	return &sigstoreAttester{
		source:   newSourceWith(server.URL, server.Client()),
		repo:     repoSlug,
		trusted:  func() (root.TrustedMaterial, error) { return signer, nil },
		identity: releaseCertificateIdentity,
	}
}

func TestAttesterRefusesBadResponses(t *testing.T) {
	cases := map[string]http.HandlerFunc{
		"not found": func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusNotFound) },
		"no attestations": func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, `{"attestations":[]}`)
		},
		"bad list": func(w http.ResponseWriter, _ *http.Request) { _, _ = io.WriteString(w, `{not json`) },
		"no bundle at all": func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, `{"attestations":[{"bundle":null,"bundle_url":""}]}`)
		},
		"malformed inline bundle": func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, `{"attestations":[{"bundle":{"mediaType":"nope"}}]}`)
		},
		"bundle url is plain http": func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, `{"attestations":[{"bundle":null,"bundle_url":"http://127.0.0.1:1/x"}]}`)
		},
	}
	for name, handler := range cases {
		t.Run(name, func(t *testing.T) {
			attester := attesterAgainst(t, handler)
			err := attester.Verify(context.Background(), decodedDigest(t, testDigest), "v1.2.0")
			if !errors.Is(err, ErrAttestation) {
				t.Fatalf("err = %v", err)
			}
		})
	}
}

func TestAttesterRefusesWithoutATrustRoot(t *testing.T) {
	attester := attesterAgainst(t, func(w http.ResponseWriter, _ *http.Request) {
		_, _ = io.WriteString(w, `{"attestations":[]}`)
	})
	attester.trusted = func() (root.TrustedMaterial, error) { return nil, errors.New("tuf is down") }
	err := attester.Verify(context.Background(), decodedDigest(t, testDigest), "v1.2.0")
	if !errors.Is(err, ErrAttestation) || !strings.Contains(err.Error(), "tuf is down") {
		t.Fatalf("err = %v", err)
	}
}

func TestAttesterRefusesWithoutAnIdentity(t *testing.T) {
	attester := attesterAgainst(t, func(w http.ResponseWriter, _ *http.Request) {
		_, _ = io.WriteString(w, `{"attestations":[]}`)
	})
	attester.identity = func(string) (verify.CertificateIdentity, error) {
		return verify.CertificateIdentity{}, errors.New("no identity")
	}
	if err := attester.Verify(context.Background(), decodedDigest(t, testDigest), "v1.2.0"); !errors.Is(err, ErrAttestation) {
		t.Fatalf("err = %v", err)
	}
}

func TestDecodeBundleRefusesBadData(t *testing.T) {
	if _, err := decodeBundle([]byte{0xff, 0xff, 0xff, 0xff, 0xff, 0xff}); err == nil {
		t.Error("garbage should fail")
	}
	// A header promising more than the limit is refused before allocating.
	bomb := snappy.Encode(nil, make([]byte, maxBundleBytes+1))
	if _, err := decodeBundle(bomb); err == nil {
		t.Error("an oversized bundle should fail")
	}
	// A valid header followed by a truncated body.
	good := snappy.Encode(nil, []byte(strings.Repeat("attestation", 100)))
	if _, err := decodeBundle(good[:len(good)/2]); err == nil {
		t.Error("a truncated bundle should fail")
	}
}

func TestFetchAllRefusesOversizedResponses(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write(make([]byte, 2048))
	}))
	defer server.Close()
	src := newSourceWith(server.URL, server.Client())
	if _, err := src.fetchAll(context.Background(), server.URL, 1024); err == nil {
		t.Fatal("expected a size failure")
	}
	if data, err := src.fetchAll(context.Background(), server.URL, 2048); err != nil || len(data) != 2048 {
		t.Fatalf("len = %d, err = %v", len(data), err)
	}
}

func TestFetchAllReportsAResponseCutShort(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Length", "100")
		_, _ = w.Write([]byte("short"))
	}))
	defer server.Close()
	src := newSourceWith(server.URL, server.Client())
	if _, err := src.fetchAll(context.Background(), server.URL, 1024); err == nil {
		t.Fatal("expected a read failure")
	}
}

func TestInlineBundleIsUsedWithoutADownload(t *testing.T) {
	attester := attesterAgainst(t, func(http.ResponseWriter, *http.Request) { t.Error("no download expected") })
	got, err := attester.bundleJSON(context.Background(), json.RawMessage(`{"a":1}`), "https://unused.invalid")
	if err != nil || string(got) != `{"a":1}` {
		t.Fatalf("got %s, err = %v", got, err)
	}
}

// failingAttester stands for any provenance failure.
type failingAttester struct{ calls *int }

func (f failingAttester) Verify(context.Context, []byte, string) error {
	*f.calls++
	return fmt.Errorf("%w: no signature", ErrAttestation)
}

func TestProvenanceFailureRefusesTheUpdate(t *testing.T) {
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	calls := 0
	err := install(t, fake, exe, "", Options{Attester: failingAttester{calls: &calls}})
	if !errors.Is(err, ErrAttestation) || calls != 1 {
		t.Fatalf("err = %v, calls = %d", err, calls)
	}
	assertBinary(t, exe, oldBinary)
	assertDirHoldsOnly(t, exe, "memoria")
}

func TestChecksumOnlySkipsProvenanceButNotTheChecksum(t *testing.T) {
	calls := 0
	opts := Options{ChecksumOnly: true, Attester: failingAttester{calls: &calls}}

	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0"})
	exe := installedBinary(t)
	if err := install(t, fake, exe, "", opts); err != nil || calls != 0 {
		t.Fatalf("err = %v, calls = %d", err, calls)
	}
	assertBinary(t, exe, newBinary)

	name := assetName("1.2.0", testOS, testArch)
	tampered := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0", checksums: strings.Repeat("0", 64) + "  " + name + "\n"})
	exe = installedBinary(t)
	if err := install(t, tampered, exe, "", opts); err == nil {
		t.Fatal("--checksum-only must still verify the checksum")
	}
	assertBinary(t, exe, oldBinary)
}

func TestChecksumFailureNeverReachesProvenance(t *testing.T) {
	name := assetName("1.2.0", testOS, testArch)
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0", checksums: strings.Repeat("0", 64) + "  " + name + "\n"})
	calls := 0
	if err := install(t, fake, installedBinary(t), "", Options{Attester: failingAttester{calls: &calls}}); err == nil || calls != 0 {
		t.Fatalf("err = %v, calls = %d", err, calls)
	}
}

// recordingAttester captures what the update asked it to verify.
type recordingAttester struct {
	digest []byte
	tag    string
}

func (r *recordingAttester) Verify(_ context.Context, digest []byte, tag string) error {
	r.digest, r.tag = digest, tag
	return nil
}

func TestProvenanceIsCheckedForTheDownloadedArchiveAndTag(t *testing.T) {
	archive := archiveFor(t, testOS, []byte(newBinary))
	fake := serveFake(t, testOS, testArch, fakeRelease{tag: "v1.2.0", archive: archive})
	recorder := &recordingAttester{}
	if err := install(t, fake, installedBinary(t), "", Options{Attester: recorder}); err != nil {
		t.Fatal(err)
	}
	if hex.EncodeToString(recorder.digest) != sum(archive) || recorder.tag != "v1.2.0" {
		t.Fatalf("digest = %x, tag = %q", recorder.digest, recorder.tag)
	}
}

func TestNewAttesterRequiresTheReleaseWorkflow(t *testing.T) {
	attester := newSigstoreAttester(newSource())
	if attester.repo != repoSlug || attester.source == nil {
		t.Fatalf("attester = %+v", attester)
	}
	want := "https://github.com/mggarofalo/memoria/.github/workflows/release.yml@refs/tags/"
	if releaseIdentity != want {
		t.Fatalf("identity = %q", releaseIdentity)
	}
}
