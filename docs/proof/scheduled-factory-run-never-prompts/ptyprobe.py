"""Drive the real Claude Code CLI in a pseudo-terminal and report whether an ask rule prompts.

Usage: python ptyprobe.py <folder> <label> -- <claude args...>
Prints a transcript of the visible screen text and a VERDICT line.
"""
import re
import sys
import threading
import time

import winpty

ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[()][A-Za-z0-9]|\x1b[=>]")
MARK = "ASKPROBE"


def clean(text):
    return ANSI.sub("", text).replace("\r", "")


def main():
    folder, label = sys.argv[1], sys.argv[2]
    args = sys.argv[sys.argv.index("--") + 1:]
    import os
    import shlex
    line = os.environ.get("PROBE_ARGS") or " ".join(args)
    argv = ["claude"] + [a[1:-1] if len(a) > 1 and a[0] == a[-1] == '"' else a for a in shlex.split(line, posix=False)]
    cmd = " ".join(argv)
    print(f"=== {label}: claude {line}  (cwd={folder})", flush=True)
    proc = winpty.PtyProcess.spawn(argv, cwd=folder, dimensions=(50, 160))
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

    # Wait for the input box, answering a folder-trust question if one appears.
    sent = False
    trusted = False
    start = time.time()
    while time.time() - start < 60:
        pump(1)
        text = clean(raw)
        if not trusted and ("trust this folder" in text.lower() or "Yes, I trust" in text):
            proc.write("\r")
            trusted = True
            print("[harness] answered the folder-trust question", flush=True)
            time.sleep(2)
            continue
        if not sent and ("? for shortcuts" in text or "bypass permissions" in text.lower() or "auto mode" in text.lower()) and time.time() - start > 6:
            break
    import os
    proc.write(os.environ.get("PROBE_PROMPT") or f"Use the Bash tool to run exactly this command and nothing else: echo {MARK}-$((40+2))")
    time.sleep(1)
    proc.write("\r")
    verdict = "TIMEOUT"
    start = time.time()
    while time.time() - start < 120:
        pump(1)
        text = clean(raw)
        flat = re.sub(r"\s+", "", text)
        if "Doyouwanttoproceed" in flat:
            # Watch on: a dialog a hook answers closes by itself; one nobody answers stays.
            pump(25)
            flat = re.sub(r"\s+", "", clean(raw))
            if f"{MARK}-42" in flat:
                verdict = "PROMPT SHOWN, THEN ANSWERED WITHOUT A PERSON - RAN"
            else:
                verdict = "PROMPTED AND STILL WAITING after 25s (Do you want to proceed?)"
            break
        if f"{MARK}-42" in flat:
            verdict = "RAN (no prompt)"
            break
        if re.search(r"permissiondenied|wasblocked|notallowed|denied", flat, re.I):
            verdict = "REFUSED"
            break
    pump(2)
    text = clean(raw)
    tail = [l.rstrip() for l in text.split("\n") if l.strip()]
    print("--- last screen lines ---")
    for line in tail[-25:]:
        print(line.encode("ascii", "replace").decode())
    print(f"VERDICT [{label}]: {verdict}", flush=True)
    try:
        proc.write("\x1b")
        time.sleep(0.5)
        proc.terminate(force=True)
    except Exception as ex:  # the probe is ending either way
        print(f"[harness] terminate: {ex}")
    import os
    os._exit(0)


if __name__ == "__main__":
    main()
