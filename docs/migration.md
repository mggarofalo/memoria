# Moving from local QMD to Memoria

This is the order for moving a local QMD setup to a Memoria server: the desktop first, then the MacBook. Every step before `cutover` only reads your files. `cutover` is the first step that changes anything on your machine, and `rollback` undoes it. Nothing here touches local QMD or its index, so until you choose to retire it, it keeps working as a way back.

All the commands are in [cli.md](cli.md).

## What you need first

- **`memoria` on your PATH,** as your clients will start it by that name for `memoria mcp`. A copy only in a build folder will not do.
- **A login.** Run `memoria auth login --api-url <server>` with a key holding the `ingest` and `read` scopes. Do this before `cutover` too: `memoria mcp` uses the stored key, so a client started without one gets no search. On a machine with no keyring, such as a headless box, it needs `--backend file`, which stores the key unencrypted.
- **The .NET SDK and gitleaks on your PATH** for the report in step 2. `dotnet --version` should show 10, and `gitleaks version` should run.
- **A checkout of this repository,** for `tools/RedactionCheck` and `skills/memoria/SKILL.md`.
- **Each target collection opened and empty** on the server.

## Before you start

- **A target must be empty when it is first opened.** The server checks this only the first time ingest uses a collection, so point `push` only at a target meant for this migration.
- **The rate limit sets the speed.** A key's default is 120 requests a minute, about 2 documents a second, so 5,000 documents take about 42 minutes. A re-run or a `verify` costs one request a document too. An operator can raise `Memoria:Auth:RateLimit:PermitLimit` for the migration.
- **Nothing can be sent until you say so.** `push` without `--apply` and `cutover` without `--apply` only show what they would do.

## The desktop

### 1. See what there is

```sh
memoria migrate plan --qmd-config ~/.config/qmd/index.yml --codex-sessions ~/.codex/sessions
```

If `~/.codex/sessions` does not exist, drop that flag, or the command exits 1. The raw sessions are counted under `--codex-target`, `codex-sessions` by default. If a QMD collection would go to that name too, such as one holding your Codex exports, the command exits 2: pass `--codex-target` another name. It exits 0 when everything selected can be stored, and 1 when something cannot. Its other exit codes are 2 for a mistake in how you ran it, such as a config with no collections.

An exit of 1 means one of these, and the counts say which:

- **A collection could not be counted.** Its row names the problem, such as a folder that cannot be opened or a pattern this refuses rather than guesses at.
- **A file or folder could not be read.** It is counted, and `--accept-skips` allows exit 0 for that alone.
- **A file can never be stored as it is,** by reason: a character or device name Windows forbids, an invisible character, a name not ending in `.md` in that case, a folder QMD excludes, names differing only in case, text that is not valid UTF-8, or a modification time over an hour ahead. Rename or fix these, or leave them out with an ignore rule. `--accept-skips` does not excuse them.

A blank file is left out and counted, and is not an error, because QMD does not index one.

**Stop or go.** Go on at exit 0. At exit 1, stop and clear each count, or use `--accept-skips` only where you are content to leave those files unsent. At exit 2, fix how you ran it.

### 2. Run the redaction report over the real corpus

Do this before any push. It reads your real files, and reports counts and rule names only, never a value, a path or a line.

```sh
dotnet build tools/RedactionCheck -c Release
memoria migrate report --qmd-config ~/.config/qmd/index.yml --tool tools/RedactionCheck/bin/Release/net10.0/RedactionCheck.dll
```

It exits 1 if a document was refused, gitleaks found something in the redacted text or could not read it, a file's name looks like a credential, or something could not be read. Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof. Text that survives redaction will be stored and searchable.

**Stop or go.** Go on only at exit 0. Exit 1 is a stop: do not push until you have dealt with what it counted, by removing the secret from the file, or by leaving that file out with an ignore rule, and the report comes back clean. If a rule's count looks wrong for your notes, stop and look at the rule, as the report will not show you the text.

### 3. Push a small part first

