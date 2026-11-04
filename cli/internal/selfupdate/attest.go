package selfupdate

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"slices"

	"github.com/golang/snappy"
	intoto "github.com/in-toto/attestation/go/v1"
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

// Subject is what an attestation must vouch for.
type Subject struct {
	Digest []byte
	Tag    string
	Asset  string
}

// Attester decides whether an archive was built by the release workflow for its tag.
type Attester interface {
	Verify(ctx context.Context, subject Subject) error
}

// sigstoreAttester checks GitHub artifact attestations, which is what stops an
// attacker who can replace both an archive and checksums.txt on a release. They
// would also need the workflow's own signing identity. It does not defend against
// a compromised workflow, which can sign whatever it builds.
type sigstoreAttester struct {
	source   *source
	repo     string
	repoURI  string
	trusted  func() (root.TrustedMaterial, error)
	identity func(tag string) (verify.CertificateIdentity, error)
}

func newSigstoreAttester(src *source) *sigstoreAttester {
	return &sigstoreAttester{
		source:   src,
		repo:     repoSlug,
		repoURI:  "https://github.com/" + repoSlug,
		trusted:  fetchTrustedRoot,
		identity: releaseCertificateIdentity,
	}
}

// trustedRootOptions keep the trust root out of ~/.sigstore: a cache there is state
// another process could change, and a fresh fetch is signed anyway.
func trustedRootOptions() *tuf.Options {
	return tuf.DefaultOptions().WithDisableLocalCache()
}

// fetchTrustedRoot gets Sigstore's current keys through TUF, whose own root ships
// inside sigstore-go, so that key rotation does not need a new CLI.
func fetchTrustedRoot() (root.TrustedMaterial, error) {
	return root.FetchTrustedRootWithOptions(trustedRootOptions())
}

// releaseCertificateIdentity requires the exact tag, so an old signed build
// cannot be served as a newer release.
func releaseCertificateIdentity(tag string) (verify.CertificateIdentity, error) {
	return verify.NewShortCertificateIdentity(githubOIDCIssuer, "", releaseIdentity+tag, "")
}

// Verify implements Attester.
func (a *sigstoreAttester) Verify(ctx context.Context, subject Subject) error {
	bundles, err := a.fetchBundles(ctx, subject.Digest)
	if err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	trusted, err := a.trustedWithin(ctx)
	if err != nil {
		return fmt.Errorf("%w: cannot get the Sigstore trust root: %v", ErrAttestation, err)
	}
	identity, err := a.identity(subject.Tag)
	if err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	entities := make([]verify.SignedEntity, 0, len(bundles))
	for _, b := range bundles {
		entities = append(entities, b)
	}
	want := expectation{subject: subject, repoURI: a.repoURI}
	if err := verifyEntities(entities, want, trusted, identity, publicGoodOptions()...); err != nil {
		return fmt.Errorf("%w: %v", ErrAttestation, err)
	}
	return nil
}

// trustedWithin keeps Ctrl-C working: the TUF client takes no context, and can
// retry for up to a minute.
func (a *sigstoreAttester) trustedWithin(ctx context.Context) (root.TrustedMaterial, error) {
	type outcome struct {
		trusted root.TrustedMaterial
		err     error
	}
	done := make(chan outcome, 1)
	go func() {
		trusted, err := a.trusted()
		done <- outcome{trusted, err}
	}()
	select {
	case result := <-done:
		return result.trusted, result.err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
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

// expectation is what a verified attestation must also say.
type expectation struct {
	subject Subject
	// repoURI is the certificate's source repository. Only tests leave it empty,
	// because the test CA's certificates have no such extension.
	repoURI string
}

// verifyEntities passes if any one entity is valid SLSA provenance for the subject
// from identity. A digest can have several attestations, such as an SBOM too.
func verifyEntities(entities []verify.SignedEntity, want expectation, trusted root.TrustedMaterial, identity verify.CertificateIdentity, options ...verify.VerifierOption) error {
	verifier, err := verify.NewVerifier(trusted, options...)
	if err != nil {
		return err
	}
	policy := verify.NewPolicy(verify.WithArtifactDigest("sha256", want.subject.Digest), verify.WithCertificateIdentity(identity))
	lastErr := errors.New("no attestation exists for this archive")
	for _, entity := range entities {
		result, err := verifier.Verify(entity, policy)
		if err == nil {
			err = want.check(result)
		}
		if err == nil {
			return nil
		}
		lastErr = err
	}
	return lastErr
}

// check covers what the signature policy does not: that this is provenance, that it
// names this archive and not only its digest, and that it comes from this repository.
func (w expectation) check(result *verify.VerificationResult) error {
	statement := result.Statement
	if statement == nil || statement.PredicateType != slsaProvenance {
		return errors.New("the attestation is not SLSA provenance")
	}
	if statement.Predicate == nil || len(statement.Predicate.GetFields()) == 0 {
		return errors.New("the attestation has an empty predicate")
	}
	if !namesAsset(statement.Subject, w.subject) {
		return errors.New("the attestation does not name this archive")
	}
	return w.checkRepository(result)
}

func (w expectation) checkRepository(result *verify.VerificationResult) error {
	if w.repoURI == "" {
		return nil
	}
	if result.Signature == nil || result.Signature.Certificate == nil ||
		result.Signature.Certificate.SourceRepositoryURI != w.repoURI {
		return errors.New("the attestation is not from this repository")
	}
	return nil
}

func namesAsset(subjects []*intoto.ResourceDescriptor, want Subject) bool {
	digest := hex.EncodeToString(want.Digest)
	return slices.ContainsFunc(subjects, func(subject *intoto.ResourceDescriptor) bool {
		return subject.GetName() == want.Asset && subject.GetDigest()["sha256"] == digest
	})
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
