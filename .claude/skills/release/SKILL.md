---
name: release
description: >
  Cut a Memoria release. Confirms a clean main that matches origin and has green
  CI, computes the next version from Conventional Commits, asks the human to
  confirm, pushes the annotated tag, watches the workflow, and verifies the
  release, assets, checksums, and image. Use when asked to release, cut a
  release, tag a version, or publish Memoria.
allowed-tools: Bash, Read, Glob, Grep
user_invocable: true
argument: "none: the version is computed, not chosen"
---

# release

**Purpose:** Turn the current head of `main` into a published release. The tag is the only version in this repository, and `docs/releasing.md` describes the scheme. Pushing the tag does the rest, because `.github/workflows/release.yml` and `.github/workflows/docker.yml` both trigger on it.

## YOU MUST EXECUTE ALL STEPS BELOW

This is a checklist to execute in order. Stop at the first step that fails and tell the human what failed. A pushed tag is public and permanent, so never push one without the confirmation in step 4.

## Never

- Never move, delete, or re-push a tag that has been pushed, and never delete a published release, without the human's explicit confirmation of that exact action in this conversation. A release someone may already have downloaded stays. The normal fix is a new patch release.
- Never choose a version by hand. The workflow refuses any tag other than the computed one, and `v1.0.0` is the single exception (see below).
- Never rewrite old commits to change what the notes say.
- Never put tracker URLs or work-item IDs in a tag, release notes, or anything else in this repository. It is public.

## Step 1: Confirm a clean main that matches origin

```bash
git fetch origin main --tags
git status --porcelain --branch
git rev-parse HEAD
git rev-parse origin/main
```

All of these must hold:

- The branch is `main`.
- The working tree is clean.
- `HEAD` and `origin/main` are the same commit.

If not, stop and report the difference. Do not stash, reset, or pull on the human's behalf without asking.

## Step 2: Confirm the commit's CI is green

Read the checks `main` requires, then the runs on this commit:

```bash
gh api repos/mggarofalo/memoria/rules/branches/main --jq ".[] | select(.type == \"required_status_checks\") | .parameters.required_status_checks[].context"
gh api repos/mggarofalo/memoria/commits/<sha>/check-runs --jq ".check_runs[] | {name, status, conclusion}"
```

Every required check must show `conclusion: success`. If one is still running, wait for it. If one failed, stop: release a commit that passes. The workflow makes this same check and refuses otherwise.

## Step 3: Compute the version

```bash
node scripts/release/release.ts next
```

This prints the last tag, the next version, and each commit with the bump it asked for.

- **`next: nothing to release`:** there are no `feat`, `fix`, `perf`, or breaking commits since the last tag. Report that and stop. Do not make a commit just to have something to release.
- **Output looks wrong** (say, a breaking change was missed because a commit message was malformed): stop and report it. The history decides the version, so the fix is a correctly described follow-up commit on `main`, not a hand-picked tag.

## Step 4: Ask the human to confirm

Show the computed tag and the commit list, then ask for a plain yes. Say what the yes does: it publishes a GitHub release and an image, and cannot be cleanly undone.

Skip nothing here, even when the request said "release it". The human has not seen the computed version until now.

**Leaving 0.x.** While the version is 0.x, breaking changes bump the minor version, so nothing reaches 1.0.0 by accident. If the human says this release is 1.0.0, use `v1.0.0` as the tag in step 5. The workflow accepts it only when the previous tag is 0.x.

## Step 5: Create and push the annotated tag

Tag the commit from step 1, by its full SHA, then push only that tag:

```bash
git tag -a <tag> <sha> -m "<tag>"
git push origin refs/tags/<tag>
```

Do not use `git push --tags`, which would push every local tag. If the push is rejected, report it, and do not retry under another name.

## Step 6: Watch the workflows

```bash
gh run list --commit <sha> --limit 10 --json databaseId,name,status,conclusion,event
gh run watch <release-run-id> --exit-status
gh run watch <docker-run-id> --exit-status
```

Two runs start from the tag: `Release` and `Docker`. Wait for both.

### If a workflow fails

Read the log first: `gh run view <run-id> --log-failed`.

- **It failed before publishing, for a transient reason** (network, runner): re-run it with `gh run rerun <run-id> --failed`. The tag and the commit are unchanged, so validation gives the same answer.
- **`validate` refused the tag** (not on main, not the computed version, checks not green): nothing was published by that workflow. Tell the human what it said. If CI was simply unfinished, re-run once it is green. Otherwise the tag is wrong and removing it needs the confirmation below.
- **The release notes guard refused:** a tracker reference is in a generated note, which means a pull request title holds one. Ask the human to fix that title, then re-run. Do not edit the guard.
- **A real defect in the commit:** fix it on `main` through a pull request and release again from this skill, which will compute the next version. Leave the failed tag unless the human decides otherwise.

To remove a tag, first check that no release used it (`gh release view <tag>` fails) and that the image was not published (see step 7). Tell the human exactly what will be deleted, wait for an explicit yes to that deletion, then:

```bash
git push origin --delete refs/tags/<tag>
git tag -d <tag>
```

If a release or an image already exists for that tag, do not remove it. Release the fix as a new version.

## Step 7: Verify what was published

Check each of these, and report each result:

```bash
gh release view <tag> --json tagName,name,isDraft,isPrerelease,assets,body
```

- The release exists and is not a draft.
- It has exactly seven assets, where `<version>` is the tag without its `v`:
  - `memoria_<version>_{linux,darwin}_{amd64,arm64}.tar.gz`
  - `memoria_<version>_windows_{amd64,arm64}.zip`
  - `checksums.txt`
- The notes contain no tracker references.

Download into a new empty temporary directory and verify the bytes:

```bash
gh release download <tag> --dir <temp-dir>
```

From inside that directory, run `sha256sum -c checksums.txt`. Then, for the archive matching this machine, extract it and run the binary with `--version`. It must print `memoria version <version>`.

Verify provenance on one asset:

```bash
gh attestation verify <temp-dir>/<asset> --repo mggarofalo/memoria
```

Verify the image reports the same version:

```bash
docker run -d --name memoria-release-check -p 127.0.0.1:18080:8080 -e Memoria__Sidecar__BaseUrl=http://127.0.0.1:8282 ghcr.io/mggarofalo/memoria:<version>
curl -fsS http://127.0.0.1:18080/health
docker rm -f memoria-release-check
```

`/health` must report `"version":"<version>"`. If Docker is not available here, say so instead of claiming the image was checked, and check the tag's existence with `docker manifest inspect ghcr.io/mggarofalo/memoria:<version>` or the Docker workflow's own "Verify the image's reported version" step.

## Step 8: Report

Give the human the tag, the release URL, the image reference, and what you verified. List anything you could not check.