Choose one small collection, or a copy of one. `small.yml` is a QMD config with only that collection in it, in the same form as `index.yml`, and its target must be a throwaway:

```yaml
collections:
  small:
    path: /path/to/a/small/folder
    pattern: "**/*.md"
```

```sh
memoria migrate push --qmd-config small.yml
memoria migrate push --qmd-config small.yml --apply
```

Stop it partway and run it again, to see it resume. Wait for the index first. A pushed document is not counted or fetched until it is indexed, so `verify` straight after `push` exits 1 with 0 documents on the server, and is clean a short time later. It says so when it may be this. Run `memoria status` until the document count stops rising, and then run `verify`.

```sh
memoria migrate verify --qmd-config small.yml
```

**Stop or go.** Go on when `push` and `verify` both exit 0. Otherwise stop: a problem at this size will be the same problem at full size.

### 4. Test one resumed session early

A session resumed after the migration replaces its migrated documents only when it shows it is the same one: it sends `projectDirectory`, and its transcript's first `cwd` matches. Otherwise it stores beside the export, or gets 409 `path_owned` under a name an export holds. So test this before pushing everything, on the small collection from step 3. It has to hold the export of one real Claude Code session, so copy that session's markdown into its folder and push it again if it does not.

Resume that session and say something in it with a word you can search for. Then send its transcript, with this collection as the target. This is what the hook will do, by hand. `<folder>` is the name of the session's folder under `~/.claude/projects`, and `<session-id>` is the transcript's file name without `.jsonl`. In PowerShell, write `curl.exe`, and use `(Get-Date).ToUniversalTime().ToString("o")` for the time.

```sh
curl -sS -i -X POST "<server>/v1/ingest/session?collection=small&format=claude&sessionId=<session-id>&projectDirectory=<folder>&updated=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  -H "Authorization: Bearer <key>" -H "Content-Type: application/x-ndjson" \
  --data-binary @"$HOME/.claude/projects/<folder>/<session-id>.jsonl"
```

It should answer 202. Wait for the index, then look:

```sh
memoria migrate verify --qmd-config small.yml
memoria query "<your word>" --collection small
```

