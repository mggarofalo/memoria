# The mutation runner

Mutation testing never runs on a pull request, and hosted runners only run it when someone starts `mutation.yml` by hand. Commits on `main` are mutated by the mutation runner instead: a compose project in [`deploy/mutation-runner/`](../deploy/mutation-runner/) on a Linux box of our own. It posts the result as a commit status named `mutation`. No ruleset requires that status, and none should: a run takes minutes to hours and lands after the merge.

## What it does

Every `POLL_SECONDS`, 15 minutes by default, the runner:

1. **Fetches `main`** into a clone of its own, in its state volume. It never uses a shared checkout.
2. **Coalesces what arrived.** Commits that land between two polls, or while a run is going, become one run on the newest. That run covers every package any of them changed since the last mutated commit, so coalescing skips nothing.
   - A path selects its package: `src/`, `tests/` and `stryker/` select .NET; `sidecar/`, `worker/` and `cli/` select themselves; the root `package.json`, `package-lock.json` and `scripts/mutation-js.sh` select both Node packages.
   - Each `scripts/mutation-*.sh` selects the packages it mutates.
   - Markdown selects nothing. A commit that changes no package is marked mutated, with a status that says so.
3. **Mutates, in a fresh clone at that commit**, using that commit's own scripts and configs: `scripts/mutation-dotnet.sh`, `scripts/mutation-js.sh` and `scripts/mutation-go.sh`, with the Stryker and Gremlins configs.
   - With `MUTATION_SCOPE=changed`, the default, the scripts mutate only the files changed since the last mutated commit (`MUTATION_BASE`).
   - With `MUTATION_SCOPE=package`, they mutate every file of each selected package. That takes hours for the sidecar or .NET.
   - **.NET runs as the shards in `stryker/shards.json`**, one after another.
     - Core's redaction code (`src/Memoria.Core/Redaction/`) is not mutated: redaction is best-effort defence in depth, and the boundary is auth. A change there selects no shard, and the run gates nothing on it. Its unit tests still run in CI.
     - A changed-files run takes only the shards that hold a changed source file under `src/`, and each mutates just its changed files.
     - When no source file changed, as when only tests did, the plain `scripts/mutation-dotnet.sh` runs with `--since`.
     - A whole-package run takes every shard. Its score for each config counts all of that config's shards together.
     - `MUTATION_CONCURRENCY` sets Stryker.NET's test runners.
   - The very first run has no last mutated commit, so it mutates everything. Seed it with `--since` to avoid that (see "Run it once").
4. **Judges the run.** How depends on the scope.
   - **A whole-package run**, and the very first run, is judged as CI judges it: the thresholds are the configs', and a script that exits nonzero fails the run.
   - **A changed-files run is judged on the changed lines.** Each config's `break` was set for its whole package, and a changed file can sit below that bar before anyone touches it, so judging the whole file would fail a one-line edit. Instead the runner sets `MUTATION_NO_BREAK`, which turns the scripts' thresholds off, and then:
     - takes the lines `git diff -U0 <last mutated commit> HEAD` adds or changes. A pure deletion changes no line;
     - scores, for each config, only the mutants whose span overlaps one of those lines, counted as the whole score is: for Stryker, killed and timed out over those plus survived and uncovered, and for Gremlins its efficacy, killed over killed and lived. Gremlins gives a mutant one line, where it starts;
     - fails the run when a config's changed-line score is below that config's own `break` (for Gremlins, its `efficacy` threshold).
   - On a changed-files run, a config with no mutant on a changed line has no score, which is not a failure. A change to tests or comments alone usually has none.
   - A script that still exits nonzero failed for a reason other than a threshold, and that fails the run.
