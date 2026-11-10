# Releasing Memoria

The whole repository has one version, and the only place it is written down is a git tag, `vX.Y.Z`. To cut a release, use the release skill (`.claude/skills/release/SKILL.md`). It runs the steps below and asks a human before it pushes the tag. The `release tags` ruleset restricts creating, updating and deleting `refs/tags/v*`, so pushing or removing a tag needs an admin's credential.

## Computing the version

`node scripts/release/release.ts next` reads the commits since the highest `vX.Y.Z` tag reachable from `HEAD`. It uses only git, so the same history always gives the same answer.

| Commits since the last tag | Next version |
|---|---|
| `!` after the type, or a `BREAKING CHANGE:` footer | major |
| `feat` | minor |
| `fix` or `perf` | patch |
| anything else | nothing to release |

The strongest commit decides. Since `main` is squash-merged, the pull request title usually becomes the commit subject, so that title decides what the change does to the version.

### Before 1.0

- **The first tag is `v0.1.0`,** whatever the history holds, as long as something in it is releasable.
- **While the major version is 0, a breaking change bumps the minor version.** Otherwise the first `!` would make a release 1.0.0 by accident.
- **`v1.0.0` is a human decision.** It is the one tag the workflow accepts that is not the computed version, and only when the previous tag is 0.x. After that, the table applies as written.

### Cutting the first tag

Nothing about the first tag is special beyond the rules above. Run the skill on a green `main`: it computes `v0.1.0`, and the workflow publishes short notes from `scripts/release/first-release-notes.md`. Read that file first and update it if the release's contents have changed.

## What a tag stamps

| Component | How it gets the version | Without a tag |
|---|---|---|
| `Memoria.Api` | `dotnet publish -p:Version=X.Y.Z`, reported by `/health` and the MCP server | `0.0.0-dev+<commit>` from a plain `dotnet build` |
| Docker image | build argument `MEMORIA_VERSION`, from `release.ts describe` | see below |
| `memoria` CLI | `-ldflags -X .../cmd.Version=X.Y.Z`, reported by `memoria --version` | `dev` from a plain `go build` |
| `memoria-qmd` | `dist/VERSION` in its release package | see below |
| `memoria-worker` | `src/VERSION` in its release package | see below |

The sidecar and worker read a `VERSION` file beside their code first and need no git once installed. `git describe` is only the fallback for a development checkout. The file sits in the code directory because the service installers copy that directory to a location with no `.git`. For an install from a checkout rather than a package, `npm run build` writes `sidecar/dist/VERSION`, and `worker/scripts/install-service.ps1` stamps the copy.

`release.ts describe` and a development checkout of the sidecar or worker all map `git describe` the same way:

| Checkout | Version |
|---|---|
| exactly on `v1.2.3` | `1.2.3` |
| 5 commits after `v1.2.3` | `1.2.4-dev.5+g<sha>` |
| no tag reachable | `0.0.0-dev+g<sha>` |
| uncommitted changes | a dev form ending in `.dirty` |
| no git and no stamp | `0.0.0-dev+unknown` |

A dev version sorts after the release it follows and before the next one. To stamp a local API or CLI build the same way, pass `release.ts describe`'s output as `-p:Version=` or as the `-X` value.

Nothing else holds a version. `package.json` says `0.0.0`, the csproj says `0.0.0-dev`, and the sidecar's `openapi.json` carries the contract version `v1`. `scripts/release/test/policy.test.ts` fails if a real version number comes back.

## What the tag triggers

`.github/workflows/release.yml`, on `v*.*.*`:

1. **Validates the tag** (`release.ts validate`). It must be strict `vX.Y.Z`, on `origin/main`, and the computed version, and every status check the `main` ruleset requires must have succeeded on the commit. The list of checks is read from the ruleset, so it follows any change there.
2. **Builds the assets:** the CLI for Windows, Linux, and macOS on amd64 and arm64, and the sidecar and worker packages.
3. **Proves them reproducible** by building everything again with an empty compiler cache in another directory, then comparing the checksums.
4. **Checks the stamps:** the checksums, the Linux binary's `--version`, the service packages installed outside a checkout (`scripts/release/verify-services.sh`), and the API's `/health` when built with the same version.
5. **Attests build provenance,** writes the notes, and runs `gh release create --verify-tag`. Only this job can write, and it runs only after the steps above.

`validate` reads the required checks with the workflow's `GITHUB_TOKEN`, under `contents: read` and `checks: read`. If either read fails, or the ruleset appears to require nothing, the tag is refused. Pull request dry runs call `release.ts read-checks` under the same permissions, so a change in what that token can read shows up before a release does.

`.github/workflows/docker.yml` runs the same validation on the same tag, so a tag the release refuses publishes no image either. Its jobs hold only what each one needs:

