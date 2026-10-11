"""Live exchange proof: a RUNNING Claude Code process moves to the other account on its next call.

Runs on Windows and on macOS, in ISOLATED config folders only - never the machine's real default
Claude login. What it does:

1. Reads, through the key store, each account's OWN window reset times from the provider (the
   reference readings), and the account the isolated "default" folder is on.
2. Starts `claude -p` on the isolated default folder with a task that pauses inside a tool call.
3. While that call is paused, exchanges the key with `cc-devthrottle license switch`.
4. Matches every call the running process made against the reference readings. Each call reports the
   reset times of the account that served it. The proof passes only when the call made BEFORE the tool
   result matches the source account's own reset times and the call made AFTER it (a request that
   started after the exchange returned) matches the target account's own - or is refused with the
   target account's own limit.
5. Always exchanges back (in a `finally`), and fails unless the folder is back on the source account.

Isolation: the folders must sit inside a test root that a SEPARATE, earlier run marked:

    <tool python> live_exchange_proof.py --init-test-root <folder>

That run only writes the marker; it refuses the home folder, a drive root, or a folder holding the real
default Claude login. A proof run then names the same root with --test-root and refuses any folder
outside it.

The proof run, with the Python that has cc-devthrottle's dependencies (the installed tool's own
environment). It runs the license code from this checkout, not the installed tool:

    <tool python> live_exchange_proof.py --tool-src <checkout>/tools/cc-devthrottle/src \
        --test-root <marked folder> --default-config-dir <folder in it> --store-dir <folder in it> \
        --to <spare account email> --alias <email>=account-b@example.com --alias ... \
        --machine-label "the Mac"

Folders are given in their one canonical form (absolute, no trailing separator; backslashes on Windows),
because the store refuses any other spelling. Every account the run touches must have an --alias, or it
refuses to start; every email address, path, the machine name and the test root are replaced in what it
prints, so the output can be committed as proof. Usage percentages and reset times are printed: they
identify no one.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import platform
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

PAUSE_SECONDS = 40
SWITCH_DELAY_SECONDS = 5
MODEL = "claude-haiku-5-5"
MARKER = ".license-proof-test-root"
LICENSE_TIMEOUT = 120
AUTH_TIMEOUT = 60
TASK_TIMEOUT = 300
USAGE_URL = "https://api.anthropic.com/api/oauth/usage"
#: Reset times from the usage endpoint and from a call's rate-limit event are compared to the minute.
RESET_TOLERANCE_SECONDS = 60

EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
WINDOWS_PATH = re.compile(r"[A-Za-z]:[\\/][^\s\"',;]*")
TIME_ZONE = re.compile(r"\b(?:Africa|America|Antarctica|Asia|Atlantic|Australia|Europe|Indian|Pacific|Etc)/[A-Za-z_+\-/0-9]+")
TAIL = re.compile(r"(<test root>|<tool source>|<home>)[\\/][^\s\"',;]*")
POSIX_PATH = re.compile(r"(?<![\w<])/(?:Users|home|tmp|private|var|opt|Volumes)/[^\s\"',;]*")


class ProofError(Exception):
    """A step that cannot go on; the message is redacted before it prints."""


class Redactor:
    """Replaces every private string before anything prints: the named aliases, the test root, the tool
    source, the machine name, then ANY remaining email address or absolute path as a class."""

    def __init__(self, aliases: dict[str, str], machine_label: str, test_root: str, tool_src: str) -> None:
        pairs = dict(aliases)
        for path, label in ((test_root, "<test root>"), (tool_src, "<tool source>"), (str(Path.home()), "<home>")):
            for spelling in {path, path.replace("\\", "/"), path.replace("\\", "\\\\"), path.replace("/", "\\")}:
                pairs[spelling] = label
        host = socket.gethostname()
        for spelling in {host, host.lower(), host.upper(), host.split(".")[0]}:
            pairs[spelling] = machine_label
        self._pairs = sorted(pairs.items(), key=lambda kv: -len(kv[0]))
        self._allowed = set(aliases.values())

    def __call__(self, text: str) -> str:
        for secret, label in self._pairs:
            text = text.replace(secret, label)
        # A folder under the test root can be named after an account (a spare's folder), so drop the rest.
        text = TAIL.sub(r"\1/...", text)
        text = EMAIL.sub(lambda m: m.group(0) if m.group(0) in self._allowed else "<unlisted email>", text)
        text = WINDOWS_PATH.sub("<path>", text)
        text = POSIX_PATH.sub("<path>", text)
        # A refusal names the account's time zone (e.g. Region/City), which says where its owner lives.
        text = TIME_ZONE.sub("<time zone>", text)
        return "".join(c if ord(c) < 128 else "-" for c in text)


def stamp(epoch: float | None) -> str:
    if not epoch:
        return "(not given)"
    return time.strftime("%Y-%m-%d %H:%M UTC", time.gmtime(epoch))


def pct(value: float | None) -> str:
    return "?" if value is None else "%d" % round(value * 100)


def epoch_of(iso: str | None) -> float | None:
    if not iso:
        return None
    return _dt.datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()


def same_moment(a: float | None, b: float | None) -> bool:
    return a is not None and b is not None and abs(a - b) <= RESET_TOLERANCE_SECONDS


class Proof:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.aliases = dict(a.split("=", 1) for a in args.alias)
        self.redact = Redactor(self.aliases, args.machine_label, args.test_root, str(Path(args.tool_src).resolve()))
        self.t0 = time.time()
        self.events: list[dict] = []
        self.tool_result_seen = False
        self.switch_returned_at: float | None = None
        self.switch_returned_before_tool_result = False
        self.switch_ok = False
        self._switch_started = False
        self._lock = threading.Lock()

    def say(self, text: str) -> None:
        with self._lock:
            print("%6.1fs  %s" % (time.time() - self.t0, self.redact(text)), flush=True)

    # ---- isolation -----------------------------------------------------------------------------

    def check_isolation(self) -> None:
        root = Path(self.args.test_root)
        if not (root / MARKER).is_file():
            raise ProofError("the test root has no %s marker; mark it first with a separate run: "
                             "live_exchange_proof.py --init-test-root <folder>" % MARKER)
        for what, folder in (("--default-config-dir", self.args.default_config_dir), ("--store-dir", self.args.store_dir)):
            path = Path(folder)
            if not path.is_absolute():
                raise ProofError("%s must be absolute" % what)
            if root.resolve() not in path.resolve().parents:
                raise ProofError("%s is not inside the marked test root" % what)

    # ---- the code under test -------------------------------------------------------------------

    def stage_code_under_test(self) -> None:
        """Make the checkout's tools/cc-devthrottle/src importable as cc_devthrottle (its installed name)."""
        src = Path(self.args.tool_src).resolve()
        if not (src / "license_store.py").is_file():
            raise ProofError("--tool-src has no license_store.py")
        self.package_root = tempfile.mkdtemp(prefix="license-proof-code-")
        shutil.copytree(src, Path(self.package_root) / "cc_devthrottle")

    def license(self, *verb: str) -> subprocess.CompletedProcess:
        command = [sys.executable, "-c", "from cc_devthrottle.cli import tool_main; tool_main()",
                   "license", *verb,
                   "--default-config-dir", self.args.default_config_dir,
                   "--store-dir", self.args.store_dir]
        env = dict(os.environ, PYTHONPATH=self.package_root, PYTHONIOENCODING="utf-8")
        return subprocess.run(command, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace",
                              timeout=LICENSE_TIMEOUT)

    def held_emails(self) -> list[str]:
        result = self.license("list", "--json")
        if result.returncode != 0:
            raise ProofError("license list failed: " + result.stdout + result.stderr)
        accounts = json.loads(result.stdout).get("accounts", [])
        for account in accounts:
            self.say("  held: %s (%s)" % (account.get("email"), account.get("role")))
        return [a["email"] for a in accounts if a.get("email")]

    def active_account(self) -> str:
        """The account the isolated default folder is on, as Claude Code itself reports it."""
        env = dict(os.environ, CLAUDE_CONFIG_DIR=self.args.default_config_dir)
        result = subprocess.run([self.claude, "auth", "status"], env=env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=AUTH_TIMEOUT)
        if result.returncode != 0:
            raise ProofError("claude auth status failed: " + result.stdout + result.stderr)
        return json.loads(result.stdout).get("email") or "(none)"

    def switch(self, to: str, label: str) -> bool:
        try:
            result = self.license("switch", to)
        except subprocess.TimeoutExpired:
            self.say("  [%s] TIMED OUT after %ds" % (label, LICENSE_TIMEOUT))
            return False
        for line in (result.stdout + result.stderr).splitlines():
            self.say("  [%s] %s" % (label, line))
        if result.returncode != 0:
            self.say("  [%s] FAILED, exit %d" % (label, result.returncode))
            return False
        return True

    # ---- the reference readings: each account's own reset times --------------------------------

    def reference_readings(self) -> dict[str, dict]:
        """Each held account's own five-hour and weekly reset times, read from the provider with that
        account's own key through the key store. The key goes only into the Bearer header."""
        sys.path.insert(0, self.package_root)
        from cc_devthrottle.license_store import LicenseStore

        store = LicenseStore(self.args.default_config_dir, self.args.store_dir)
        readings = {}
        for account in store.held_accounts():
            request = urllib.request.Request(USAGE_URL, headers={
                "Authorization": "Bearer " + account.read_key().access_token(),
                "anthropic-beta": "oauth-2025-04-20",
            })
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    body = json.load(response)
            except urllib.error.HTTPError as exc:
                raise ProofError("reading %s's usage failed with HTTP %d; renew its key and run again"
                                 % (account.email, exc.code)) from None
            reading = {
                "five_hour_resets": epoch_of((body.get("five_hour") or {}).get("resets_at")),
                "seven_day_resets": epoch_of((body.get("seven_day") or {}).get("resets_at")),
                "seven_day_utilization": (body.get("seven_day") or {}).get("utilization"),
            }
            readings[account.email] = reading
            self.say("  reference for %s: five-hour window resets %s, weekly window resets %s (%s%% used)"
                     % (account.email, stamp(reading["five_hour_resets"]), stamp(reading["seven_day_resets"]),
                        reading["seven_day_utilization"] if reading["seven_day_utilization"] is not None else "?"))
        return readings

    def matches(self, event: dict, reading: dict) -> bool:
        """A call matches an account when its weekly reset time is that account's own, and its five-hour
        reset time is too wherever both sides give one."""
        if not same_moment(event["seven_day_resets"], reading["seven_day_resets"]):
            return False
        if event["five_hour_resets"] and reading["five_hour_resets"]:
            return same_moment(event["five_hour_resets"], reading["five_hour_resets"])
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
        reader = threading.Thread(target=lambda: [self.on_line(line) for line in process.stdout], daemon=True)
        reader.start()
        try:
            code = process.wait(timeout=TASK_TIMEOUT)
        except subprocess.TimeoutExpired:
            self.say("the task did not finish within %ds - stopping it" % TASK_TIMEOUT)
            process.kill()
            process.wait(timeout=30)
            return -1
        reader.join(timeout=10)
        return code

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
                "after_tool_result": self.tool_result_seen,
                "status": info.get("status"),
                "five_hour_resets": windows.get("five_hour", {}).get("resetsAt"),
                "seven_day_resets": windows.get("seven_day", {}).get("resetsAt"),
                "seven_day_utilization": windows.get("seven_day", {}).get("utilization"),
            }
            self.events.append(record)
            self.say("call %s the tool result: status=%s, five-hour window resets %s, weekly window resets %s (%s%% used)"
                     % ("made AFTER" if record["after_tool_result"] else "made before", record["status"],
                        stamp(record["five_hour_resets"]), stamp(record["seven_day_resets"]),
                        pct(record["seven_day_utilization"])))
        elif kind == "user":
            for part in event.get("message", {}).get("content", []):
                if isinstance(part, dict) and part.get("type") == "tool_result" and not self.tool_result_seen:
                    self.tool_result_seen = True
                    self.switch_returned_before_tool_result = self.switch_returned_at is not None
                    self.say("tool result delivered; the next request starts now (exchange had %s)"
                             % ("returned" if self.switch_returned_before_tool_result else "NOT returned"))
        elif kind == "assistant":
            for part in event.get("message", {}).get("content", []):
                if part.get("type") == "tool_use":
                    self.say("tool call started: " + json.dumps(part.get("input", {}).get("command")))
                    if not self._switch_started:
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
        self.switch_returned_at = time.time()
        self.say("exchange returned")

    # ---- the whole proof -----------------------------------------------------------------------

    def run(self) -> int:
        self.check_isolation()
        self.stage_code_under_test()
        self.claude = shutil.which("claude")
        if not self.claude:
            raise ProofError("claude is not on PATH")
        version = subprocess.run([self.claude, "--version"], capture_output=True, text=True, timeout=AUTH_TIMEOUT)
        self.say("machine: %s (%s), %s" % (socket.gethostname(), platform.system(), version.stdout.strip()))

        held = self.held_emails()
        source = self.active_account()
        missing = sorted({source, self.args.to, *held} - set(self.aliases))
        if missing:
            raise ProofError("refusing to start: %d account(s) this run touches have no --alias" % len(missing))
        if source == self.args.to:
            raise ProofError("the default folder is already on the target account - nothing to prove")
        if self.args.to not in held:
            raise ProofError("the target account is not held in the store")
        self.say("before: the isolated default folder is on %s" % source)
        readings = self.reference_readings()
        if self.matches(readings[source], readings[self.args.to]):
            raise ProofError("the two accounts have the same reset times, so a call cannot tell them apart")

        exit_code = None
        after = "(not read)"
        restored = False
        try:
            exit_code = self.run_task()
            self.say("claude exited %d" % exit_code)
            after = self.active_account()
            self.say("after: the isolated default folder is on %s" % after)
        finally:
            if self._switch_started:
                for _ in range(60):
                    if self.switch_returned_at is not None:
                        break
                    time.sleep(1)
                self.say("exchanging back: license switch " + source)
                back_ok = self.switch(source, "switch back")
                now = self.active_account()
                restored = back_ok and now == source
                self.say("switch back %s: the isolated default folder is on %s"
                         % ("VERIFIED" if restored else "FAILED", now))
            else:
                restored = self.active_account() == source
        return self.verdict(source, after, readings, restored)

    def verdict(self, source: str, after: str, readings: dict, restored: bool) -> int:
        pre = [e for e in self.events if not e["after_tool_result"]]
        post = [e for e in self.events if e["after_tool_result"]]
        target = self.args.to
        checks = [
            ("the exchange command succeeded", self.switch_ok),
            ("the exchange returned before the paused tool call ended", self.switch_returned_before_tool_result),
            ("the folder reported the target account after the task", after == target),
            ("a call made before the tool result matches the SOURCE account's own reset times",
             bool(pre) and all(self.matches(e, readings[source]) for e in pre)),
            ("the call made after the tool result matches the TARGET account's own reset times",
             bool(post) and all(self.matches(e, readings[target]) for e in post)),
            ("the folder was switched back and verified on the source account", restored),
        ]
        self.say("VERDICT")
        for name, ok in checks:
            self.say("  %s  %s" % ("PASS" if ok else "FAIL", name))
        passed = all(ok for _, ok in checks)
        self.say("PROOF %s" % ("PASSED" if passed else "FAILED"))
        return 0 if passed else 1


