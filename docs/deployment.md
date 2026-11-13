# Deploying Memoria

One host runs the API and the sidecar, and workers on other machines run the models.

```
laptop / desktop / phone / cloud agents
        │  HTTPS through the reverse proxy, on its own network
        ▼
  Memoria.Api         container, ghcr.io/mggarofalo/memoria, port 8080      ┐
        │  one bridge network, by service name, nothing between          │ the Linux
        ▼                                                                 │ API host
  memoria-qmd         container, ghcr.io/mggarofalo/memoria-qmd, no port  │
        │                                                                 │
        ├── QMD index, collection roots          volumes                  ┘
        │
        │  WireGuard
        ▼
  memoria-worker      the GPU PC and the laptop: embedding, reranking, expansion
```

## Decision: two images, one host

The API and the sidecar are two images and two compose services on the Linux API host:

- **Nothing may sit between them,** and on one bridge network nothing does. The sidecar publishes no port, so only the API can reach it. See [below](#nothing-may-sit-between-the-api-and-the-sidecar).
- **The host runs no model.** Model work goes to workers, so the sidecar needs no GPU, and its image holds no llama.cpp binaries. It cannot run a model even if configured to.
- **The two still move independently.** Each image has its own version tag from the same release.

## Running as a service

This section is about the workers. The sidecar runs from its image, under compose.

`worker/scripts/install-service.ps1` installs a worker as a Windows service, using
`deploy/windows/MemoriaService.psm1`. **It has not been run on a real install.**
It accepts `-WhatIf`, which prints every step, needs no elevation and changes
nothing.

| What | Where | Who can use it |
|---|---|---|
| Code, `node.exe`, `nssm.exe` | `%ProgramFiles%\Memoria\<service>` | Only administrators can change it. The script checks this and stops otherwise. |
| Secret | `%ProgramData%\<service>\secret\` | Administrators and the service account only, with inheritance off. The script checks this before starting. |
| Logs | `%ProgramData%\<service>\logs` | Administrators and the service account only. |
| Service identity | `NT SERVICE\<service>` | A virtual account, with no password to manage. Not LocalSystem. |

**Why this shape.** A service running as LocalSystem from a tree a user can write
to hands SYSTEM to any process running as that user at the next restart. A secret
passed through `nssm set AppEnvironmentExtra` appears on a command line and stays
in the registry under the service's key, which local users can generally read.

**What the account can reach.** Grants are added to the paths you name and
nothing else:

| Service | Path | Access | Why |
|---|---|---|---|
| worker | `-ModelDir`, by default `%USERPROFILE%\.cache\qmd\models` | Read | The models. |
| worker | the directory holding `llama-server` | Read | The binary and its DLLs. Refused if a non-administrator can change it. |

The default leaves the models in your own profile, so a process running as you can
still change them. That cannot reach SYSTEM, but a model file is parsed by
`llama-server` under the service account, so moving the models under
`%ProgramData%` is the stronger setup. Pass `-ModelDir` to do that after copying
them.

**Log rotation.** `AppRotateFiles` and `AppRotateOnline` are on with
`AppRotateBytes` at 10 MiB, so a log rotates at that size while the service runs,
as well as at start. Per NSSM's documentation, rotated files are renamed with a
timestamp, online rotation ignores any age limit, and a rename happens only after
the application's next line of output. The documentation describes no deletion of
old files, so plan on pruning them yourself.

**Upgrading an earlier install.** The script replaces the registry environment,
which removes a token stored there, and moves the service off LocalSystem. Treat
that token as exposed and rotate it: choose a new one and update the sidecar's
worker list on the API host. The old install directory is not touched. Its log and
data locations are the ones above unless you passed others.

**NSSM version.** 2.24, the last stable release, fails to start services on
Windows 10 Creators Update or newer, according to nssm.cc, which recommends
2.24-101 or later. The script warns about 2.24. `winget`'s version was not checked.

**macOS.** `worker/scripts/install-launchd.sh` writes the token to a mode 600 file
in a mode 700 directory, and the plist (also mode 600) holds the path. The agent
runs as you, so there is no privilege boundary to design around. launchd does not
rotate logs.

### Not confirmed

These need a real install, and none has been done:

- `sc.exe config <service> obj= "NT SERVICE\<service>"` on an NSSM-registered
  service, and NSSM starting its child and writing and rotating logs under that
  account. NSSM's documentation describes `ObjectName` as needing a password and
  does not mention virtual accounts, though a commit in its repository appears to
  add them, which is why the script uses `sc.exe`.
- **GPU access from the virtual account.** CUDA for `llama-server` has only ever
  been used in an interactive session. `nvidia-smi` under the account is also unproven. If it
  fails, this is the one thing that may force a different account.
- Reading the models through a user profile. The account has no rights on
  `C:\Users\<you>` itself and relies on the default "bypass traverse checking"
  right. Anything that walks the parent directories could fail, which `-ModelDir`
  under `%ProgramData%` would avoid.
- That the account's own profile, `C:\Windows\ServiceProfiles\<service>`, is created
  and writable, since temp files may use it.
- The `Log on as a service` right, which virtual accounts normally hold through
  `NT SERVICE\ALL SERVICES`. A group policy can remove it.
- The launchd script on a Mac: `launchctl bootstrap` with the mode 600 plist, the
  `Umask` key's effect on the log files, and the worker reading the token file.

## Collection root permissions

**A local user with write access to a collection root is outside the threat these
checks defend against.** Restrict each root so only the sidecar's account and the
people who administer it can write there, and do not point a collection at a
directory other accounts share.

The sidecar's path checks stop a remote caller from leaving the root, and they
narrow what a local user can do, but they cannot finish the job. Node has no
`openat` or `renameat`, so every filesystem call resolves its path again, and
someone who can swap a directory for a junction can still race those calls. Such a
user can:

- win the gap inside a rename and overwrite a file outside the root, if the
  sidecar's account can write to it;
- leave an empty file outside the root;
- make a delete rename an outside file to a `.memoria-*.tmp` name beside its old
  one, and a later write in that directory may then remove it if it is over an hour
  old and another swap lands at the right moment.

The sidecar's container runs as a user, not root, which limits the damage. `sidecar/README.md` lists the rules a document path must meet.

## The images

`.github/workflows/docker.yml` publishes both. Every pull request builds them and publishes nothing. Each reports the version it was stamped with on `/health`, the API only to a caller with a key; see [releasing.md](releasing.md).

| Image | Holds |
|---|---|
| `ghcr.io/mggarofalo/memoria` | `Memoria.Api` |
| `ghcr.io/mggarofalo/memoria-qmd` | the sidecar, with the QMD SDK patched at install and no llama.cpp binaries |

Both carry the same tags:

| Tag | Published by | Moves? |
|---|---|---|
| `<major>.<minor>.<patch>` | a release tag | no |
| `<major>.<minor>` | a release tag | to the newest patch of that minor |
| `latest` | a release tag | to the newest release |
| `edge` | every push to `main` | to the head of `main`, unreleased |
| `sha-<short>` | every push to `main` | no |

Deploy a specific release. `latest` is there for looking around, and `edge` is for trying unreleased work; neither says what is running. A failed sidecar image stops the API image publishing too, so a release always has both.

| Property | API | Sidecar |
|---|---|---|
| Base | `mcr.microsoft.com/dotnet/aspnet:10.0` | `node:26-bookworm-slim` |
| User | non-root (`$APP_UID`) | non-root (`node`, uid 1000) |
| Port | 8080 | 8282, published by nothing |
| Health check | `dotnet Memoria.Api.dll --health-check` | `/health` from inside the container |
| Logs | JSON lines on stdout | JSON lines on stdout |

Build them locally:

```bash
docker build -t memoria-api:local .
docker build -t memoria-qmd:local sidecar
```

## Compose on the API host

[`deploy/compose.yaml`](../deploy/compose.yaml) is the production shape. Put the values below in `deploy/.env`, mode 600, beside it.

### First run

The sidecar never creates an index, so make one first. For each name the index does not have, this records a collection rooted at `/corpus/<name>`, making the folder if it is missing, and indexes what is there. Running it again, with any names in any order, adds only the collections the index does not have. A collection it already has keeps its root, rules, contexts and documents, and is not scanned, and a collection left off the command is not touched. It warns if a recorded root does not exist, which usually means the corpus volume is not mounted. It reads and writes no config file:

```bash
docker compose -f deploy/compose.yaml run --rm --no-deps memoria-qmd \
  scripts/init-index.mjs claude-sessions codex-sessions
docker compose -f deploy/compose.yaml run --rm --no-deps memoria-api \
  --bootstrap-admin-key homelab-admin
docker compose -f deploy/compose.yaml up -d
```

These two collections are the ones the compose file opens to ingest. Each opens only while its root is empty, so move an old corpus in through the API (`memoria migrate push`), not by copying files. An index from another host does not carry over, as it records that host's paths.

### Required environment

| Variable | Purpose |
|---|---|
| `MEMORIA_VERSION` | The release to run, for both images, e.g. `0.1.0`. No default, so an upgrade happens only when you change it. |
| `MEMORIA_SIDECAR_SECRET` | The secret the API presents to the sidecar. 16 or more random characters. |
| `MEMORIA_INGEST_DIGEST_KEY` | 32 or more random characters, kept outside the database. See [configuration](configuration.md#digests-in-the-ledger) for how to change it. |
| `MEMORIA_QMD_WORKERS` | The workers, as a JSON list. See [the workers](#the-workers). |

### Optional environment

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_WORKER_TOKEN_GPU` | empty | The token of the worker whose `tokenEnv` names it. |
| `MEMORIA_WORKER_TOKEN_LAPTOP` | empty | The same, for a second worker. |
| `MEMORIA_CORPUS` | the `memoria-corpus` volume | A host directory for the collection roots instead, owned by uid 1000. |
| `MEMORIA_BIND_ADDRESS` | `127.0.0.1` | Where the API's port is published. The host's LAN address lets LAN clients connect directly. `ufw` does not filter a published port: see [published ports and the host firewall](#published-ports-and-the-host-firewall). |
| `MEMORIA_PROXY_NETWORK` | `memoria-proxy` | The name of the network the reverse proxy reaches the API on. Change the proxy's external network name with it. See [the proxy's network](#the-proxys-network). |
| `MEMORIA_TRUSTED_PROXY` | empty | The reverse proxy's address as the API container sees it. Without it, all clients share one failed-authentication budget. |

Both containers run as a user, not root, with a read-only root filesystem, a `tmpfs` at `/tmp`, every Linux capability dropped and `no-new-privileges`. Everything they write is on the volumes. `scripts/e2e/compose.ts` checks each of these, for both.

The API runs with a 1 GiB heap (`DOTNET_GCHeapHardLimit`), the least ingest is sized for. Pass secrets through `deploy/.env` or a Docker secret; never bake them into an image or commit them.

### Volumes

| Volume | Holds | If it is lost |
|---|---|---|
| `memoria-data` | API keys and the audit log, in `control.db` | **Not rebuildable.** Every key is revoked and the audit trail is gone. Back it up. |
| `memoria-corpus` | the collection roots: every document | **Not rebuildable** unless every source still exists. Back it up. |
| `memoria-qmd-index` | the QMD index, and the job queue's file, which also holds the documents set aside from embedding | Rebuildable, by running the first-run command and then `POST /reindex` with `embed: true` on the sidecar, which re-embeds everything on the workers. Backed up anyway, as restoring it is much cheaper: see [Backups](#backups). Deletes are kept in `control.db`, not here. |
| `memoria-qmd-config` | QMD's own config, used only by the `qmd` CLI. Memoria writes no collection there, so unless you add one it names none, and the CLI writes only its model names | Rebuildable. The sidecar reads its collections from the index, and a missing or empty config removes none of them. See [lost collection records](#lost-collection-records). |

Keep them on a local disk, as SQLite's WAL mode does not work over a network share. A volume's name carries the compose project's, `memoria` here; `docker volume ls` shows it.

### What a delete removes, and when

A delete (`DELETE /v1/documents`, or a session that replaces its own parts) takes the file out of the corpus at once, and every read route stops returning it at once. The index follows at the next scan, which the delete queues. The scan runs once the debounce window ends, or at the next start if shutdown comes first. That scan marks the document's row inactive, which takes it out of the full-text index's own copy of the text. At its end, one transaction removes:

- the document's row;
- its text, in `content`;
- its chunks' vectors, in `content_vectors` and `vectors_vec`.

Then any record that it was set aside from embedding goes from the job queue's file, which holds those beside the jobs.

Text and vectors are kept by content, so when another document holds the same text word for word, that text and its vectors stay, as they are that document's too.

A collection the scan found with no document at all needs one more check, as that is also what a mount that went away looks like. Removing everything then would cost every vector of the collection a re-embed when it comes back. So the sidecar keeps a marker file, `.memoria-root`, in each collection root: `init-index` writes it into each root folder it creates, never into one that was already there, and every scan writes it into each root that holds a document. QMD indexes only `.md` files, so it is never a document, and a root holding only the marker still counts as empty for a first ingest. Then:

- **The folder is missing, or cannot be read.** Nothing of the collection is removed, and each scan logs a warning naming it, until the folder is back.
- **The folder is there, readable, and has its marker.** Its documents really were deleted, and they are removed as any other delete is, the last one included.
- **The folder is there and readable but has no marker.** This is what the empty folder under a mount that failed looks like, so nothing is removed, and each scan logs a warning naming the collection and the marker. A root the sidecar never saw holding a document, from before the marker existed, is in the same state: if its documents really were all deleted, put an empty `.memoria-root` in it, and the next scan removes them.

The marker is the only signal, so two things defeat it:

- **A mount that fails underneath a marked root,** as when a marked folder holds a separate mount for its subfolders, is not detected.
- **A write while the mount is down.** Ingest creates a missing root and writes into it, and the next scan then marks that folder. If the mount comes back over it and later fails again, that marked folder underneath counts as emptied by deletes. Keep the corpus mounted before the sidecar starts, as compose does with a volume.

Deleting the marker turns the safeguard off for that root, and makes nothing unsafe.

A **busy index**, as when a `qmd` run outside the sidecar holds it, also waits: nothing is removed, a warning is logged, and the next scan tries again.

The sidecar writes with SQLite's `secure_delete` on, so what it deletes is overwritten in the file, not left in free pages. After a scan that removed anything, it folds the write-ahead log into the file and empties it, so old versions of those pages do not linger there either. The job queue's file does the same when it removes a set-aside. Some places can still hold deleted text for a while:

- **The full-text index's word lists.** FTS5 keeps a removed document's words, with their positions, so their order can be read back, until it merges the segments that hold them. It merges segments of like size as more is written, so a large old segment can keep them for a long time. `qmd cleanup` merges them all.
- **The write-ahead log, when the empty fails.** A reader in the middle of a long read stops the log from being emptied, and its frames then stay until SQLite reuses them.
- **An index written before this.** Free pages left by a version that did not use `secure_delete`, or by a `qmd` run outside the sidecar, keep what they held until those pages are reused. `qmd cleanup`, which vacuums the file, or a rebuild of the index removes them.
- **Backups.** A backup taken before the scan holds the document as it was, until restic's `forget` and `prune` drop that snapshot.

## Backups

[`deploy/backup.yaml`](../deploy/backup.yaml) backs up everything in [Volumes](#volumes) with [restic](https://restic.net), from its official image, pinned by digest. It is used beside `compose.yaml`, with the services running:

| What | How |
|---|---|
| The collection roots and QMD's config | restic reads the files. |
| `control.db` and the index | `snapshot` copies each live database with SQLite's online backup, as `sqlite3 .backup` does, and checks each copy, and restic backs up the copies. A plain copy of a database in use can be torn. |
| The job queue's file, with its jobs and set-aside documents | `snapshot` asks the running sidecar for it (`POST /queue:snapshot`, with the sidecar's secret), or with the sidecar stopped copies it directly, checks the copy, and restic backs it up. |

**The sidecar copies the job queue's file.** A running sidecar holds that file under an exclusive lock, which is how one sidecar owns it, and nothing else can read it then. So `snapshot`, which is on the sidecar's network, asks it for a copy. The sidecar writes one, whole and consistent, beside the file as `index.sqlite.queue.sqlite.snapshot`, a name the request cannot change, and answers with its size and SHA-256. `snapshot` checks that the file it reads is that one, checks it as it checks the others, moves it to the staging volume and removes the sidecar's copy. [The sidecar's README](../sidecar/README.md#the-job-queue) says how the copy is made.

**The lock decides whether to ask.** `snapshot` looks at the file first:

- **Nothing holds it.** The sidecar is stopped, so nothing is writing it, and `snapshot` copies it directly, without contacting the sidecar or sending its secret anywhere. This is how a backup with the services stopped works.
- **Something holds it.** That is the running sidecar, so `snapshot` asks it. A refusal fails: a wrong secret, a sidecar running with its jobs in memory only (409 `queue_not_persistent`), or a copy that failed. So does no answer within 60 s, and a copy that is not the one the sidecar answered with, as when two snapshots run at once. That last copy is left where the sidecar wrote it, until its next one replaces it.

**`snapshot` fails, rather than keep an old copy without saying so,** whenever it cannot get the queue as it is, and the same goes for a database that another process holds under a lock, or a copy that fails SQLite's integrity check. It exits 1 and names what failed. The copies replace the staged ones only once every one has been made and checked, so a failed run leaves the staged set as it was: all from the last run that succeeded, never half old and half new. A database that is gone has its staged copy removed by a run that succeeds, for the same reason. Run one `snapshot` at a time: two at once share the staging volume. Run `backup` only when `snapshot` succeeds, as below, or it backs up that older set again. A set-aside lost to a restore means that document is tried again, and set aside again if it still fails.

**What the secret allows here.** Anyone holding it can already reach every route. This one can only write that one copy, beside the queue file, replacing the last: no request names a path, so it cannot write anywhere else or fill the disk past one copy. Calling it again while a backup reads only changes the copy, which `snapshot` then refuses for not matching, so the backup fails rather than taking a mixed file. Each copy holds the event loop while it runs, which for a queue file of job records takes milliseconds.

The index is in the backup because restoring it is much cheaper than rebuilding it. Measured with the scripted worker, 300 documents of 20 KB, 13,455 chunks, took 15 s to embed, and their whole backup took 8 s and stored 8.5 MiB. A real worker embeds far more slowly than the scripted one, and a full corpus is far larger, so re-embedding it takes hours where restoring takes minutes. A daily delta of 40 documents embedded in 3 s on the scripted worker, and its backup took 8 s and stored 5.6 MiB: most of a day's new data is the index's pages, which change throughout. On a real worker, an RTX 5080 serving Qwen3-Embedding-0.6B, a copy of the real corpus of 1,559 documents and 25,694 chunks embedded in 6 min 48 s, about 63 chunks a second, and a delta of its 40 newest documents, 1,106 chunks, in a 20 s job: 2 s of scan and 17 s of embedding.

**What is in it.** Only what Memoria stores: the documents, which are redacted before they are written, and the control database, which holds key hashes and the audit log, never a plaintext key. It is still private text, so the backup is encrypted, and the password decides who can read it.

### Backup environment

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_BACKUP_PASSWORD_FILE` | | Required. A file holding restic's password, one line, mode 600, outside the repository. |
| `MEMORIA_BACKUP_REPOSITORY` | `memoria-backup-repository` | Where the repository is, as compose mounts it: a host directory on another disk, or that volume's name. |
| `RESTIC_REPOSITORY` | `/repository` | The repository as restic names it. The default is the mount above. Set a remote one, such as `rest:https://...` or `s3:...`, with the credentials it needs in an override, and the mount goes unused. |
| `MEMORIA_RESTORE_SNAPSHOT` | `latest` | Which snapshot `restore` restores. `restic snapshots` lists them. |

**Where it goes.** Not this host's disk alone: a backup on the disk it backs up is lost with it. Use a mounted disk or NAS, or a remote repository. With a local repository, copy it off the host too, for example with `restic copy` to a second one.

**The password.** Losing it loses every backup, and nothing can recover them. Keep it in a password manager as well as in the file, and keep `deploy/.env` there too: a restore needs the same `MEMORIA_INGEST_DIGEST_KEY`, or deleted documents stop being recognized when sent again.

### Taking one

```bash
B="docker compose -f deploy/compose.yaml -f deploy/backup.yaml"
$B run --rm backup init       # once, for a new repository
$B run --rm snapshot && $B run --rm backup   # daily
$B run --rm backup forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12 --prune
$B run --rm backup check
```

Run it from a systemd timer or cron, and alert on its exit status.

### Restore

`restore` writes only into empty volumes, and refuses if any of them holds data, so it cannot overwrite a deployment that is running or was merely stopped:

```bash
docker compose -f deploy/compose.yaml down
docker volume rm memoria_memoria-data memoria_memoria-corpus memoria_memoria-qmd-index memoria_memoria-qmd-config
docker compose -f deploy/compose.yaml -f deploy/backup.yaml run --rm restore
docker compose -f deploy/compose.yaml up -d
```

Remove only the volumes you mean to replace, by name, and only once the backup you are restoring has been checked: `restic snapshots` and `restic check`. The restore puts each file back with the owner its service runs as.

`scripts/e2e/backup-restore.ts` proves this path on a throwaway project. It takes its backups with the services running, destroys each data volume by name, restores, and checks that the keys, documents, deletes, a waiting job and a document set aside from embedding, and search came back. It also checks a snapshot with the services stopped, that `snapshot` fails when it cannot reach a running sidecar, and that a restore into volumes holding data is refused.

### Lost collection records

The index keeps a record of each collection: its root, pattern, ignore rules and contexts. Stock QMD deletes a record whenever its config does not name that collection, and a missing config names none, so any `qmd` command, even `qmd status`, deleted them all while the documents stayed (upstream tobi/qmd#1048). Memoria never writes a collection into the config volume, so this was every `qmd` command run in the container.

The image's SDK is patched against this (`sidecar/scripts/patch-qmd.ts`). A config now adds and updates the records it names and never deletes one. A config that names no collection, as a missing one reads, leaves the global context too; one that names collections and no global context still clears it, which is how `qmd context rm /` works. The patch covers only the SDK installed with the sidecar: a `qmd` installed anywhere else on the host is stock.

**If records are lost anyway,** the sidecar refuses to start, naming the collections whose documents have no record, and changes nothing. Their documents and vectors are still in the index, and a record put back under the same name and root finds them again. Nothing is re-embedded, as vectors belong to a document's content, which has not changed. Put the records back before anything scans those collections, because a scan without its ignore rules indexes the files they kept out of search.

- **If you had set ignore rules or contexts,** write them into `/config/qmd/index.yml` on the config volume, naming each collection with its root, pattern, rules and contexts in QMD's config format, then run `qmd status` once in the container (`node node_modules/@tobilu/qmd/bin/qmd status`). It records them all from the file. Keep that file: from then on it is the copy of those settings.
- **If you had not,** run only the `init-index.mjs` command from [first run](#first-run), with the names the error lists. It records each one again with its root under `/corpus`.

Then start the sidecar.

On a host where the sidecar serves an index that `qmd` itself maintains, restore `~/.config/qmd/index.yml` from a backup instead, and run `qmd status` once. Its records, contexts included, come back from the file.

### The control database

API keys and the audit log live in a SQLite file at `/data/control.db`, on the `memoria-data` volume. Restoring it forgets the deletes made since the backup, so deleted content can be sent again and stored; see [the limits](#limits-accepted-for-this-deployment).

### The first admin key

Nothing can call the API until a key exists. The first-run command above prints it once on stdout and exits without starting a host. The key is never stored in plaintext and cannot be shown again, so copy it into your password manager then. The command refuses while an active admin key exists; pass `--allow-additional-admin` after the label to add another deliberately. If you lose the only admin key, run the command with that flag, then revoke the lost key with the new one.

This key has only the `admin` scope and cannot read the corpus. Issue clients their own scoped keys with it; see [configuration.md](configuration.md#memoriaauth--api-keys-rate-limits-audit).

## Nothing may sit between the API and the sidecar

Ingest runs with the API and the sidecar on one Linux host, as two compose services on one bridge network, with the API calling the sidecar by service name and **no published sidecar port**. Run one API instance.

The rule: **nothing may sit between the API and the sidecar.** No reverse proxy, no TLS terminator, no load balancer, no `docker-proxy` from a published port, and no gateway that buffers a request or answers for the sidecar. The reason is that a write is tied to its connection. If the API dies or gives up on a write, its connection closes, and the sidecar drops the write just before it would land. Anything holding its own connection to the sidecar keeps that write alive after the API has gone, and anything that answers 502 and forwards anyway makes the API take a write that may land for one that did not. Then a write can land after a delete has been answered, and only repeating the delete clears it.

Two API instances have the same effect, as the locks that order writes and deletes are in the API's memory.

Reaching the sidecar across a network instead still serves search, but weakens ingest: a lost tunnel or a host crash closes nothing until a TCP timeout, so the sidecar cannot tell its caller has gone. On Linux a connection cut off that way still looked open after 75 s.

## Limits accepted for this deployment

- **Run the API with at least a 1 GiB heap** (`DOTNET_GCHeapHardLimit`). Under less, large uploads are refused more often, and running out of memory can kill the process.
- **A write already past its connection check can land after a delete.** The rename follows the check in about 0.1 ms, and at most 69 ms when measured. Repeating the delete clears it.
- **A mount that disappears after a collection opened is not noticed.** A missing directory will not open, but an empty directory that is really an unmounted volume cannot be told from a new one. If that empty directory opens the collection, or an empty remount follows opening, search stops returning the documents, documents written then stay in the other mount, and ingest overwrites or deletes whatever is there once the real volume returns. Mount the volume before first use.
- **Restoring the control database forgets deletes made since the backup.** Deleted content can be sent again and stored. A delete of a newer file still works.
- **A sidecar crash can leave `.memoria-*.tmp` files holding text,** until a later write in that folder removes them after an hour. They are not indexed, but they may hold secret text, so a credential cleanup should check for them.

## The workers

The sidecar sends embedding, reranking and query expansion to workers on other machines, over WireGuard. List them in `MEMORIA_QMD_WORKERS`, which names the variable holding each token, never the token. For example, with tunnel addresses as in the table below:

```json
[
  { "name": "gpu", "url": "http://10.0.0.3:8383", "tokenEnv": "MEMORIA_WORKER_TOKEN_GPU", "priority": 1 },
  { "name": "laptop", "url": "http://10.0.0.4:8383", "tokenEnv": "MEMORIA_WORKER_TOKEN_LAPTOP", "priority": 2, "capabilities": ["rerank", "generate"] }
]
```

[workers.md](workers.md) covers the rest of the list's fields, including each worker's `availability` thresholds, issuing and rotating worker tokens, and setting up a worker. The compose file takes the list from `MEMORIA_QMD_WORKERS`, so a compose install sets the pool there rather than in `MEMORIA_QMD_CONFIG_FILE`. To take a worker out for maintenance without editing the list, drain it with `memoria workers drain <name>` ([cli.md](cli.md#operating-the-backend)).

### With every worker down

The host runs no model, so search keeps answering, from BM25 alone, and says so. Measured on this compose file with both workers unreachable:

- A search answered in 5 to 30 ms, with `degraded: true` and `degradedReasons` of `no_rerank` and `no_vectors`, and `no_expansion` too when the search asked for expansion.
- New documents were found, as a scan needs no model. Embedding waits for a worker and retries; it never falls back to this host.
- `/ready` answered 200 with `status: "degraded"`, and `/ready?strict=true` answered 503.
- No llama.cpp library was loaded, no model file existed, and the sidecar's resident memory stayed under 120 MiB.

`MEMORIA_QMD_LLM_FALLBACK` is `0` here, and the image holds no llama.cpp binaries, so turning it on does nothing either.

## Traffic between hosts: WireGuard

The sidecar and the API share a host and a bridge network, so only traffic to the workers leaves the host. It runs over a WireGuard tunnel: the services speak plain http on tunnel addresses, and the tunnel does the encrypting, including of the worker tokens and whole documents being embedded.

The addresses here are examples:

| Host | Tunnel address | Serves | Port |
|---|---|---|---|
| API host | `10.0.0.1` | nothing to the tunnel | |
| GPU PC | `10.0.0.3` | the sidecar | 8383 |
| Laptop | `10.0.0.4` | the sidecar | 8383 |

**The tunnel.** Make a key pair on each machine with `wg genkey | tee private.key | wg pubkey > public.key`, or let the WireGuard app make one for a new empty tunnel. Only public keys leave the machine. The API host has a peer for each worker, with that worker's tunnel address alone as its allowed IPs. It needs no `Endpoint`, as each worker dials in:

```ini
# /etc/wireguard/wg0.conf on the API host
[Interface]
Address = 10.0.0.1/24
ListenPort = 51820
PrivateKey = <the API host's private key>

[Peer]
# GPU PC
PublicKey = <the GPU PC's public key>
AllowedIPs = 10.0.0.3/32
```

Each worker has one peer, the API host, at its LAN address. `PersistentKeepalive` makes the worker dial in as soon as its tunnel is up, so the API host learns where it is and can reach it before the worker has sent anything else:

```ini
# wg0.conf on the GPU PC
[Interface]
Address = 10.0.0.3/24
PrivateKey = <the GPU PC's private key>

[Peer]
# API host
PublicKey = <the API host's public key>
Endpoint = <the API host's LAN address>:51820
AllowedIPs = 10.0.0.1/32
PersistentKeepalive = 25
```

Leave `MTU` unset. The default, 1420 on a network of 1500-byte packets, carries requests of several MiB between the sidecar and a worker.

**What the API host needs.** The sidecar container reaches workers through the host: Docker masquerades its traffic, so a worker sees it from the host's tunnel address. With Docker's default firewall backend, iptables, Docker also accepts its containers' outbound traffic ahead of the host firewall's own rules, so `ufw`'s default of dropping forwarded traffic does not stop it, and no route rule is needed. Docker's opt-in nftables backend keeps its rules in a table of its own, which this has not been tried with. What `ufw`'s default of denying incoming traffic does stop is the workers' handshakes, so allow the tunnel's port:

```sh
ufw allow 51820/udp
```

**What each worker needs.** Bind its tunnel address, say that plain http there is intended, and accept only the API host:

```sh
MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1
MEMORIA_WORKER_HOST=10.0.0.3
```

```powershell
New-NetFirewallRule -DisplayName "memoria-worker" -Direction Inbound -Protocol TCP -LocalPort 8383 -LocalAddress 10.0.0.3 -RemoteAddress 10.0.0.1 -Action Allow
```

**Start order.** The tunnel must be up before a worker binds its address, or the worker fails to start. Make the worker depend on the tunnel, or let it restart until the address exists. The sidecar starts without workers and finds them at its next poll.

`scripts/e2e/wireguard.ts` runs this setup in Docker: an API host with its own Docker, `ufw` and the tunnel, the sidecar from the compose file, and a worker on its tunnel address. See [its README](../scripts/e2e/README.md#the-tunnel-to-a-worker).

TLS is not the chosen setup.

**Host names.** A request must arrive under an IP address, `localhost`, or a name in `MEMORIA_QMD_ALLOWED_HOSTS`, and any request carrying an `Origin` is refused. That stops a web page reaching the service through DNS rebinding. Compose lists `memoria-qmd`, the name the API calls it by. The worker has the same rule, with `MEMORIA_WORKER_ALLOWED_HOSTS`.

**Secrets.** With no secret set, each run makes its own and nothing else can call it. Placeholders, the old README example and repeated characters are refused.

## Memory and large documents

### Sizing the host

What the two processes hold is small. What makes `docker stats` look large is the page cache, which it counts and the kernel gives back under pressure. Measured on this compose file with 1,000 documents of 200 KB, every worker down for the writes and scan, then one worker for the embedding:

| | Sidecar | API |
|---|---|---|
| Resident memory at its peak | 292 MiB, while embedding 454,000 chunks | 306 MiB, during the writes |
| Resident memory idle | about 115 MiB | about 110 MiB |
| `docker stats` during the scan | 520 MiB, of which 89 MiB was the process's own and 406 MiB active page cache | 229 MiB |

The index took 2.1 GB for 192 MB of documents, most of it vectors. Size the host for the two processes' ceilings plus room to cache the index: the sidecar's heap is capped at 4 GiB and the API's at 1 GiB, so 6 GiB covers both at their worst, and the index's size on top keeps searches from reading the disk. Below that, searches still work, more slowly.

Run the sidecar with `--max-old-space-size=4096`. The image sets it. This is a ceiling, not a target. Node's default on a smaller host can be lower, and a search that reaches the limit kills the process.

`sidecar/scripts/patch-qmd.ts` runs on install and fixes the SDK's scans. Without it, one search over six 21 MB documents froze for 35 s and needed over 1 GB of heap. With it, the same search took 2 to 3 s and fitted in 512 MiB. The SDK's functions are checked at start, and a sidecar on an unpatched SDK refuses to start. `/ready` shows `sdk.patched`.

The same script carries the fix for QMD's reranker blend (upstream tobi/qmd#1032). A reranked hit's score is now the reranker's own score, and equal scores keep their rank-fusion order. That moves `minScore` too: the top-ranked hits no longer get a score floor from their rank. Stored indexes and cached reranker scores stay valid, so nothing needs re-embedding. The image applies it when it is built. A source checkout must run `npm ci` in `sidecar/` after updating, or the sidecar refuses to start.

It also carries the config sync fix described under [lost collection records](#lost-collection-records), and the start-up check covers it the same way.

`POST /documents` refuses anything over `MEMORIA_QMD_MAX_DOCUMENT_BYTES` (2 MiB) with a 413 `document_too_large`, and writes nothing. The rest of this section is about stalls, meaning time when the sidecar answers nothing else, including `/health`.

- **Within the cap.** A single search stalls for under a second: 675 ms at worst with one sub-query over 2 MiB documents.
- **Files already on disk, at any size.** The cap applies only at ingest, so these stay indexed and searchable, and their stalls are longer. Measured: 16 MiB of blank lines stalled for 6.0 s, and an update scan of a 170 MiB file for 5.8 s. A 21 MB ordinary transcript chunks in about 340 ms.
- **Very large files of blank lines.** Past 2.2 million break points, about 2 MiB of nothing but newlines, the chunker stops collecting more and cuts the rest at its plain chunk size. So such a document searches and embeds, with cruder chunks. A document that still cannot be chunked is set aside, and costs only itself.

**Concurrent searches.** Each search holds whole bodies while it runs, so memory grows with how many run at once. `MEMORIA_QMD_SEARCH_CONCURRENCY` (4) run together, `MEMORIA_QMD_SEARCH_QUEUE` (16) wait for up to `MEMORIA_QMD_SEARCH_MAX_WAIT_MS` (10 s), and the rest get a 503 `search_busy` with `Retry-After`. Memoria.Api passes that on as a 503. Four is chosen against the 4096 MiB heap: 20 searches of 10 sub-queries over 2 MiB documents peaked at 3.1 GB with 4 running, and the same load ran out of heap at 1024 MiB unless cut to 2 running. If you lower the heap, lower this with it.

**Connections.** One address may hold `MEMORIA_QMD_MAX_CONNECTIONS_PER_SOURCE` (64) connections, so a peer with no secret cannot use up the `MEMORIA_QMD_MAX_CONNECTIONS` total (1,024). List Memoria.Api's address in `MEMORIA_QMD_API_ADDRESSES` to give it 512. Reaching the total takes 16 addresses.

**Frontmatter.** When a key repeats, the last one wins, where it used to give no metadata. That spoofs nothing: filters read a document's own frontmatter, whoever can write that can write any value, and on a transcript the renderer writes those keys itself.

**Why not a worker thread.** Running the SDK in one was evaluated and not adopted. With the scans fixed, a search within the cap stalls for under a second. It would not reliably contain an out-of-memory error either: of four allocation patterns tried, three failed only the worker and one killed the whole process. It would also mean making about 25 backend methods asynchronous and copying 21 MB bodies between threads.

## Reverse proxy and TLS

`nginx-proxy-manager` already terminates TLS on the homelab with Let's Encrypt
certificates, and `cloudflare-ddns` keeps the dynamic IP current. Memoria
needs one proxy host entry:

| Setting | Value |
|---|---|
| Domain | your chosen hostname |
| Scheme / forward | `http` to `memoria-api:8080`, over the proxy's network below |
| SSL | Let's Encrypt, Force SSL, HTTP/2 |
| Websockets | not needed; MCP uses Streamable HTTP (POST + SSE responses) |
| Custom config | `proxy_read_timeout 180s;` — reranked searches take seconds, a cold sidecar longer |

### The proxy's network

The reverse proxy reaches the API on a network of its own, `memoria-proxy`, which
`deploy/compose.yaml` creates and which only `memoria-api` joins. The sidecar is not on
it, so the proxy cannot reach the sidecar at all, and only the API stands between the
internet and the index. Never put the proxy on the `memoria` network: anything there
can call the sidecar directly, with nothing but its shared secret in the way.

Join nginx-proxy-manager to it from its own compose project, as an external network:

```yaml
services:
  nginx-proxy-manager:
    networks: [default, memoria-proxy]
networks:
  memoria-proxy:
    external: true
```

Memoria's compose project creates the network, so:

- **Start Memoria first.** The proxy refuses to start while the network is missing.
- **Do not create the network by hand.** One made with `docker network create` lacks
  compose's label, and Memoria's `up` refuses it. Remove it, and let `up` make it.
- **Memoria's `down` removes it if nothing else is on it,** as when the proxy is
  stopped. Bring Memoria up again before the proxy. With the proxy running, `down`
  leaves the network in place, and the next `up` uses it.

The proxy host's forward is `memoria-api:8080`. Set `MEMORIA_TRUSTED_PROXY` to the
network's subnet, from `docker network inspect memoria-proxy`, rather than the proxy's
own address: Docker assigns that address and can give the proxy another, while only the
API and the proxy are on the network. The subnet stays until the network is made again,
so check it after a `down` that removed the network. `scripts/e2e/compose.ts` checks
that the API is the only container there, and that a container there reaches the API
and cannot reach the sidecar, by name or by address.

### Published ports and the host firewall

`ufw` does not filter a port Docker publishes. Docker writes its own iptables rules,
which accept the traffic before `ufw`'s rules see it, so a port published on the LAN
address is open to the whole LAN whatever `ufw` says. Keep the API's published port on
`127.0.0.1`, the default, and let the proxy reach the API over its network instead.
If LAN clients must connect without the proxy, bind `MEMORIA_BIND_ADDRESS` to the LAN
address and filter in Docker's own `DOCKER-USER` chain, which Docker consults first.
For example, with the host at `192.168.1.10` on `eth0`, to let only the LAN's range reach
the API's published port:

```bash
iptables -I DOCKER-USER -i eth0 -p tcp -m conntrack --ctorigdst 192.168.1.10 --ctorigdstport 8080 ! -s 192.168.1.0/24 -j DROP
```

Match the interface and the host's address as well as the port. `DOCKER-USER` also
sees traffic between containers, and without them the rule would drop the proxy
calling `memoria-api:8080` on its own network.

A rule added this way is gone after a reboot. Keep it in `ufw`'s own file instead,
`/etc/ufw/after.rules`, at its end, so `ufw` loads it with the rest. Do not install
`iptables-persistent` for it: it replaces `ufw`, which the tunnel's rule above relies on.

```text
*filter
:DOCKER-USER - [0:0]
-A DOCKER-USER -i eth0 -p tcp -m conntrack --ctorigdst 192.168.1.10 --ctorigdstport 8080 ! -s 192.168.1.0/24 -j DROP
-A DOCKER-USER -j RETURN
COMMIT
```

Then `ufw reload`, and after the next reboot check it is there with
`iptables -L DOCKER-USER -n`.

`X-Trace-Id` on every response, and `traceId` in every problem-details body,
are the ids to grep for in the API's and the sidecar's logs.

Rate limits and the audit trail key on the API key, so they need nothing from the
proxy. The proxy must terminate TLS: a key on plain HTTP is readable by anyone on
the path. Do not log the `Authorization` header in the proxy's access log.

### Tell the API who its proxy is

**This is required once the proxy host exists.** Failed authentication is limited
per client address. With `Memoria:Auth:TrustedProxies` empty the API treats the
socket peer as the client, and behind a proxy that peer is the proxy, so every caller
shares one budget: 20 bad tokens from anyone would refuse every credential the API
does not already know, for the rest of the minute. Keys it knows keep working, but a
new client's first request can be refused. Two warnings point at this: one at most
hourly when a private address sends `X-Forwarded-For` it is not trusted to send, and
one naming the address when a budget runs out. Neither trusts the header.

1. Find the address the API sees. Give nginx-proxy-manager a fixed address on a
   user-defined Docker network and use that, or use the network's whole subnet if
   nothing untrusted shares it (`docker network inspect <network>`). If the API is
   reached through a published port instead of that network, the peer is usually the
   Docker gateway, not the proxy container. Confirm with `RemoteIp` in the API's log.
2. Set it:

   ```bash
   MEMORIA_TRUSTED_PROXY=172.20.0.2 docker compose -f deploy/compose.yaml up -d
   ```

   More than one entry: `Memoria__Auth__TrustedProxies__0`, `__1`, and so on.
3. The proxy must send this header, and a custom config must not replace it. Check
   that your nginx-proxy-manager version does, in `conf.d/include/proxy.conf`:

   ```nginx
   proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
   ```

   That appends the peer nginx sees to whatever the client sent. The API reads the
   chain from the right and stops at the first address that is not a trusted proxy,
   so anything a client writes on the left is ignored.
4. If Cloudflare or another proxy sits in front of nginx-proxy-manager, what nginx
   appends depends on its `real_ip` settings. Either nginx resolves the real client,
   or you add the outer proxy's ranges to `TrustedProxies`. Otherwise every client
   looks like that proxy.
5. Check it: send a bad token through the proxy, and confirm the `RemoteIp` in the
   API's request log is your address, not the proxy's. Then send it again with
   `X-Forwarded-For: 192.0.2.1` and confirm `RemoteIp` does not change. This check is
   what counts; the steps above only explain it.

Never trust a range that an untrusted client can connect from, and make sure the API's
port cannot be reached except through the proxy. A `/0` range is refused at startup.

This limit protects the API from one address at a time. A flood from many addresses
stays within each one's budget, so limit connections at the proxy as well, for example
with `limit_req` in the proxy host's custom config.

`/ready` stays anonymous so an orchestrator can probe it. It answers 200 while the
sidecar answers, even with no GPU worker up, and tells anonymous callers only the
status word. Point a health check that should fail on degradation at `/ready?strict=true`. See
[configuration.md](configuration.md#anonymous-routes).

### Refuse what only the LAN needs

Two route groups have no business being reachable from the internet, so the proxy
host refuses them for any address outside the LAN:

- **`/ready`.** It needs no key, and each probe makes the sidecar scan the index.
  The API shares one probe among its callers without an admin key for `Memoria:Sidecar:ReadinessCacheTime`
  (2 s), and the sidecar shares one scan among its callers for 2 s, so a flood costs
  one scan every 2 s. Measured on an index of 50,000 documents, one scan took about
  30 ms of the sidecar's single event loop. Fifty `/ready` calls at once took 1.35 s
  of it before the sharing, and 0.12 s after. An orchestrator on the host or the LAN
  is the only caller that needs it.
- **`/v1/admin/*`.** Key management, worker drain, the queue and reindex. An admin key
  is all they ask for, so refusing them at the proxy means a leaked admin key cannot
  be used from outside.

In nginx-proxy-manager, add these to the proxy host's custom config, with your LAN's
range in place of `192.168.1.0/24`:

```nginx
location ~* ^/ready/?$ {
    allow 192.168.1.0/24;
    deny all;
    include conf.d/include/proxy.conf;
}
location ~* ^/v1/admin(/|$) {
    allow 192.168.1.0/24;
    deny all;
    include conf.d/include/proxy.conf;
}
```

- **Case-insensitive, by regex.** The API matches routes whatever their case, and
  ignores a trailing slash, while a plain nginx `location` matches case and all. A
  plain `location /v1/admin/` would let `/V1/Admin/keys` through to the API. A regex
  `location` is also chosen over nginx-proxy-manager's own `location /`.
- **`include`, not `proxy_pass`.** nginx-proxy-manager sets `X-Forwarded-For` and its
  other headers in its own `location /`, and a new `location` inherits none of them.
  Its `proxy.conf` holds those headers and its own `proxy_pass`, so these requests
  carry the client's address like any other, as
  [Tell the API who its proxy is](#tell-the-api-who-its-proxy-is) requires.
- **Who counts as local.** A caller on the host itself reaches nginx through Docker's
  published port, so nginx sees the Docker gateway's address, not `127.0.0.1`. Add
  that address if something on the host needs these routes through the proxy.

With Cloudflare or another proxy in front, `allow` sees that proxy's address unless
nginx resolves the real client, as in step 4 above, so set that up first. Check from
outside the LAN, for example from a phone off Wi-Fi, that `/ready`, `/READY`,
`/v1/admin/keys` and `/V1/Admin/keys` each answer 403 from nginx, not the API's own
401 or 200. Run `nginx -t` inside the proxy's container after saving.

## Upgrade order

**The first build with whole-chunk embedding re-embeds every index once.** Its vectors carry a new fingerprint, so every document reads as pending, and the next embed job for its collection redoes all of it, on the workers here. Until it finishes, that job holds the one job queue, so the ingest behind it waits. `POST /reindex` with `embed: true` on the sidecar does every collection at once, which took under 10 minutes for 46,000 chunks on one RTX 5080. Search keeps using the old vectors until each document's new ones are written, and a document set aside keeps them. Never run a stock `qmd embed` on an index the sidecar serves: it writes vectors under the old fingerprint, which the sidecar then embeds all over again. [workers.md](workers.md#vectors-made-by-stock-qmd-are-embedded-again) explains why.

**An index a newer QMD has migrated is refused.** The sidecar reads the index's schema version before its SDK opens it, and stops at start, saying so, if the version is above the one its SDK writes (1), or if the index has the `vector_rows` table that replaces `vectors_vec` upstream. Serving it would delete its vector rows at every start and leave deleted documents' vectors behind. An index caught partway through a newer QMD's upgrade, with `vector_rows` beside `vectors_vec`, is refused too, as this SDK would let the newer copy fall behind. Rolling the image back across a QMD upgrade, or running a stock `qmd` newer than the pinned one on the index, gets here: run the release that matches the index, or restore the index from a backup taken before. For a `qmd` command, use the one in the image, `node node_modules/@tobilu/qmd/bin/qmd` in a shell in the container, or `docker compose -f deploy/compose.yaml run --rm --no-deps memoria-qmd node_modules/@tobilu/qmd/bin/qmd <command>`, never a global install.

Upgrade the sidecar before the API. The API sends a collection-scoped key's allowlist
with each document read, and the sidecar's strict body schema makes an older sidecar
refuse scoped batch reads with `400`. An older sidecar also ignores the allowlist on
single-document reads and has no glob grammar of its own, so scoped callers would be
back to QMD's global lookup for as long as the two are mismatched.

An older sidecar also refuses the `expand` field that turns query expansion off, which the API sends on every search by default, so every default search would get `400`. If the API has to run first, set `Memoria__Search__ExpandByDefault=true` until the sidecar is upgraded: the API then sends no `expand` at all.

## Exposure is gated

Nothing in this repository exposes Memoria to the internet, and nothing
should before the host is hardened: patched, with its firewall reviewed. API keys with collection
scopes are enforced by default, but a key is only as private as the transport,
so TLS through the proxy is part of that gate. `Memoria:Auth:Mode = Disabled` is
for development on loopback, where the host refuses any other address unless
`Memoria:Auth:AllowDisabledOnNetwork` is set, and must never be set on an exposed host.

Until it is: keep the compose port on `127.0.0.1` or the LAN address,
do not create the proxy host entry, and do not forward a router port.

## Deployment checklist

Run these from the release you are about to deploy, before pulling its images or
restarting a worker:

```bash
(cd sidecar && npm audit --omit=dev --audit-level=high)
(cd worker && npm audit --omit=dev --audit-level=high)
```

Both must exit 0. A critical or high finding stops the deployment until it is
fixed, or until `sidecar/README.md` records it as an accepted risk with an
argument for why it cannot be reached. Do not silence a finding with an audit
exclusion. Findings appear without any change to this repository, because new
advisories are published against versions already in the lockfile, so a result
from an earlier deployment does not carry over.

The audit reads the lockfile, which is what the sidecar's image and a worker's
`npm ci` install, and nothing else. It says nothing about a `qmd` CLI installed
globally on the host, which is one more reason to use the one inside the image and
never to serve QMD's HTTP transport from a host install.
`sidecar/README.md` lists the current overrides and what to do with them on a
QMD bump.

Then check the network boundary, below.

## The loopback invariant

QMD itself is never reachable from anywhere. The sidecar is the only process that opens the index. It requires the shared secret on every route except `/health` and `/openapi.json`, which give away only that it is up and the contract it serves, and compares it in constant time. `Memoria.Api` is the only intended caller. If any path exists from a client to the sidecar that does not pass through `Memoria.Api`, authentication has been bypassed entirely. When auditing the deployment, check that:

- the sidecar container publishes no port: `docker port memoria-qmd` prints nothing;
- the API and the sidecar are the only containers on their network: `docker network inspect memoria_memoria`;
- the API is the only Memoria container on the proxy's network: `docker network inspect memoria-proxy`;
- the API's port is published on `127.0.0.1`, or, if on the LAN address, filtered in `DOCKER-USER`, as `ufw` does not filter it: `docker port memoria-api`;
- each worker's port accepts only the API host's tunnel address.
