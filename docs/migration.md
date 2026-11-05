# Moving from local QMD to Memoria

This is the order for moving a local QMD setup to a Memoria server: the desktop first, then the MacBook. Every step before `cutover` only reads your files. `cutover` is the first step that changes anything on your machine, and `rollback` undoes it. Nothing here touches local QMD or its index, so until you choose to retire it, it keeps working as a way back.

All the commands are in [cli.md](cli.md). You need a built `memoria`, a key with the `ingest` and `read` scopes, and each target collection opened and empty on the server. Use a checkout of this repository for `tools/RedactionCheck` and `skills/memoria/SKILL.md`.

## Before you start

- **A target must be empty when it is first opened.** The server checks this only the first time ingest uses a collection, so point `push` only at a target meant for this migration.
- **The rate limit sets the speed.** A key's default is 120 requests a minute, about 2 documents a second, so 5,000 documents take about 42 minutes. A re-run or a `verify` costs one request a document too. An operator can raise `RateLimit:PermitLimit` for the migration.
- **Nothing can be sent until you say so.** `push` without `--apply` and `cutover` without `--apply` only show what they would do.

## The desktop

### 1. See what there is

```sh
memoria migrate plan --qmd-config ~/.config/qmd/index.yml --codex-sessions ~/.codex/sessions
```

It exits 0 when everything selected can be stored, and 1 when something cannot. Its other exit codes are 2 for a mistake in how you ran it, such as a config with no collections.

An exit of 1 means one of these, and the counts say which:

- **A collection could not be counted.** Its row names the problem, such as a folder that cannot be opened or a pattern this refuses rather than guesses at.
- **A file or folder could not be read.** It is counted, and `--accept-skips` allows exit 0 for that alone.
- **A file can never be stored as it is,** by reason: a character or device name Windows forbids, an invisible character, a name not ending in `.md` in that case, a folder QMD excludes, names differing only in case, text that is not valid UTF-8, or a modification time over an hour ahead. Rename or fix these, or leave them out with an ignore rule. `--accept-skips` does not excuse them.

A blank file is left out and counted, and is not an error, because QMD does not index one.

### 2. Run the redaction report over the real corpus

Do this before any push. It reads your real files, and reports counts and rule names only, never a value, a path or a line.

```sh
dotnet build tools/RedactionCheck -c Release
memoria migrate report --qmd-config ~/.config/qmd/index.yml --tool tools/RedactionCheck/bin/Release/net10.0/RedactionCheck.dll
```

It exits 1 if a document was refused, gitleaks found something in the redacted text or could not read it, a file's name looks like a credential, or something could not be read. Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof. Look at this before going on. Text that survives redaction will be stored and searchable.

### 3. Push a small part first

Choose one small collection, or a copy of one, and send it:

```sh
memoria migrate push --qmd-config small.yml
memoria migrate push --qmd-config small.yml --apply
```

Stop it partway and run it again, to see it resume. Then check it with step 5.

### 4. Test one resumed session early

A session resumed after the migration replaces its migrated documents only when it shows it is the same one: it sends `projectDirectory`, and its transcript's first `cwd` matches. Otherwise it stores beside the export, or gets 409 `path_owned` under a name an export holds. So test this before pushing everything: migrate one real session, resume it, send it through the hook or by hand, and confirm one copy is left, not two. A session with fewer messages than its documents gets 409 `session_shrank`, and nothing is lost.

### 5. Push everything, then verify

```sh
memoria migrate push --qmd-config ~/.config/qmd/index.yml --apply
memoria migrate verify --qmd-config ~/.config/qmd/index.yml --queries queries.txt
```

`push` can be stopped at any time, and running it again continues. It exits 1 if anything was not stored, and a private report under the CLI's config folder names those files. The summary does not, as it may reach shared logs.

**The `--accept-*` flags.** These change only the exit code. Everything they cover stays counted and is not stored.

| Flag | What it says |
|---|---|
| `--accept-over-cap` | I know these documents are over the server's size limit |
| `--accept-skips` | I know these files or folders could not be read |

**The 4 over-cap Codex documents.** Four Codex exports are over the 2 MiB limit, so the server answers 413 and `push` reports them as over the cap. This is waiting on the owner's decision. The options are re-rendering them from their raw transcripts through the session route, which splits, or raising `MaxDocumentBytes` and `MEMORIA_QMD_MAX_DOCUMENT_BYTES` together for the migration. Until then, `verify` also exits 1 for them, as they are not held.

**What `verify` proves.** Every path's state and version on the server, and that a sample of documents matches the local text apart from redaction. It does not prove every document's text is right: a file gone from the server's disk shows only if it is in the sample, and taken-over documents are counted on their state alone.

**Overlap.** `verify` reports how many of local QMD's top 10 results the server also returned. It never fails on this. Local QMD ranks differently and may use another embedder, so 100% is not expected, and nobody has measured a threshold yet. Write down the number from the desktop's first run. If you want a threshold, that number is its evidence.

By default local results come from `qmd search`, which only reads and uses no model. That is keyword search against the server's hybrid search, so its overlap is lower. `--local-mode query` compares like with like, but it loads models and writes to the LLM cache in QMD's own index.

### 6. Cut over

```sh
memoria migrate cutover --skill skills/memoria/SKILL.md --hook-command "<the hook>"
```

Read what it shows. It backs up each file, swaps the `qmd` MCP entry for `memoria` in Claude Code and Codex, installs the skill, and adds the hook. When it looks right, run the same command with `--apply`, and confirm. Restart your clients.

Running it again is safe. It changes only what is left to change, and keeps the first backup. It changes user-level entries only: a `qmd` entry in one project's own config stays.

### 7. If you need to go back

```sh
memoria migrate rollback
memoria migrate rollback --apply
```

This restores every file from its backup, and removes the files the cutover created. If a file was edited since, it stops and says so, and `--overwrite-edits` goes ahead and loses that edit. Running it twice is safe. It changes nothing on the server, so documents stay there. Local QMD was never touched, so it works straight away.

## The MacBook

Do the same, with one report and one verify of its own, as its files are not the desktop's.

- **Paths.** The commands are the same. The CLI finds the client files from your home folder, and honours `CLAUDE_CONFIG_DIR` and `CODEX_HOME` on either machine.
- **Targets.** A collection on the Mac needs its own empty target, or it mixes with the desktop's. `--target local=name` gives it one.
- **Decomposed names.** A Mac can hold names in decomposed form, which the server refuses as they stand. `push` sends them in composed form, the same every time, so a re-send is the same document. The stored spelling differs from the local one, and `plan` counts how many. Two local files with one composed spelling are a conflict: the first is sent, and the second is refused as `second_spelling` until you rename one.

## Retiring local QMD

This is a separate step, and no command does it. Leave local QMD and its index alone until you have used the server for long enough to trust it.

## What the hook has to do

The hook that pushes sessions is not in this repository, and `cutover` only adds the command you give it to run when a Claude Code session ends. For a migrated session to be replaced rather than doubled, the hook has to:

- **Send `projectDirectory`** to `POST /v1/ingest/session`, the session's folder name under Claude Code's projects folder. Without it, a session cannot show it is the original, so it never takes over its migrated documents.
- **Surface `session_shrank`.** A 409 with this code means the upload holds fewer messages than the documents reach. Nothing was changed, and someone has to decide, so it must not be retried quietly or dropped.
- **Report a transcript with no `cwd`.** With none, the server cannot match the folder, so the session stores beside its export. The hook should say so rather than look like a success.