| Job | Runs on | Permissions | Does |
|---|---|---|---|
| `Validate Release Tag` | tags | `contents: read`, `checks: read` | the gate above |
| `Docker Image` | everything | `contents: read` | builds locally and checks `/health`; publishes nothing |
| `Publish Image` | `main`, tags | `contents: read`, `packages: write` | pushes by digest with no tag, runs that exact digest and checks `/health`, then points the tags at the digest and confirms each one resolves to it |
| `Attest Image Provenance` | tags | `contents: read`, `id-token: write`, `attestations: write` | attests the verified digest |

A pull request that touches the CLI, the services, or the release tooling runs the `release.yml` build job with the version `0.0.0-dryrun`, stopping before anything is published.

## Asset contract

Other tools depend on these names. Changing them is a breaking change.

| Asset | Contents |
|---|---|
| `memoria_<version>_{linux,darwin}_{amd64,arm64}.tar.gz` | `memoria` |
| `memoria_<version>_windows_{amd64,arm64}.zip` | `memoria.exe` |
| `memoria-sidecar_<version>.tar.gz` | `dist/` including `dist/VERSION`, `package.json`, `package-lock.json`, `.env.example`, `README.md`, `scripts/install-service.ps1`, `scripts/uninstall-service.ps1`, `scripts/patch-qmd.ts`, `deploy/windows/MemoriaService.psm1` |
| `memoria-worker_<version>.tar.gz` | `src/` including `src/VERSION`, `scripts/`, `package.json`, `package-lock.json`, `README.md`, `deploy/windows/MemoriaService.psm1` |
| `checksums.txt` | SHA-256 of each archive above, in `sha256sum` format |

`<version>` has no leading `v`. Files sit at the archive's root, with no enclosing directory. The CLI's self-updater looks for `memoria_<version>_<os>_<arch>`; the service packages start `memoria-`, so they can never match it. `scripts/release/test/assets.test.ts` pins that.

## Installing the sidecar and worker

Extract a package into an empty directory, on a host with Node 22.18 or newer. Neither needs git or a checkout once installed.

The **worker** has no runtime dependencies, so it is ready as soon as it is extracted: `node src/main.ts`.

The **sidecar's** native dependencies (`better-sqlite3`, `node-llama-cpp`, and others) are built for one platform and Node version, so the package does not contain them. They are installed from the lockfile on the host, as part of installing, while the directory is still writable. This needs network access and may compile native code, and it must be repeated after upgrading Node.

```bash
cd <install-dir>
npm ci --omit=dev
node dist/main.js
```

**As a Windows service,** run the packaged installer, which copies the service to its read-only location and runs `npm ci --omit=dev` there. The sidecar's installer needs `-NoBuild`, because a package is already built and holds no compiler configuration:

```powershell
.\scripts\install-service.ps1 -NoBuild ...   # sidecar
.\scripts\install-service.ps1 ...            # worker
```

[deployment.md](deployment.md) covers the other parameters and the service account. Its `npm audit` checklist applies to these directories too.

## Reproducible builds

The CLI is built with `CGO_ENABLED=0 -trimpath -buildvcs=false -ldflags "-s -w -buildid= -X ..."`. The service packages are built from `git archive` of the commit, not the working tree, with the sidecar compiled by the TypeScript version in its lockfile. `scripts/release/assets.ts` writes the archives itself so they hold only each file's name, mode, size, and the commit's timestamp. They carry no build-host user, timezone, or tool version.

The same commit gives the same bytes with the same Go version, which `cli/go.mod` pins, and the same Node major version, whose bundled zlib compresses the archives.

To check a release yourself:

```bash
git checkout vX.Y.Z
node scripts/release/release.ts build X.Y.Z --out /tmp/rebuild
diff /tmp/rebuild/checksums.txt <(gh release download vX.Y.Z --pattern checksums.txt --output -)
```

## Release notes

The notes must never carry a tracker reference, because this repository is public and some early commit subjects and pull request titles hold them. History is not being rewritten, so:

- **The first release** uses the written `scripts/release/first-release-notes.md`, not a list generated over the whole history.
- **Later releases** use GitHub's generated notes for pull requests since the previous tag only.
- **Every release** passes `release.ts notes`, which refuses to write notes containing a tracker reference, stopping the workflow before anything is published. If this happens, fix the pull request title and re-run.

## Testing the release tooling

```bash
node --test "scripts/release/test/**/*.test.ts"
```

It has no dependencies. `npm test` and `npm run typecheck` in `sidecar/` include it. CI runs `npm run test:coverage` in `scripts/release/`, with its gates in `scripts/release/.c8rc.json`, and the release workflow runs its tests again before building.
