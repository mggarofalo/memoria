#!/usr/bin/env python3
"""Regenerate the session fixtures and goldens from the Python exporters.

Every input here is synthetic. The goldens are what the exporters themselves produce, so a
passing SessionGoldenTests means the C# port matches them byte for byte. Run this only when
the exporters change or a case is added:

    python -I -B generate_goldens.py \
        --claude-exporter <agent-skills>/skills/recall/export_sessions.py \
        --codex-exporter <agent-skills>/codex/skills/recall/scripts/export_sessions.py

-B matters: it stops Python writing bytecode beside the exporters. The script calls the
exporters' parsing and rendering functions directly and writes only under this directory.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import sys
import tempfile
import types
from pathlib import Path

HERE = Path(__file__).resolve().parent
HOME = "/Users/synth"
CLAUDE_ID = "abcdef12-3456-7890-abcd-ef1234567890"
CODEX_ID = "11111111-2222-4333-8444-555555555555"
FALLBACK_EPOCH = 1_700_000_000


def load(path: str, name: str):
    sys.modules.setdefault("fcntl", types.ModuleType("fcntl"))  # the Codex exporter imports it; unused here
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.HOME = Path(HOME)
    return module


def line(value) -> str:
    return json.dumps(value, ensure_ascii=False) + "\n"


def claude(kind: str, text, second: int, **extra) -> str:
    content = text if isinstance(text, (str, list)) else []
    return line({
        "parentUuid": None, "isSidechain": False, "userType": "external",
        "cwd": f"{HOME}/Source/demo-app", "sessionId": CLAUDE_ID, "version": "2.0.0",
        "gitBranch": "feature/parity", "slug": "quiet-testing-otter", "type": kind,
        "message": {"role": kind, "content": content},
        "uuid": f"00000000-0000-4000-8000-{second:012d}",
        "timestamp": f"2026-10-01T10:00:{second:02d}.250Z", **extra,
    })


def claude_basic() -> str:
    return "".join([
        line({"type": "summary", "summary": "synthetic", "leafUuid": "x"}),
        line({"type": "file-history-snapshot", "messageId": "m0", "snapshot": {
            "messageId": "m0", "timestamp": "2026-10-01T09:59:59.000Z",
            "trackedFileBackups": {"src\\zeta.cs": {"version": 1}, "src/alpha.cs": {"version": 1}},
        }}),
        claude("user", "  Fix the parser, please.\nSecond line of the request.  ", 1),
        claude("assistant", [
            {"type": "thinking", "thinking": "not exported", "signature": "x"},
            {"type": "text", "text": "Looking at it now."},
            {"type": "tool_use", "id": "t1", "name": "Read", "input": {"file_path": "x"}},
            {"type": "text", "text": "  \n "},
            {"type": "text", "text": "Found it: café \U0001F680 and 日本語."},
        ], 2),
        claude("user", [{"type": "tool_result", "tool_use_id": "t1", "content": "not exported"}], 3),
        claude("user", [{"type": "text", "text": "[Request interrupted by user for tool use]"}], 4),
        line({"type": "progress", "data": {"type": "hook_progress"}, "timestamp": "2026-10-01T10:00:05Z"}),
        line({"type": "system", "subtype": "turn_duration", "durationMs": 10, "timestamp": "2026-10-01T10:00:05Z"}),
        "\n",
        "{not json\n",
        "[1, 2, 3]\n",
        '"a bare string"\n',
        line({"type": "some-future-record", "note": "unknown types are ignored"}),
        line({"type": "file-history-snapshot", "snapshot": {
            "trackedFileBackups": {"src/later.cs": {"version": 2}, "src/alpha.cs": {"version": 2}},
        }}),
        claude("assistant", "Done. --- is not a fence here.\n---\nstill body", 6),
        claude("user", "this record has no newline and stays unread", 7).rstrip("\n"),
    ])


def claude_parts() -> str:
    """Three parts at the real 65536 limit; astral text proves sizes count code points, not UTF-16 units."""
    rocket = "\U0001F680"
    return "".join([
        claude("user", "A" * 40000, 1),
        claude("assistant", rocket * 25000, 2),  # 25000 code points, 50000 UTF-16 units
        line({"type": "file-history-snapshot", "snapshot": {"trackedFileBackups": {"first-part.txt": {}}}}),
        claude("user", "B" * 40000, 3),
        line({"type": "file-history-snapshot", "snapshot": {"trackedFileBackups": {"second-part.txt": {}}}}),
        claude("assistant", "C" * 70000, 4),
        claude("user", "D" * 10, 5),
    ])


def claude_no_cwd() -> str:
    """No timestamp and no cwd: the start falls back and the project comes from the directory name."""
    return "".join(
        line({"type": kind, "message": {"role": kind, "content": text}})
        for kind, text in (("assistant", "An assistant message first, so a part can have no title."), ("user", "then a user"))
    )


def claude_home() -> str:
    def at(cwd: str) -> str:
        return line({"type": "user", "cwd": cwd, "timestamp": "2026-10-02T23:30:00-05:00",
                     "message": {"role": "user", "content": "from the home directory"}})
    return at(HOME) + line({"type": "assistant", "timestamp": "2026-10-03T01:00:00.5+02:00",
                            "message": {"role": "assistant", "content": "offsets are kept"}})


def claude_hostile_values() -> str:
    """Values that would break naive frontmatter. The exporter quotes these correctly, so they must match too."""
    return line({
        "type": "user", "timestamp": "2026-10-01T10:00:00Z",
        "cwd": '/tmp/x\n---\ninjected: "true"',
        "gitBranch": 'evil/branch"\n---\nproject: pwned\n&anchor *alias #c: d',
        "slug": "back\\slash\ttab\x01ctl",
        "message": {"role": "user", "content": "---\nsession_id: spoofed\n---\n# hostile title line"},
    })


def codex(kind: str, payload: dict, second: int) -> str:
    return line({"timestamp": f"2026-09-09T14:00:{second:02d}.125Z", "type": kind, "payload": payload})


def codex_message(role: str, text: str, second: int, **extra) -> str:
    block = "input_text" if role == "user" else "output_text"
    return codex("response_item", {"type": "message", "role": role, "content": [{"type": block, "text": text}], **extra}, second)


def codex_basic() -> str:
    return "".join([
        codex("session_meta", {"id": CODEX_ID, "session_id": CODEX_ID, "cwd": f"{HOME}/Source/demo-app/sub/dir", "source": "cli"}, 0),
        codex("turn_context", {"turn_id": "turn-1", "cwd": "ignored"}, 1),
        codex_message("user", "### Fix the #1 bug\nmore detail", 2),
        codex("response_item", {"type": "reasoning", "summary": [], "encrypted_content": "opaque"}, 3),
        codex("response_item", {"type": "custom_tool_call", "name": "apply_patch", "input": "*** Update File: a.py\n"}, 4),
        codex("response_item", {"type": "function_call", "name": "shell", "arguments": "{}"}, 4),
        codex_message("assistant", "Patched. \U0001F680", 5),
        "{not json\n",
        "\n",
        line({"type": "future_record", "payload": {"x": 1}}),
        codex("event_msg", {"type": "agent_message", "turn_id": "ignored", "message": "not exported"}, 6),
        codex("event_msg", {"type": "task_started", "turn_id": "turn-2"}, 7),
        codex("compacted", {"replacement_history": [{"type": "compaction", "encrypted_content": "opaque"}]}, 8),
        codex_message("user", "second question", 9),
        codex_message("assistant", "answered with its own turn", 10,
                      internal_chat_message_metadata_passthrough={"turn_id": "turn-from-metadata"}),
        codex("response_item", {"type": "message", "role": "developer", "content": [{"type": "input_text", "text": "not exported"}]}, 11),
    ])


def codex_subagent() -> str:
    return "".join([
        codex("session_meta", {
            "id": CODEX_ID, "session_id": "root-thread-0001", "parent_thread_id": "parent-thread-0002",
            "forked_from_id": "fork-0003", "agent_path": "researcher/one", "agent_role": "researcher",
            "cwd": "/srv/work/outside-home", "source": {"subagent": {"thread_spawn": {"depth": 1}}},
        }, 0),
        codex("session_meta", {"id": "later-meta-is-ignored", "cwd": "/ignored"}, 1),
        codex_message("assistant", "An assistant message with no turn and no user title.", 2),
    ])


def codex_no_meta() -> str:
    """No session_meta and no timestamps: the id comes from the file name, the date is the epoch."""
    return line({"type": "response_item", "payload": {"type": "message", "role": "user", "content": "plain string content"}})


def codex_titles() -> str:
    return "".join([
        codex("session_meta", {"id": CODEX_ID, "cwd": HOME, "source": "exec"}, 0),
        codex_message("user", "###  second logical line", 1),
    ])


def codex_parts() -> str:
    rocket = "\U0001F680"
    return "".join([
        codex("session_meta", {"id": CODEX_ID, "cwd": f"{HOME}/Source", "source": "cli"}, 0),
        codex_message("user", "A" * 40000, 1),
        codex_message("assistant", rocket * 25000, 2),
        codex_message("user", "B" * 70000, 3),
    ])


CASES = [
    {"name": "claude-basic", "format": "claude-code", "session_id": CLAUDE_ID, "input": claude_basic},
    {"name": "claude-parts", "format": "claude-code", "session_id": CLAUDE_ID, "input": claude_parts},
    {"name": "claude-no-cwd", "format": "claude-code", "session_id": CLAUDE_ID, "input": claude_no_cwd,
     "project_dir": "-Users-synth-Source-derived-name--claude-worktrees-agent-1"},
    {"name": "claude-home", "format": "claude-code", "session_id": CLAUDE_ID, "input": claude_home},
    {"name": "claude-hostile-values", "format": "claude-code", "session_id": CLAUDE_ID, "input": claude_hostile_values},
    {"name": "codex-basic", "format": "codex", "session_id": f"rollout-2026-09-09T10-00-00-{CODEX_ID}", "input": codex_basic},
    {"name": "codex-subagent", "format": "codex", "session_id": f"rollout-2026-09-09T10-00-00-{CODEX_ID}", "input": codex_subagent},
    {"name": "codex-no-meta", "format": "codex", "session_id": f"rollout-2026-09-09T10-00-00-{CODEX_ID}", "input": codex_no_meta},
    {"name": "codex-titles", "format": "codex", "session_id": CODEX_ID, "input": codex_titles},
    {"name": "codex-parts", "format": "codex", "session_id": CODEX_ID, "input": codex_parts},
]


def render_claude(module, source: Path, session_id: str, project_dir: str):
    with tempfile.TemporaryDirectory() as temporary:
        copy = Path(temporary) / f"{session_id}.jsonl"
        shutil.copyfile(source, copy)
        os.utime(copy, (FALLBACK_EPOCH, FALLBACK_EPOCH))
        stats = module.new_stats()
        state, _ = module.read_appended(copy, module.blank_state(copy, project_dir), stats)
        start = module.session_start(state, copy)
        documents, message_start = [], 0
        for index, messages in enumerate(module.chunk_messages(state["messages"]), 1):
            name = module.chunk_filename(start.strftime("%Y-%m-%d"), state["session_id"][:8], index)
            documents.append((name, module.render_chunk(state, start, messages, index, message_start)))
            message_start += len(messages)
    return documents, stats["malformed_lines"]


def render_codex(module, source: Path, session_id: str):
    with tempfile.TemporaryDirectory() as temporary:
        copy = Path(temporary) / f"{session_id}.jsonl"
        shutil.copyfile(source, copy)
        state, _ = module.read_appended(copy, module.blank_state(copy))
        documents, message_start = [], 0
        for index, messages in enumerate(module.chunk_messages(state["messages"]), 1):
            documents.append((module.target_path(state, index).name, module.render_chunk(state, messages, index, message_start)))
            message_start += len(messages)
    return documents, None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--claude-exporter", required=True)
    parser.add_argument("--codex-exporter", required=True)
    args = parser.parse_args()
    claude_module = load(args.claude_exporter, "claude_exporter")
    codex_module = load(args.codex_exporter, "codex_exporter")

    inputs, goldens = HERE / "inputs", HERE / "goldens"
    for directory in (inputs, goldens):
        shutil.rmtree(directory, ignore_errors=True)
        directory.mkdir()

    manifest = []
    for case in CASES:
        source = inputs / f"{case['name']}.jsonl"
        source.write_bytes(case["input"]().encode("utf-8"))
        if case["format"] == "claude-code":
            documents, malformed = render_claude(claude_module, source, case["session_id"], case.get("project_dir", ""))
        else:
            documents, malformed = render_codex(codex_module, source, case["session_id"])
        folder = goldens / case["name"]
        folder.mkdir()
        for name, text in documents:
            (folder / name).write_bytes(text.encode("utf-8"))
        manifest.append({
            "name": case["name"], "format": case["format"], "session_id": case["session_id"],
            "project_dir": case.get("project_dir"), "fallback_unix": FALLBACK_EPOCH if case["format"] == "claude-code" else 0,
            "malformed_lines": malformed, "documents": [name for name, _ in documents],
        })
    (HERE / "cases.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(manifest)} cases, {sum(len(c['documents']) for c in manifest)} documents")


if __name__ == "__main__":
    main()
