"""Live exchange proof: a RUNNING Claude Code process moves to the other account on its next call.

Runs on Windows and on macOS, in ISOLATED config folders only - never the machine's real default
Claude login. What it does:

1. Reads the active account of the isolated "default" folder and the spare it is exchanged with.
2. Starts `claude -p` on the isolated default folder with a task that pauses inside a tool call.
3. While that call is paused, exchanges the key with `cc-devthrottle license switch`.
4. Prints every `rate_limit_event` the running process received. Each one carries the reset times of
   the account that served that call, so the call after the exchange is shown to be on the other
   account - or refused with the other account's own usage limit and its own reset time.
5. Reads the folders back, then exchanges again to leave them as it found them.

Run it with the Python that has cc-devthrottle's dependencies (the installed tool's own environment). It
runs the license code from this checkout, not the installed tool:

    <tool python> live_exchange_proof.py --tool-src <checkout>/tools/cc-devthrottle/src \
        --default-config-dir <isolated folder> --store-dir <isolated store> --to <spare account email> \
        --alias <email>="account B" --alias <email>="account C" --machine-label "the Mac"

Folders are given in their one canonical form (absolute, no trailing separator; backslashes on Windows),
because the store refuses any other spelling.

Every email address passed with --alias, the machine name and the home folder are replaced in what it
prints, so the output can be committed as proof.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import shutil
import socket
import tempfile
import subprocess
import sys
import threading
import time
from pathlib import Path

PAUSE_SECONDS = 40
SWITCH_DELAY_SECONDS = 5
MODEL = "claude-haiku-5-5"


class Redactor:
    """Replaces every private string (addresses, machine name, home folder) before anything prints."""

    def __init__(self, aliases: dict[str, str], machine_label: str) -> None:
        self._pairs: list[tuple[str, str]] = sorted(aliases.items(), key=lambda kv: -len(kv[0]))
        host = socket.gethostname()
        self._pairs.append((host, machine_label))
        self._pairs.append((host.lower(), machine_label))
        home = str(Path.home())
        for variant in {home, home.replace("\\", "/"), home.replace("\\", "\\\\")}:
            self._pairs.append((variant, "~"))

    def __call__(self, text: str) -> str:
        for secret, label in self._pairs:
            text = text.replace(secret, label)
        return text


class Proof:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.redact = Redactor(dict(a.split("=", 1) for a in args.alias), args.machine_label)
        self.t0 = time.time()
        self.events: list[dict] = []
        self.switch_done_at: float | None = None
        self.switch_ok = False
        self._lock = threading.Lock()

    def say(self, text: str) -> None:
        with self._lock:
            print("%6.1fs  %s" % (time.time() - self.t0, self.redact(text)), flush=True)

    # ---- the code under test -------------------------------------------------------------------

    def license(self, *verb: str) -> subprocess.CompletedProcess:
        command = [sys.executable, "-c", "from cc_devthrottle.cli import tool_main; tool_main()",
                   "license", *verb,
                   "--default-config-dir", self.args.default_config_dir,
                   "--store-dir", self.args.store_dir]
        env = dict(os.environ, PYTHONPATH=self.package_root, PYTHONIOENCODING="utf-8")
        return subprocess.run(command, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")

    def active_account(self) -> str:
        """The account the isolated default folder is on, as Claude Code itself reports it."""
        env = dict(os.environ, CLAUDE_CONFIG_DIR=self.args.default_config_dir)
        result = subprocess.run([self.claude, "auth", "status"], env=env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace")
        if result.returncode != 0:
            raise SystemExit("claude auth status failed:\n" + self.redact(result.stdout + result.stderr))
        return json.loads(result.stdout).get("email") or "(none)"

    def switch(self, to: str, label: str) -> bool:
        result = self.license("switch", to)
        for line in (result.stdout + result.stderr).splitlines():
            self.say("  [%s] %s" % (label, line))
        if result.returncode != 0:
            self.say("SWITCH FAILED, exit %d" % result.returncode)
            return False
        return True

    # ---- the running task ----------------------------------------------------------------------

    def pause_command(self) -> str:
        python = "python" if platform.system() == "Windows" else "python3"
        return '%s -c "import time; time.sleep(%d)"' % (python, PAUSE_SECONDS)

    def run_task(self) -> int:
        pause = self.pause_command()
        prompt = ("Use the Bash tool to run exactly this command in the foreground: %s . "
                  "When it finishes, reply with exactly: SECOND CALL OK" % pause)
        tool_rule = "Bash(%s:*)" % pause.split(" ")[0]
        env = dict(os.environ, CLAUDE_CONFIG_DIR=self.args.default_config_dir)
        work = tempfile.mkdtemp(prefix="license-proof-work-")
        self.say("starting claude -p (model %s) on the isolated default folder; it will run: %s" % (MODEL, pause))
        process = subprocess.Popen(
            [self.claude, "-p", prompt, "--allowedTools", tool_rule, "--model", MODEL,
             "--output-format", "stream-json", "--verbose"],
            env=env, cwd=work, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace")
        for line in process.stdout:
            self.on_line(line)
        return process.wait()

    def on_line(self, line: str) -> None:
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            if line.strip():
                self.say("claude: " + line.rstrip()[:300])
            return
        kind = event.get("type")
        if kind == "rate_limit_event":
            info = event.get("rate_limit_info", {})
            windows = info.get("unifiedWindows", {})
            record = {
                "after_switch": self.switch_done_at is not None,
                "status": info.get("status"),
                "five_hour_resets": windows.get("five_hour", {}).get("resetsAt"),
                "seven_day_resets": windows.get("seven_day", {}).get("resetsAt"),
                "seven_day_utilization": windows.get("seven_day", {}).get("utilization"),
            }
            self.events.append(record)
            self.say("call answered %s the switch: status=%s, five-hour window resets %s, weekly window resets %s (%s%% used)"
                     % ("AFTER" if record["after_switch"] else "before", record["status"],
                        stamp(record["five_hour_resets"]), stamp(record["seven_day_resets"]),
                        pct(record["seven_day_utilization"])))
        elif kind == "assistant":
            for part in event.get("message", {}).get("content", []):
                if part.get("type") == "tool_use":
                    self.say("tool call started: " + json.dumps(part.get("input", {}).get("command")))
                    if self.switch_done_at is None and not getattr(self, "_switch_started", False):
                        self._switch_started = True
                        threading.Thread(target=self.switch_mid_task, daemon=True).start()
                elif part.get("type") == "text":
                    self.say("reply: " + part.get("text", "")[:300])
        elif kind == "result":
            self.say("task finished: %s %s" % (event.get("subtype"), str(event.get("result"))[:300]))

    def switch_mid_task(self) -> None:
        time.sleep(SWITCH_DELAY_SECONDS)
        self.say("the tool call is paused - exchanging the key now: license switch " + self.args.to)
        self.switch_ok = self.switch(self.args.to, "switch")
        self.switch_done_at = time.time()
        self.say("exchange returned; the running task's next call is the test")

    # ---- the whole proof -----------------------------------------------------------------------

    def stage_code_under_test(self) -> None:
        """Make the checkout's tools/cc-devthrottle/src importable as cc_devthrottle (its installed name)."""
        src = Path(self.args.tool_src).resolve()
        if not (src / "license_store.py").is_file():
            raise SystemExit("--tool-src %s has no license_store.py" % src)
        self.package_root = tempfile.mkdtemp(prefix="license-proof-code-")
        shutil.copytree(src, Path(self.package_root) / "cc_devthrottle")

    def run(self) -> int:
        self.stage_code_under_test()
        self.claude = shutil.which("claude")
        if not self.claude:
            raise SystemExit("claude is not on PATH")
        self.say("machine: %s (%s), %s" % (socket.gethostname(), platform.system(), subprocess.run(
            [self.claude, "--version"], capture_output=True, text=True).stdout.strip()))
        before = self.active_account()
        self.say("before: the isolated default folder is on %s" % before)
        listing = self.license("list")
        for line in (listing.stdout + listing.stderr).splitlines():
            self.say("  [list] " + line)
        if listing.returncode != 0:
            raise SystemExit("license list failed")
        if before == self.args.to:
            raise SystemExit("the default folder is already on the target account - nothing to prove")

        exit_code = self.run_task()
        self.say("claude exited %d" % exit_code)

        after = self.active_account()
        self.say("after: the isolated default folder is on %s" % after)
        listing = self.license("list")
        for line in (listing.stdout + listing.stderr).splitlines():
            self.say("  [list] " + line)

        verdict = self.verdict(before, after)

        if not self.args.keep:
            self.say("exchanging back to leave the folders as they were: license switch " + before)
            self.switch(before, "switch back")
            self.say("restored: the isolated default folder is on %s" % self.active_account())
        return verdict

    def verdict(self, before: str, after: str) -> int:
        pre = [e for e in self.events if not e["after_switch"]]
        post = [e for e in self.events if e["after_switch"]]
        checks = [
            ("the exchange command succeeded", self.switch_ok),
            ("the running task made a call before the exchange", bool(pre)),
            ("the running task made a call after the exchange", bool(post)),
            ("the folder reports the target account afterwards", after == self.args.to),
        ]
        if pre and post:
            checks.append(("the call after the exchange was answered by a DIFFERENT account (its own reset times)",
                           (pre[0]["seven_day_resets"], pre[0]["five_hour_resets"])
                           != (post[-1]["seven_day_resets"], post[-1]["five_hour_resets"])))
        self.say("VERDICT")
        for name, ok in checks:
            self.say("  %s  %s" % ("PASS" if ok else "FAIL", name))
        passed = all(ok for _, ok in checks)
        self.say("PROOF %s" % ("PASSED" if passed else "FAILED"))
        return 0 if passed else 1


def stamp(epoch: int | None) -> str:
    if not epoch:
        return "(not given)"
    return time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime(epoch))


def pct(value: float | None) -> str:
    return "?" if value is None else "%d" % round(value * 100)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--default-config-dir", required=True,
                        help="the ISOLATED folder that plays the machine's default Claude config")
    parser.add_argument("--store-dir", required=True, help="the isolated key store holding the spare")
    parser.add_argument("--tool-src", required=True, help="tools/cc-devthrottle/src of the checkout under test")
    parser.add_argument("--to", required=True, help="the spare account to switch to (its email)")
    parser.add_argument("--alias", action="append", default=[],
                        help="EMAIL=LABEL, replaced in everything printed (repeat for each account)")
    parser.add_argument("--machine-label", required=True, help='what to print instead of the machine name, e.g. "the Mac"')
    parser.add_argument("--keep", action="store_true", help="do not exchange back at the end")
    args = parser.parse_args()
    real_default = Path.home() / ".claude"
    if Path(args.default_config_dir).resolve() == real_default.resolve():
        raise SystemExit("refusing: --default-config-dir is the machine's real default Claude login")
    return Proof(args).run()


if __name__ == "__main__":
    sys.exit(main())
