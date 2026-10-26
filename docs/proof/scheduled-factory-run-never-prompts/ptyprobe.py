"""Drive the real Claude Code CLI in a pseudo-terminal and report whether an ask rule prompts.

Usage: python ptyprobe.py <folder> <label> -- <claude args...>
Prints a transcript of the visible screen text and a VERDICT line.

Runs on Windows (pywinpty) and on Linux and macOS (ptyprocess). The child gets this process's environment without
the CLAUDE* variables of the session running the probe, plus:
  PROBE_CONFIG_DIR   an isolated Claude Code configuration folder (CLAUDE_CONFIG_DIR), so the user-level settings
                     the run reads are the test's, never the owner's real ~/.claude/settings.json. It needs no
                     sign-in: the probe reads the current access token from PROBE_CREDENTIALS (default
                     ~/.claude/.credentials.json) and hands it over as CLAUDE_CODE_OAUTH_TOKEN. The refresh token
                     is never copied, so the test cannot rotate the owner's sign-in.
  PROBE_ENV_*        PROBE_ENV_FOO=1 sets FOO=1 in the child.
  PROBE_PROMPT       the prompt typed (default: run echo ASKPROBE-$((40+2))-$PROBE_SECRET with the Bash tool).
  PROBE_MARK         the text that proves the command ran (default ASKPROBE-42-{SECRET}; {SECRET} becomes the secret).
PROBE_SECRET is a random value put in the child's environment only. The model cannot write it without running the
command, so its own one-line description of the command ("Print ASKPROBE-42") never reads as a run.
  PROBE_MISS         a text the model prints when what it was asked to find is not there (verdict MISSING).
                     Ask for it in a way that keeps it out of the typed prompt (for example "NO and SKILL joined").
"""
import json
import os
import re
import secrets
import shlex
import sys
import threading
import time

if os.name == "nt":
    import winpty

    def spawn(argv, cwd, env):
        return winpty.PtyProcess.spawn(argv, cwd=cwd, env=env, dimensions=(50, 160))
else:
    import ptyprocess

    def spawn(argv, cwd, env):
        return ptyprocess.PtyProcessUnicode.spawn(argv, cwd=cwd, env=env, dimensions=(50, 160))

ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[()][A-Za-z0-9]|\x1b[=>]")


def clean(text):
    return ANSI.sub("", text).replace("\r", "")


SECRET = secrets.token_hex(4).upper()


def child_environment():
    env = {k: v for k, v in os.environ.items() if not k.startswith("CLAUDE")}
    env["PROBE_SECRET"] = SECRET
    for key, value in os.environ.items():
        if key.startswith("PROBE_ENV_"):
            env[key[len("PROBE_ENV_"):]] = value
    config = os.environ.get("PROBE_CONFIG_DIR")
    if config:
        credentials = os.environ.get("PROBE_CREDENTIALS") or os.path.join(os.path.expanduser("~"), ".claude", ".credentials.json")
        with open(credentials, encoding="utf-8") as f:
            env["CLAUDE_CODE_OAUTH_TOKEN"] = json.load(f)["claudeAiOauth"]["accessToken"]
        env["CLAUDE_CONFIG_DIR"] = config
    return env


def main():
    folder, label = sys.argv[1], sys.argv[2]
    args = sys.argv[sys.argv.index("--") + 1:]
    line = os.environ.get("PROBE_ARGS") or " ".join(args)
    split = shlex.split(line, posix=os.name != "nt")
    argv = ["claude"] + [a[1:-1] if len(a) > 1 and a[0] == a[-1] == '"' else a for a in split]
    mark = (os.environ.get("PROBE_MARK") or "ASKPROBE-42-{SECRET}").replace("{SECRET}", SECRET)
    miss = os.environ.get("PROBE_MISS")
    print(f"=== {label}: claude {line}  (cwd={folder}, config={'isolated' if os.environ.get('PROBE_CONFIG_DIR') else 'the user own'})", flush=True)
    proc = spawn(argv, folder, child_environment())
    buf = []

    def reader():
        while True:
            try:
                chunk = proc.read(65536)
            except EOFError:
                return
            if chunk:
                buf.append(chunk)

    threading.Thread(target=reader, daemon=True).start()
    raw = ""

    def pump(seconds):
        nonlocal raw
        time.sleep(seconds)
        raw = "".join(buf)

    def screen_tail():
        text = clean("".join(buf)).replace(folder, "<scratch>").replace(os.path.expanduser("~"), "<user-home>")
        return [l.rstrip() for l in text.split("\n") if l.strip()][-25:]

    # Wait for the input box, answering a folder-trust question if one appears.
    trusted_at = 0.0
    start = time.time()
    while time.time() - start < 60:
        pump(1)
        text = clean(raw)
        flat = re.sub(r"[^a-z]", "", text.lower())
        if "trustthisfolder" in flat[-400:] and time.time() - trusted_at > 4:
            proc.write("\r")
            trusted_at = time.time()
            print("[harness] answered the folder-trust question", flush=True)
            time.sleep(2)
            continue
        if ("? for shortcuts" in text or "bypass permissions" in text.lower() or "auto mode" in text.lower()) and time.time() - start > 6:
            break
    try:
        proc.write(os.environ.get("PROBE_PROMPT") or "Use the Bash tool to run exactly this command and nothing else: echo ASKPROBE-$((40+2))-$PROBE_SECRET")
        time.sleep(1)
        proc.write("\r")
    except (EOFError, OSError):
        print("--- last screen lines ---")
        for l in screen_tail():
            print(l.encode("ascii", "replace").decode())
        print(f"VERDICT [{label}]: CLI CLOSED BEFORE THE PROBE", flush=True)
        os._exit(0)
    verdict = "TIMEOUT"
    flat_mark = re.sub(r"\s+", "", mark)
    start = time.time()
    while time.time() - start < 120:
        pump(1)
        text = clean(raw)
        flat = re.sub(r"\s+", "", text)
        if "Doyouwanttoproceed" in flat:
            # Watch on: a dialog a hook answers closes by itself; one nobody answers stays.
            pump(25)
            flat = re.sub(r"\s+", "", clean(raw))
            if flat_mark in flat:
                verdict = "PROMPT SHOWN, THEN ANSWERED WITHOUT A PERSON - RAN"
            else:
                verdict = "PROMPTED AND STILL WAITING after 25s (Do you want to proceed?)"
            break
        if flat_mark in flat:
            verdict = "RAN (no prompt)"
            break
        if miss and miss in flat:
            verdict = f"MISSING (the model answered {miss})"
            break
        if re.search(r"permissiondenied|wasblocked|deniedbyrule|hasbeendenied|isdenied|wasdenied", flat, re.I):
            verdict = "REFUSED"
            break
    pump(3)
    print("--- last screen lines ---")
    for l in screen_tail():
        print(l.encode("ascii", "replace").decode())
    print(f"VERDICT [{label}]: {verdict}", flush=True)
    try:
        proc.write("\x1b")
        time.sleep(0.5)
        proc.terminate(force=True)
    except Exception as ex:  # the probe is ending either way
        print(f"[harness] terminate: {ex}")
    os._exit(0)


if __name__ == "__main__":
    main()
