# Releasing Memoria

The whole repository has one version, and the only place it is written down is a git tag, `vX.Y.Z`. To cut a release, use the release skill (`.claude/skills/release/SKILL.md`). It runs the steps below and asks a human before it pushes the tag.

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
| `memoria-qmd` | `npm run build` writes `dist/VERSION` from `git describe`; `node src/main.ts` asks git directly | see below |
| `memoria-worker` | runs from source, so it asks git directly at startup | see below |

`release.ts describe` and the sidecar and worker all map `git describe` the same way:

| Checkout | Version |
|---|---|
| exactly on `v1.2.3` | `1.2.3` |
| 5 commits after `v1.2.3` | `1.2.4-dev.5+g<sha>` |
| no tag reachable | `0.0.0-dev+g<sha>` |
| uncommitted changes | a dev form ending in `.dirty` |
| no git and no stamp | `0.0.0-dev+unknown` |

A dev version sorts after the release it follows and before the next one. To stamp a local API or CLI build the same way, pass `release.ts describe`'s output as `-p:Version=` or as the `-X` value.

To run a released sidecar or worker, check out the tag on that host, then build the sidecar. A checkout's sidecar reports the version from when `npm run build` last ran, and a worker reports the checkout's version at startup. A service account needs `git` on its `PATH` for the worker to report anything but `unknown`.

Nothing else holds a version. `package.json` says `0.0.0`, the csproj says `0.0.0-dev`, and the sidecar's `openapi.json` carries the contract version `v1`. `scripts/release/test/policy.test.ts` fails if a real version number comes back.

## What the tag triggers

`.github/workflows/release.yml`, on `v*.*.*`:

1. **Validates the tag** (`release.ts validate`). It must be strict `vX.Y.Z`, on `origin/main`, and the computed version, and every status check the `main` ruleset requires must have succeeded on the commit. The list of checks is read from the ruleset, so it follows any change there.
2. **Builds the CLI** for Windows, Linux, and macOS on amd64 and arm64.
3. **Proves it reproducible** by building everything again with an empty compiler cache in another directory, then comparing the checksums.
4. **Checks the stamps:** the checksums, the Linux binary's `--version`, and the API's `/health` when built with the same version.
5. **Attests build provenance,** writes the notes, and runs `gh release create --verify-tag`. Only this job can write, and it runs only after the steps above.

`.github/workflows/docker.yml` runs the same validation on the same tag, so a tag the release refuses publishes no image either. It builds the image with the derived version, checks that the running container's `/health` reports it, and pushes `X.Y.Z` and `X.Y`.

A pull request that touches the CLI or the release tooling runs the `release.yml` build job with the version `0.0.0-dryrun`, stopping before anything is published.

## Asset contract

The CLI's self-updater depends on these names. Changing them is a breaking change.

| Asset | Contents |
|---|---|
| `memoria_<version>_{linux,darwin}_{amd64,arm64}.tar.gz` | `memoria` |
| `memoria_<version>_windows_{amd64,arm64}.zip` | `memoria.exe` |
| `checksums.txt` | SHA-256 of each archive, in `sha256sum` format |

`<version>` has no leading `v`.

## Reproducible builds

The CLI is built with `CGO_ENABLED=0 -trimpath -buildvcs=false -ldflags "-s -w -buildid= -X ..."`. `scripts/release/assets.ts` writes the archives itself so they hold only the file name, mode, size, and the commit's timestamp. They carry no build-host user, timezone, or tool version. The same commit built with the same Go version gives the same bytes, and `cli/go.mod` pins the Go version the workflow uses.

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

It has no dependencies. `npm test` and `npm run typecheck` in `sidecar/` include it, which is how CI reaches it.
