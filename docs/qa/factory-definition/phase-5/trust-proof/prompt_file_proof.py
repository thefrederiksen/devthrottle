"""Factory Definition, phase 5, P7: a prompt saved OUTSIDE the session's folder reaches Claude Code as an @ reference.

A seat's prompt file now goes in its kit, never in the checkout, so the reference the Director types is a full path
outside the working folder instead of today's `.temp/...`. This runs the REAL Claude Code interactively in a
pseudo-terminal, in a scratch folder (marked trusted for the run, the way the Director now does for a seat's
folders), types the reference exactly as the Director composes it, and reads the answer off the screen. Each prompt
file asks for one marker word, so the answer proves the file was read.

Two cases: a path with no space, and a path through a folder whose name has a space.
The permission flag is the one this machine's agent entries carry (--dangerously-skip-permissions), and the model is
the small one, to keep the two calls cheap.

Usage (from the repository root):
    uv run --no-project --with pywinpty --with pyte python docs/qa/factory-definition/phase-5/trust-proof/prompt_file_proof.py
Output: prompt-file-proof-output.txt beside this script.
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import trust_proof as tp  # noqa: E402  (the lock-respecting writer and the pseudo-terminal seat)

OUT = Path(__file__).with_name("prompt-file-proof-output.txt")
PROJECTS = Path.home() / ".claude" / "projects"


def loose(text: str) -> str:
    # PromptArrival.Loose: the Director finds a typed prompt in the transcript by its letters and digits alone.
    return "".join(c for c in text if c.isalnum())


def transcript_check(scratch_id: str, typed: str, file_name: str) -> None:
    """Find the scratch session's own transcript and say whether its user line holds what the Director typed, the way
    PromptArrival looks for it (the first 80 letters and digits)."""
    folders = [d for d in PROJECTS.iterdir() if scratch_id in d.name] if PROJECTS.exists() else []
    needle = loose(typed)[:80]
    for folder in folders:
        for f in folder.glob("*.jsonl"):
            for raw in f.read_text(encoding="utf-8", errors="replace").splitlines():
                try:
                    line = json.loads(raw)
                except json.JSONDecodeError:
                    continue
                if line.get("type") != "user" or line.get("isMeta"):
                    continue
                content = line.get("message", {}).get("content")
                text = content if isinstance(content, str) else " ".join(
                    c.get("text", "") for c in content or [] if isinstance(c, dict) and c.get("type") == "text")
                if file_name in text:
                    tp.say(f"  transcript user line: {text[:200]}")
                    tp.say(f"  holds what was typed, as PromptArrival matches it: {needle in loose(text)}")
                    return
    tp.say(f"  NO transcript user line found for {typed}; the user lines were:")
    for folder in folders:
        for f in folder.glob("*.jsonl"):
            for raw in f.read_text(encoding="utf-8", errors="replace").splitlines():
                if '"type":"user"' in raw.replace(" ", ""):
                    tp.say(f"    | {raw[:600]}")


def reference(path: Path) -> str:
    # LargeInputHandler.MakeAtReference: a path outside the working folder is sent whole, with forward slashes.
    text = str(path).replace("\\", "/")
    return f'@"{text}"' if " " in text else f"@{text}"


def ask(cwd: Path, prompt_file: Path, marker: str) -> None:
    prompt_file.parent.mkdir(parents=True, exist_ok=True)
    prompt_file.write_text(
        "This is a test of reading a prompt from a file.\n"
        f"Reply with exactly this one word and nothing else: {marker}\n", encoding="ascii")
    ref = reference(prompt_file)
    tp.say(f"  typed: {ref}")
    tp.CLAUDE_ARGS = ["--dangerously-skip-permissions", "--model", "haiku"]
    seat = tp.Seat(cwd)
    try:
        verdict = seat.wait_first_screen()
        tp.say(f"  first screen: {verdict}")
        if verdict != "READY PROMPT":
            return
        time.sleep(2.0)
        seat.send(ref)
        time.sleep(1.5)
        seat.send("\r")
        deadline = time.monotonic() + 120.0
        while time.monotonic() < deadline:
            # The marker is in the typed prompt's echo only as part of the file path, never on its own line.
            answered = [l for l in seat.text().splitlines() if marker in l and "input_" not in l and "Reply" not in l]
            if answered:
                tp.say(f"  answer on screen: {answered[-1].strip()}")
                deadline2 = time.monotonic() + 30.0
                while time.monotonic() < deadline2 and not any(
                        prompt_file.name in f.read_text(encoding="utf-8", errors="replace")
                        for d in PROJECTS.iterdir() if cwd.parent.name in d.name for f in d.glob("*.jsonl")):
                    time.sleep(0.5)
                transcript_check(cwd.parent.name, ref, prompt_file.name)
                return
            time.sleep(0.5)
        tp.say("  NO ANSWER within 120s. The screen:")
        for line in seat.text().splitlines():
            tp.say(f"    | {line}")
    finally:
        seat.stop()
        time.sleep(1.0)


def main() -> int:
    scratch = Path(tempfile.gettempdir()) / f"fd-p7-prompt-{uuid.uuid4().hex[:8]}"
    cwd = scratch / "slot"
    cwd.mkdir(parents=True)
    tp.say(f"Scratch root: {tp.key(scratch)}")
    try:
        tp.trust(cwd)
        tp.say("")
        tp.say("CASE 1 - the prompt file in a folder outside the working folder, no space in its path.")
        ask(cwd, scratch / "factory-run" / "kit-1" / "prompts" / "input_20261016_000000_abcdef.txt", "OSPREY-4471")
        tp.say("")
        tp.say("CASE 2 - the same, through a folder whose name has a space.")
        ask(cwd, scratch / "user name" / "factory-run" / "kit-2" / "prompts" / "input_20261016_000001_ghijkl.txt",
            "KESTREL-8813")
        tp.say("")
        tp.say("The working folder afterwards (it must hold no .temp folder):")
        tp.say(f"  .temp exists: {(cwd / '.temp').exists()}")
    finally:
        tp.say("")
        tp.say("CLEAN-UP")
        removed: list[str] = []
        prefix = tp.key(scratch).lower()

        def drop(projects):
            for k in [k for k in projects if k.lower().replace("\\", "/").startswith(prefix)]:
                removed.append(k)
                del projects[k]

        tp.edit_config(drop)
        for k in sorted(removed):
            tp.say(f"  removed projects['{k}']")
        for folder in [d for d in PROJECTS.iterdir() if scratch.name in d.name] if PROJECTS.exists() else []:
            shutil.rmtree(folder, ignore_errors=True)
            tp.say(f"  removed the scratch session's transcript folder: {not folder.exists()}")
        for _ in range(20):
            shutil.rmtree(scratch, ignore_errors=True)
            if not scratch.exists():
                break
            time.sleep(1.0)
        tp.say(f"  scratch root exists afterwards: {scratch.exists()}")
        OUT.write_text("\n".join(tp.lines) + "\n", encoding="ascii", errors="replace")
    return 0


if __name__ == "__main__":
    sys.exit(main())