**Stop or go.** Go on only if there is one copy of the session, `verify` counts its documents as taken over, and the text you added in the resumed session can be found by search. Two copies, or a 409 `path_owned`, means the hook is not sending what it needs: see [What the hook has to do](#what-the-hook-has-to-do), and fix that first, because every session you resume after the push would do the same. A session with fewer messages than its documents gets 409 `session_shrank`, and nothing is lost.

### 5. Push everything, then verify

```sh
memoria migrate push --qmd-config ~/.config/qmd/index.yml --apply
```

Wait for the index first. A pushed document is not counted or fetched until it is indexed, so `verify` straight after `push` exits 1 with 0 documents on the server, and is clean a short time later. It says so when it may be this. Run `memoria status` until the document count stops rising, and then run `verify`.

```sh
memoria migrate verify --qmd-config ~/.config/qmd/index.yml --queries queries.txt
```

`queries.txt` is yours to write: one query a line, of the kind you really search for, about 20. It stays on your machine, and the output says how many results overlapped, never the query.

`push` can be stopped at any time, and running it again continues. It exits 1 if anything was not stored, and a private report under the CLI's config folder names those files. The summary does not, as it may reach shared logs.

**The `--accept-*` flags.** These change only the exit code. Everything they cover stays counted and is not stored.

| Flag | What it says |
|---|---|
| `--accept-over-cap` | I know these documents are over the server's size limit |
| `--accept-skips` | I know these files or folders could not be read |

**Codex exports over the cap.** An export over the 2 MiB limit gets 413, and `push` reports it as over the cap. It goes again from their raw transcripts, through the session route, which splits a session into parts the cap allows:

```sh
memoria migrate rerender --qmd-config ~/.config/qmd/index.yml --codex-sessions ~/.codex/sessions
memoria migrate rerender --qmd-config ~/.config/qmd/index.yml --codex-sessions ~/.codex/sessions --apply --timeout 10m
```

Add `--codex-sessions ~/.codex/archived_sessions` as well if Codex archived any of those sessions. A large transcript takes a while to send and render, which is what `--timeout` is for.

- **Which documents.** Each selected file larger than the default 2 MiB cap, and each the last `push` to this server named over the cap.
- **Which transcript.** The one whose file name holds the export's frontmatter `session_id`, and whose `session_meta` names that same session, as the server checks. It is sent with `format=codex`, that `session_id`, and the transcript's file time as `updated`.
- **Stop or go.** It exits 0 only when every one is held through its session. The dry run, without `--apply`, already says what it found, and its private report names each file it could not match.
- **A missing transcript.** It says so, names the file in the report, and exits 1, with this line in its output: "An over-cap document with no raw transcript here is held only by its export, which the server cannot store. Find its transcript, or keep the export." Do not retire local QMD while that is so: the export is the only copy.
- **More than one transcript naming a session.** It sends none of them and says so. Move the one you do not want out of the folders given.
- **What a Codex session takes over.** Unlike Claude Code, a Codex session needs no proof of which copy it is. It replaces documents in the collection root whose frontmatter names it, and leaves copies in subfolders alone. One that has fewer messages than those documents reach is refused with 409 `session_shrank`, and nothing changes. `rerender` never sends `allowShrink`.
- **Running it again** asks the server first, and sends nothing it holds complete at this version or later.

Afterwards `push` still reports them over the cap, as they are, so pass it `--accept-over-cap`. `verify` counts each as held through its session, does not sample it, and turns the count check off for that collection, as a session writes more documents than one export.

**What `verify` proves.** Every path's state and version on the server, and that a sample of documents matches the local text apart from redaction. It does not prove every document's text is right:

- A file gone from the server's disk reads clean until the index updates, and after that shows only if it is in the sample.
- Taken-over documents are counted on their state alone, and one takeover turns the count check off for its whole collection. The output says so.
- A change inside a mark cannot be seen. A mark can also stand for lines with no spaces below it, up to about 255, so lines of that shape lost under a mark cannot be seen either.

**Could not compare.** Redaction can leave a document that cannot be lined up with its local copy: a key block with no end, or one followed by a second `BEGIN`. A copy stored by an older server can also have a token joined to the words after it, or a password to the `Authorization` line below it, with those words lost. If every line away from a mark still matches, in order, `verify` counts the document as could not compare, names it in the private report, and does not fail for it.

Check each one by hand: the local file against `memoria get`. It may be missing text beside a mark, and a real lost line right next to a mark reads this way too. Each mark can hide one line with spaces, and any lines without, so a document with many marks has no limit on what it can lose. If text is missing, send the file again. A changed or lost line away from any mark is still a difference, and fails.

**Overlap.** `verify` reports how many of local QMD's top 10 results the server also returned. Low overlap never fails it, but a query that cannot run does. Local QMD ranks differently and may use another embedder, so 100% is not expected, and no threshold has been measured. Write down the number from the desktop's first run.

**Stop or go.** Run `verify` until these hold, then cut over:

- `verify` exits 0, with any over-cap documents counted as held by their session. If `rerender` could not send one, the count of files not held is that one, and it is a stop until its transcript is found or you accept that it stays unsearchable, with its export as its only copy.
- You have looked by hand at each document it could not compare.
- You have looked at the overlap and it does not surprise you. Results that share almost nothing with local QMD are worth a look, even though they do not fail `verify`.

By default local results come from `qmd search`, which only reads and uses no model. That is keyword search against the server's hybrid search, so its overlap is lower. `--local-mode query` compares like with like, but it loads models and writes to the LLM cache in QMD's own index.

### 6. Cut over

```sh
memoria migrate cutover --skill skills/memoria/SKILL.md
```

Read what it shows. It looks for `qmd` in `~/.mcp.json`, `~/.claude.json` and Codex's `config.toml`, swaps it for `memoria` in each that defines it, and backs up each file first. A file that does not define it stays as it is, and it says so. If `qmd` is nowhere, it says where it looked and exits 1. Nothing was changed: find where your entry is before going on.

Where `settings.json` or `settings.local.json` allows qmd's tools, it adds `mcp__memoria__*` rules the same way, and `memoria` in `enabledMcpjsonServers` if qmd is there. Without those, your clients would ask about each memoria tool or not load it at all. The old rules stay, and it says how many: they do nothing once qmd is gone, and you can remove them later. It installs the skill in each skills folder it finds. A folder that is a link is skipped and named, as writing there would change the repository it points at: add the skill there yourself if you want it.

It adds no hook. It shows the `SessionEnd` hook you already have, and whether one is there at all. The hook is yours, and has to do what [What the hook has to do](#what-the-hook-has-to-do) says.

It refuses a config it cannot read as a whole, a form of TOML it cannot edit safely, and a config that is a link or read-only, and changes nothing. If so, fix that file by hand and run it again.

**Stop or go.** Go on when every change it shows is one you expected. Then run the same command with `--apply`, and confirm. If a client wrote to a file since it was shown, it stops: run it again and read it again.

Restart your clients, and check by running a search from one. **If the search works, you are done here. If not, roll back,** and look at `memoria auth status`.

Running it again is safe. It changes only what is left to change, and keeps the first backup, including after a run that failed or was killed. It changes user-level entries only: a `qmd` entry in one project's own config stays, and it says how many projects have one.

Two things it leaves you with. A JSON file it changes comes back re-indented, with its keys and values kept. And `kill -9`, or a power cut, in the instant before a file is replaced can leave a `.memoria-cutover-*` file beside it, holding that config with the same private permissions. `rollback` does not remove it, so delete it yourself if you find one.

### 7. If you need to go back

```sh
memoria migrate rollback
memoria migrate rollback --apply
```

This shows what it would do, then does it. A file no one has edited since comes back whole from its backup. A file edited since, as a client will do to `~/.claude.json`, keeps those edits: it reverses only the cutover's own change, puts `qmd` back as it was, and says so. Files and folders the cutover created are removed by their exact recorded paths, and a folder only if nothing else is in it. If an edit cannot be kept, such as a skill you changed or a config no longer readable, it stops and says so, and `--overwrite-edits` goes ahead and loses that edit. Running it twice is safe. It changes nothing on the server, so documents stay there. Local QMD was never touched, so it works straight away.

## The MacBook

Do the same, with one report and one verify of its own, as its files are not the desktop's.

- **Paths.** The commands are the same. The CLI finds the client files from your home folder, and honours `CLAUDE_CONFIG_DIR` and `CODEX_HOME` on either machine.
- **Targets.** A collection on the Mac needs its own empty target, or it mixes with the desktop's. `--target local=name` gives it one.
- **Decomposed names.** A Mac can hold names in decomposed form, which the server refuses as they stand. `push` sends them in composed form, the same every time, so a re-send is the same document. The stored spelling differs from the local one, and `plan` counts how many. Two local files with one composed spelling are a conflict: the first is sent, and the second is refused as `second_spelling` until you rename one.

## Retiring local QMD

This is a separate step, and no command does it. Leave local QMD and its index alone until you have used the server for long enough to trust it.

## What the hook has to do

The hook that pushes sessions is not in this repository, and `cutover` does not add or change one. It reports what is set for `SessionEnd`. For a migrated session to be replaced rather than doubled, the hook has to:

- **Send `projectDirectory`** to `POST /v1/ingest/session`, the session's folder name under Claude Code's projects folder. Without it, a session cannot show it is the original, so it never takes over its migrated documents.
- **Surface `session_shrank`.** A 409 with this code means the upload holds fewer messages than the documents reach. Nothing was changed, and someone has to decide, so it must not be retried quietly or dropped.
- **Report a transcript with no `cwd`.** With none, the server cannot match the folder, so the session stores beside its export. The hook should say so rather than look like a success.
