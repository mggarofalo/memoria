package selfupdate

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/golang/snappy"
	"github.com/sigstore/sigstore-go/pkg/bundle"
	"github.com/sigstore/sigstore-go/pkg/root"
	"github.com/sigstore/sigstore-go/pkg/tuf"
	"github.com/sigstore/sigstore-go/pkg/verify"
)

const (
	githubOIDCIssuer = "https://token.actions.githubusercontent.com"
	slsaProvenance   = "https://slsa.dev/provenance/v1"
	// releaseIdentity is who may sign: this repository's release workflow,
	// running for the tag being installed.
	releaseIdentity = "https://github.com/" + repoSlug + "/.github/workflows/release.yml@refs/tags/"
	maxBundleBytes  = 16 << 20
)

// ErrAttestation means an archive has no provenance this CLI can verify.
var ErrAttestation = errors.New("provenance attestation check failed")

// Attester decides whether an archive with this digest was built by the release
// workflow for this tag.
type Attester interface {
	Verify(ctx context.Context, digest []byte, tag string) error
}

// sigstoreAttester checks GitHub artifact attestations, which is what stops an
// attacker who can replace both an archive and checksums.txt on a release. They
// would also need the workflow's own signing identity. It does not defend against
// a compromised workflow, which can sign whatever it builds.
type sigstoreAttester struct {
	source   *source
	repo     string
	trusted  func() (root.TrustedMaterial, error)
	identity func(tag string) (verify.CertificateIdentity, error)
}

func newSigstoreAttester(src *source) *sigstoreAttester {
	return &sigstoreAttester{source: src, repo: repoSlug, trusted: fetchTrustedRoot, identity: releaseCertificateIdentity}
}

// fetchTrustedRoot gets Sigstore's current keys through TUF, whose own root ships
// inside sigstore-go, so that key rotation does not need a new CLI. Nothing is
// cached on disk.
func fetchTrustedRoot() (root.TrustedMaterial, error) {
	return root.FetchTrustedRootWithOptions(tuf.DefaultOptions().WithDisableLocalCache())
}

// releaseCertificateIdentity requires the exact tag, so an old signed build
// cannot be served as a newer release.
func releaseCertificateIdentity(tag string) (verify.CertificateIdentity, error) {
	return verify.NewShortCertificateIdentity(githubOIDCIssuer, "", releaseIdentity+tag, "")
}

// Verify implements Attester.
func (a *sigstoreAttester) Verify(ctx context.Context, digest []byte, tag string) error {
	bundles, err := a.fetchBundles(ctx, digest)
	if err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	trusted, err := a.trusted()
	if err != nil {
		return fmt.Errorf("%w: cannot get the Sigstore trust root: %v", ErrAttestation, err)
	}
	identity, err := a.identity(tag)
	if err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	entities := make([]verify.SignedEntity, 0, len(bundles))
	for _, b := range bundles {
		entities = append(entities, b)
	}
	if err := verifyEntities(entities, digest, trusted, identity, publicGoodOptions()...); err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	return nil
}

// publicGoodOptions are what an attestation from a public GitHub repository must
// carry: a certificate timestamp, a transparency log entry, and a time from it.
func publicGoodOptions() []verify.VerifierOption {
	return []verify.VerifierOption{
		verify.WithSignedCertificateTimestamps(1),
		verify.WithTransparencyLog(1),
		verify.WithObserverTimestamps(1),
	}
}

// verifyEntities passes if any one entity is valid SLSA provenance for digest
// from identity. A digest can have several attestations, such as an SBOM too.
func verifyEntities(entities []verify.SignedEntity, digest []byte, trusted root.TrustedMaterial, identity verify.CertificateIdentity, options ...verify.VerifierOption) error {
	verifier, err := verify.NewVerifier(trusted, options...)
	if err != nil {
		return err
	}
	policy := verify.NewPolicy(verify.WithArtifactDigest("sha256", digest), verify.WithCertificateIdentity(identity))
	lastErr := errors.New("no attestation exists for this archive")
	for _, entity := range entities {
		result, err := verifier.Verify(entity, policy)
		if err != nil {
			lastErr = err
			continue
		}
		if result.Statement == nil || result.Statement.PredicateType != slsaProvenance {
			lastErr = errors.New("the attestation is not SLSA provenance")
			continue
		}
		return nil
	}
	return lastErr
}

type attestationList struct {
	Attestations []struct {
		Bundle    json.RawMessage `json:"bundle"`
		BundleURL string          `json:"bundle_url"`
	} `json:"attestations"`
}

func (a *sigstoreAttester) fetchBundles(ctx context.Context, digest []byte) ([]*bundle.Bundle, error) {
	listURL := a.source.apiBase + "/repos/" + a.repo + "/attestations/sha256:" + hex.EncodeToString(digest)
	data, err := a.source.fetchAll(ctx, listURL, maxBundleBytes)
	if err != nil {
		return nil, fmt.Errorf("fetching attestations: %w", err)
	}
	var list attestationList
	if err := json.Unmarshal(data, &list); err != nil {
		return nil, errors.New("the attestation list is not valid JSON")
	}
	bundles := make([]*bundle.Bundle, 0, len(list.Attestations))
	for _, item := range list.Attestations {
		raw, err := a.bundleJSON(ctx, item.Bundle, item.BundleURL)
		if err != nil {
			return nil, err
		}
		var parsed bundle.Bundle
		if err := parsed.UnmarshalJSON(raw); err != nil {
			return nil, errors.New("an attestation bundle is malformed")
		}
		bundles = append(bundles, &parsed)
	}
	return bundles, nil
}

// bundleJSON returns a bundle given inline, or downloads it. GitHub serves the
// download snappy-compressed, and currently leaves the inline field null.
func (a *sigstoreAttester) bundleJSON(ctx context.Context, inline json.RawMessage, url string) ([]byte, error) {
	if len(inline) > 0 && !bytes.Equal(inline, []byte("null")) {
		return inline, nil
	}
	if url == "" {
		return nil, errors.New("an attestation has no bundle")
	}
	compressed, err := a.source.fetchAll(ctx, url, maxBundleBytes)
	if err != nil {
		return nil, fmt.Errorf("fetching an attestation bundle: %w", err)
	}
	return decodeBundle(compressed)
}

func decodeBundle(compressed []byte) ([]byte, error) {
	size, err := snappy.DecodedLen(compressed)
	if err != nil || size > maxBundleBytes {
		return nil, errors.New("an attestation bundle is malformed or too large")
	}
	data, err := snappy.Decode(nil, compressed)
	if err != nil {
		return nil, errors.New("an attestation bundle is malformed")
	}
	return data, nil
}
