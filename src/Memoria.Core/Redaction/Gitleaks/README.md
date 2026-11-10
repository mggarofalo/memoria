# Gitleaks rules as data

These files bring gitleaks's default detection rules into Memoria's own engine. Gitleaks itself never runs during ingest.

| File | What it is |
|---|---|
| `gitleaks.toml` | Upstream's `config/gitleaks.toml`, byte for byte, at the version and commit named in `rules.json`. Never edit it. |
| `LICENSE` | Upstream's MIT licence. It is copied into every build as `licenses/gitleaks/LICENSE`. |
| `rules.json` | Generated from `gitleaks.toml`. Embedded in `Memoria.Core`. Never edit it by hand. |

`rules.json` holds each rule's pattern translated from RE2 to .NET, with the original beside it for review, and its keywords, entropy threshold and allowlists. Its `excluded` list names every upstream rule left out, with the reason.

## How the translation works

The translator is `tests/Memoria.Core.Tests/Redaction/Gitleaks/Re2Translator.cs`, and its summary lists every rewrite and refusal. A pattern it cannot translate without changing its meaning fails the generator instead of being imported.

## Changing the version

1. Download `config/gitleaks.toml` and `LICENSE` from the new release tag, and replace the files here.
2. In `tests/Memoria.Core.Tests/Redaction/Gitleaks/GitleaksRuleGenerator.cs`, set `Version`, `Commit` and `Sha256` to match.
3. Regenerate the data: `MEMORIA_WRITE_GITLEAKS_RULES=1 dotnet test Memoria.slnx --filter GitleaksRuleData`.
4. Review the diff of `rules.json`, which is the change in behaviour.

`GitleaksRuleDataTests` fails when `rules.json` no longer matches the pinned file, or when the file no longer matches its pinned hash.