def init_test_root(folder: str) -> int:
    root = Path(folder)
    if not root.is_absolute():
        raise SystemExit("--init-test-root needs an absolute folder")
    resolved = root.resolve()
    home = Path.home().resolve()
    real_default = (Path.home() / ".claude").resolve()
    if resolved == home or resolved == Path(resolved.anchor) or resolved in home.parents:
        raise SystemExit("refusing to mark the home folder, a drive root or a folder above the home folder")
    if resolved == real_default or resolved in real_default.parents or real_default in resolved.parents:
        raise SystemExit("refusing to mark a folder that holds, or is inside, the real default Claude login")
    root.mkdir(parents=True, exist_ok=True)
    (root / MARKER).write_text("Marked as a live exchange proof test root on %s. Only isolated test folders belong "
                               "inside it.\n" % _dt.datetime.now(_dt.timezone.utc).isoformat(), encoding="utf-8")
    print("marked the test root")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--init-test-root", help="ONLY mark this folder as a test root, then stop")
    parser.add_argument("--test-root", help="the marked test root every folder must sit inside")
    parser.add_argument("--default-config-dir", help="the ISOLATED folder that plays the machine's default Claude config")
    parser.add_argument("--store-dir", help="the isolated key store holding the spare")
    parser.add_argument("--tool-src", help="tools/cc-devthrottle/src of the checkout under test")
    parser.add_argument("--to", help="the spare account to switch to (its email)")
    parser.add_argument("--alias", action="append", default=[],
                        help="EMAIL=LABEL, replaced in everything printed; required for every account the run touches")
    parser.add_argument("--machine-label", help='what to print instead of the machine name, e.g. "the Mac"')
    args = parser.parse_args()
    if args.init_test_root:
        return init_test_root(args.init_test_root)
    for name in ("test_root", "default_config_dir", "store_dir", "tool_src", "to", "machine_label"):
        if not getattr(args, name):
            parser.error("--%s is required for a proof run" % name.replace("_", "-"))
    proof = Proof(args)
    try:
        return proof.run()
    except (ProofError, subprocess.TimeoutExpired) as exc:
        proof.say("STOPPED: %s" % exc)
        proof.say("PROOF FAILED")
        return 1


if __name__ == "__main__":
    sys.exit(main())
