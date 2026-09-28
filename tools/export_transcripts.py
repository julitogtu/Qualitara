#!/usr/bin/env python3
"""Convert Claude Code session transcripts (.jsonl) into readable Markdown for ai-log/.

Usage:
  python export_transcripts.py <project-transcripts-dir> <out-dir>

Transcripts live in ~/.claude/projects/<encoded-repo-path>/*.jsonl
(Windows: %USERPROFILE%\\.claude\\projects\\...). Raw .jsonl files are also
copied to <out-dir>/raw/ so reviewers can check nothing was edited.
"""
import json, sys, shutil
from pathlib import Path

def text_of(content):
    if isinstance(content, str):
        return [("text", content)]
    out = []
    for b in content or []:
        t = b.get("type")
        if t == "text":
            out.append(("text", b.get("text", "")))
        elif t == "tool_use":
            out.append(("tool_use", f"**{b.get('name')}**\n```json\n{json.dumps(b.get('input'), indent=2)[:4000]}\n```"))
        elif t == "tool_result":
            c = b.get("content")
            if isinstance(c, list):
                c = "\n".join(x.get("text", "") for x in c if isinstance(x, dict))
            out.append(("tool_result", f"```\n{str(c)[:3000]}\n```"))
        # thinking blocks are skipped on purpose (often empty/redacted)
    return out

def convert(path: Path, out_dir: Path):
    lines = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        try:
            e = json.loads(raw)
        except json.JSONDecodeError:
            continue
        if e.get("type") not in ("user", "assistant"):
            continue
        side = " (subagent)" if e.get("isSidechain") else ""
        msg = e.get("message") or {}
        ts = e.get("timestamp", "")
        for kind, body in text_of(msg.get("content")):
            if not body.strip():
                continue
            if kind == "tool_result":
                who = "Tool result"
            elif e["type"] == "user":
                who = "🧑 Me"
            elif kind == "tool_use":
                who = "🤖 Agent → tool"
            else:
                who = "🤖 Agent"
            lines.append(f"### {who}{side} · {ts}\n\n{body}\n")
    prefix = "subagent-" if "subagents" in path.parts else ""
    md = out_dir / (prefix + path.stem + ".md")
    md.write_text(f"# Transcript {path.stem}\n\nSource: raw/{path.name} (unedited)\n\n" + "\n".join(lines), encoding="utf-8")
    (out_dir / "raw").mkdir(exist_ok=True)
    shutil.copy2(path, out_dir / "raw" / (prefix + path.name))
    return md

if __name__ == "__main__":
    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    dst.mkdir(parents=True, exist_ok=True)
    for f in sorted(src.rglob("*.jsonl"), key=lambda p: p.stat().st_mtime):
        print("wrote", convert(f, dst))