5. **Posts the status**: `pending` when the run starts, then `success` or `failure`, with each package's scores in the description. A changed-files run's description starts with `changed lines:`, as its scores are for those lines only.
   - **When the runner itself fails** mid-run, it posts `error`, and the commit stays queued for the next poll. Examples: a failed `npm ci`, a step the OOM killer stopped, or a missing tool.
   - **After three errors on one commit,** it records and posts a `failure` and moves on, so a fault that repeats cannot rerun forever.
   - **A failed fetch** comes before there is a commit to mark, so it is only logged, and the next poll tries again.
   - **A status GitHub refuses** is logged. The run's result is kept regardless.
6. **Keeps the reports** under `results/<commit>/` in the state volume: each package's reports, its log, and `summary.json`.

Only one run goes at a time. A lock in the state volume keeps the service and any `docker compose run` apart. Its holder touches it every minute, and a lock untouched for ten minutes counts as a dead run's.

The run happens inside the service's container, which is capped by `MUTATION_CPUS` and `MUTATION_MEMORY`. It starts no other containers and needs no Docker socket.

The runner holds the token, and runs as root. The scripts, npm's install scripts and the tests run as an unprivileged account, `mutation`, without the token in their environment. That account cannot read the runner's environment either, because the runner's process belongs to root.

## Set it up

You need Docker with the compose plugin on the box, and a copy of this repository's `deploy/mutation-runner/` directory. A checkout of the repository works; the runner never reads it after the build.

1. **Create a token.** Make a fine-grained personal access token with:
   - **Repository access:** only `mggarofalo/memoria`;
   - **Permissions:** "Commit statuses: read and write", and nothing else.

   The repository is public, so the clone needs no token and "Contents" can stay at no access. Give the token an expiry, and renew it before then: once it expires, every run ends in `error`.
2. **Configure it.** In `deploy/mutation-runner/`, copy `.env.example` to `.env`, and set `GITHUB_TOKEN`. Then set the caps to what the box can spare:
   - `MUTATION_CPUS=6` and `MUTATION_MEMORY=10g` suit a box shared with a GPU worker.
   - .NET and the sidecar each want about 4 CPUs and 4 GB at their busiest.

   Keep `.env` private: `chmod 600 .env`. The runner never logs the token, and never passes it to the scripts or the tests.
3. **Start it:**

   ```sh
   cd deploy/mutation-runner
   docker compose up -d --build
   ```

   The first poll clones the repository. With no state yet, it would then mutate everything. To start from the current head instead, run step 4 of "Run it once" first.

## Watch it

```sh
docker compose logs -f runner                              # what it is doing, and every status it posts
docker compose exec runner node /runner/runner.mts queue   # last mutated commit, any run in progress, what is waiting, recent results
```

Reports live in the state volume. `docker compose exec runner ls /state/results` lists them, and `docker compose cp runner:/state/results/<commit> .` copies one out. To keep them on the host instead, set `MUTATION_STATE` to a host path.

## Run it once

```sh
docker compose run --rm runner plan                         # what the next run would mutate, and why
docker compose run --rm runner once                         # one poll now; does nothing if a run holds the lock
docker compose run --rm runner once --since <commit>        # take <commit> as the last mutated one, e.g. to seed a first run
docker compose run --rm runner once --ref <branch>          # mutate a branch's head, from main's last mutated commit
docker compose run --rm runner once --packages worker,cli   # override the packages
```

`--rm` removes each one-off container when it ends. A branch run records its own last mutated commit, separate from `main`'s.

## Try it without a token

With `GITHUB_TOKEN` empty, the runner does everything except post. Each status it would have posted goes to the log as `status (not posted, no GITHUB_TOKEN): …`. Use this to try it on a workstation's Docker Desktop. Give it a project name of its own, so its volumes stay apart:

```sh
docker compose -p mutation-try run --rm runner once --since HEAD~1
docker compose -p mutation-try down -v
```

## Remove it

```sh
docker compose down          # stop the service; keep the state, the reports and the caches
docker compose down -v       # also delete those volumes
docker image rm memoria-mutation-runner:local
```

Then revoke the token on GitHub.
