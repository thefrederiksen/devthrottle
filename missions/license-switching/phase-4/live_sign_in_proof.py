"""Live proof for phase 4: adding a spare by signing it in, refusing the wrong account, and renewing it in place.

Runs the license commands of THIS checkout (tools/cc-devthrottle/src) against an ISOLATED store under a fresh test
root - never the machine's real Claude login. The approval always happens here, in one of this machine's Director
browser profiles. The target - the machine whose store gains the spare - is this machine, or another one reached
over SSH (`--remote`), where the same source has been copied and the license commands run with `--remote-approval`.

What it shows, each step checked, the output passing only when every check passes:

1. the store starts empty;
2. a sign-in for an account the browser profile is NOT signed in as is refused (wrong-account), and nothing is kept;
3. a sign-in for the profile's own account adds it as a spare;
4. `license usage` reads the new spare with its own key and reports when it was signed in;
5. signing it in again RENEWS the same spare in place: same folder, a new sign-in time;
6. the renewed spare reads, exactly one spare is held, and the refused sign-in left no folder behind;
7. the spare is switched in, so it is the ACTIVE account of the isolated default folder, and signing it in again
   renews the ACTIVE sign-in in place: the default folder, a new sign-in time, and it reads;
8. the test root is deleted - on the Mac its Keychain items too - and checked, so no test sign-in is left behind.
   The root is one the run created itself, exclusively, inside the parent folder given, and marked with the run's
   id; cleanup deletes it only when that marker is there.

Nothing private is printed: every email given is replaced by its label, the test root by "<test root>", the home
folder, user and machine names by labels, and the authorize code is never printed at all.

Usage (the Mac's Keychain is locked over SSH, so its password comes from the environment):

    python live_sign_in_proof.py --account <email> --label "account A" --wrong-account <email> --wrong-label "account B"
        --browser <profile> --test-parent <existing folder>
    cc-secrets run desktop-login -- python live_sign_in_proof.py ... --remote <user@host> --remote-parent <folder>
        --remote-python <python on that machine> --remote-tool <tools/cc-devthrottle copied there>
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import platform
import re
import shlex
import subprocess
import sys
import threading
import uuid
from pathlib import Path

HERE = Path(__file__).resolve()
TOOL = HERE.parents[3] / "tools" / "cc-devthrottle"


class Proof:
    def __init__(self, args):
        self.args = args
        self.failed = False
        # A spare's folder is named after its email (license_store.spare_slug), so the folder names go first.
        self.redactions = [(_slug(args.account), f"<{args.label} folder>"),
                           (_slug(args.wrong_account), f"<{args.wrong_label} folder>"),
                           (args.account, f"<{args.label}>"), (args.wrong_account, f"<{args.wrong_label}>")]
        if args.remote:
            self.redactions += [(args.remote, "<the target machine>"), (args.remote_root, "<test root>"),
                                (args.remote_parent, "<test parent>"),
                                (args.remote_tool, "<tool copy>")]
        self.redactions += [(f'"{args.browser}"', '"<browser profile>"'), (f" {args.browser} ", " <browser profile> "),
                            (args.test_root, "<test root>"), (args.test_parent, "<test parent>"),
                            (str(Path.home()), "<home>"),
                            (platform.node(), "<this machine>"), (getpass.getuser(), "<user>")]

    def redact(self, text: str) -> str:
        # An authorize URL names the account in its login hint, and is no evidence of anything by itself.
        text = re.sub(r"https://\S+/oauth/authorize\?\S+", "<authorize URL>", text)
        for value, label in self.redactions:
            if value:
                text = re.sub(re.escape(value), label, text, flags=re.I)
                text = re.sub(re.escape(value.replace("\\", "\\\\")), label, text, flags=re.I)
        return text

    def say(self, text: str = "") -> None:
        print(self.redact(text), flush=True)

    def check(self, name: str, ok: bool, detail: str = "") -> None:
        self.say(f"CHECK {name}: {'PASS' if ok else 'FAIL'}{(' - ' + detail) if detail else ''}")
        self.failed |= not ok

    # ----- the target's store -----

    def _store_args(self) -> list[str]:
        root = self.args.remote_root if self.args.remote else self.args.test_root
        sep = "/" if self.args.remote else "\\"
        return ["--default-config-dir", f"{root}{sep}default", "--store-dir", f"{root}{sep}store"]

    def _remote_command(self, words: list[str]) -> list[str]:
        a = self.args
        line = ('read -r P; security unlock-keychain -p "$P" ~/Library/Keychains/login.keychain-db || exit 9; '
                f'export PATH=$HOME/.local/bin:$PATH; cd {shlex.quote(a.remote_tool)}; '
                f'PYTHONPATH=.. {shlex.quote(a.remote_python)} -m src.cli license '
                + " ".join(shlex.quote(w) for w in words))
        return ["ssh", "-o", "BatchMode=yes", a.remote, line]

    def target(self, *words: str, stdin_lines: tuple[str, ...] = ()) -> subprocess.CompletedProcess:
        words = list(words) + self._store_args()
        if self.args.remote:
            lines = (os.environ["DESKTOP_LOGIN_PASSWORD"],) + stdin_lines
            return subprocess.run(self._remote_command(words), input="\n".join(lines) + "\n", capture_output=True,
                                  text=True, encoding="utf-8", errors="replace", timeout=300)
        return local(["license", *words], "\n".join(stdin_lines) + "\n" if stdin_lines else None)

    def target_json(self, *words: str):
        done = self.target(*words, "--json")
        self.say(f"$ license {' '.join(words)} --json  (exit {done.returncode})")
        self.say(done.stdout.rstrip())
        return done, _first_json(done.stdout)

    # ----- signing in -----

    def sign_in(self, email: str):
        """Sign `email` in on the target, approved here in the browser profile. Returns (exit code, JSON, text)."""
        a = self.args
        if not a.remote:
            done = local(["license", "sign-in", email, "--browser", a.browser, "--json", *self._store_args()])
            return done.returncode, _first_json(done.stdout), done.stdout + done.stderr
        # Two machines: the target prints the URL and waits; the code is approved here and sent back.
        words = ["sign-in", email, "--remote-approval", "--json"] + self._store_args()
        p = subprocess.Popen(self._remote_command(words), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", bufsize=1)
        p.stdin.write(os.environ["DESKTOP_LOGIN_PASSWORD"] + "\n")
        p.stdin.flush()
        err: list[str] = []
        threading.Thread(target=lambda: err.extend(p.stderr), daemon=True).start()
        out, url = [], None
        for line in p.stdout:
            out.append(line)
            if line.startswith("authorize-url: "):
                url = line[len("authorize-url: "):].strip()
                break
        if url is None:
            p.wait(timeout=60)
            return p.returncode, None, "".join(out) + "".join(err)
        self.say("target printed an authorize URL; approving it here")
        approved = local(["license", "approve", url, "--email", email, "--browser", a.browser])
        if approved.returncode == 0:
            self.say("approve: printed a code (not shown); sending it to the target")
            p.stdin.write(approved.stdout.strip() + "\n")
        else:
            self.say(f"approve refused here (exit {approved.returncode}): {approved.stdout.strip()} "
                     f"{approved.stderr.strip()}")
            p.stdin.write("\n")  # no code: the target gives up and deletes its sign-in folder
        p.stdin.flush()
        p.stdin.close()
        rest = p.stdout.read()
        p.wait(timeout=180)
        return p.returncode, _first_json(rest), "".join(out) + rest + "".join(err)


MARKER = ".license-proof-root"


def make_root(args) -> None:
    """Create THIS run's test root, exclusively, inside the parent folder given, and mark it with the run's id. The
    proof only ever works in - and only ever deletes - a root it created itself in this run."""
    name = f"license-proof-{args.run_id}"
    if not args.remote:
        root = Path(args.test_parent) / name
        root.mkdir()  # fails if it exists: never adopt a folder this run did not make
        (root / MARKER).write_text(args.run_id, encoding="utf-8")
        (root / "default").mkdir()
        args.test_root = str(root)
        return
    root = f"{args.remote_parent.rstrip('/')}/{name}"
    done = subprocess.run(["ssh", "-o", "BatchMode=yes", args.remote,
                           f"set -e; mkdir {shlex.quote(root)}; printf %s {shlex.quote(args.run_id)} > "
                           f"{shlex.quote(root + '/' + MARKER)}; mkdir {shlex.quote(root + '/default')}"],
                          capture_output=True, text=True)
    if done.returncode != 0:
        raise SystemExit(f"could not create the test root on the target (exit {done.returncode})")
    args.remote_root = root


def clean(args, default_dir: str | None, store_dir: str | None) -> bool:
    """Delete this run's test root, and only when it carries this run's marker. The key store refuses to empty the
    active folder - by design - so the proof deletes its own root directly; on the Mac the Keychain item of every
    folder it used goes first."""
    if not args.remote:
        import shutil

        root = Path(args.test_root)
        if (root / MARKER).read_text(encoding="utf-8") != args.run_id:
            return False
        shutil.rmtree(root)
        return not root.exists()
    service = 'Claude Code-credentials-$(printf %s "$F" | shasum -a 256 | cut -c1-8)'
    folders = [default_dir or f"{args.remote_root}/default"]
    marker = shlex.quote(args.remote_root + "/" + MARKER)
    script = ('read -r P; security unlock-keychain -p "$P" ~/Library/Keychains/login.keychain-db || exit 9; left=0; '
              f'[ "$(cat {marker})" = {shlex.quote(args.run_id)} ] || exit 7; '
              + "".join(f'F={shlex.quote(f)}; while security delete-generic-password -s "{service}" >/dev/null 2>&1; '
                        f'do :; done; security find-generic-password -s "{service}" >/dev/null 2>&1 && left=1; '
                        for f in folders)
              # find, not a glob: the target's shell may be zsh, which aborts on a glob that matches nothing
              + f'for F in $(find {shlex.quote((store_dir or args.remote_root + "/store") + "/spares")} -mindepth 1 '
              f'-maxdepth 1 -type d 2>/dev/null); do '
              f'security find-generic-password -s "{service}" >/dev/null 2>&1 && left=1; done; '
              f'rm -rf {shlex.quote(args.remote_root)}; [ -e {shlex.quote(args.remote_root)} ] && left=1; '
              'echo "left=$left"')
    done = subprocess.run(["ssh", "-o", "BatchMode=yes", args.remote, script],
                          input=os.environ["DESKTOP_LOGIN_PASSWORD"] + "\n", capture_output=True, text=True)
    return done.returncode == 0 and done.stdout.strip().endswith("left=0")


def _slug(email: str) -> str:
    """The spare folder name for an email, as license_store.spare_slug makes it."""
    return re.sub(r"[^a-z0-9._-]+", "-", email.strip().lower()).strip("-.")


def local(words: list[str], stdin: str | None = None) -> subprocess.CompletedProcess:
    env = dict(os.environ, PYTHONPATH=str(TOOL.parent))
    env.pop("DESKTOP_LOGIN_PASSWORD", None)
    return subprocess.run([sys.executable, "-m", "src.cli", *words], cwd=TOOL, env=env, input=stdin,
                          capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300)


def _first_json(text: str):
    start = text.find("{")
    if start < 0:
        return None
    try:
        value, _ = json.JSONDecoder().raw_decode(text[start:])
    except ValueError:
        return None
    return value


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--account", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--wrong-account", required=True)
    ap.add_argument("--wrong-label", required=True)
    ap.add_argument("--browser", required=True)
    ap.add_argument("--test-parent", help="an existing folder; this run creates its own root inside it")
    ap.add_argument("--remote")
    ap.add_argument("--remote-parent", help="the same, on the target machine")
    ap.add_argument("--remote-python")
    ap.add_argument("--remote-tool")
    args = ap.parse_args()
    if args.remote and not all((args.remote_parent, args.remote_python, args.remote_tool)):
        ap.error("--remote needs --remote-parent, --remote-python and --remote-tool")
    if not args.remote and not args.test_parent:
        ap.error("--test-parent is required")
    args.run_id = uuid.uuid4().hex[:12]
    args.test_root = args.remote_root = None
    make_root(args)
    proof = Proof(args)
    account, wrong = args.account.lower(), args.wrong_account.lower()
    where = "the target machine over SSH (approval here)" if args.remote else "this machine (approval here)"
    proof.say(f"Phase 4 live sign-in proof. Target: {where}. Isolated store under <test root>.")
    proof.say(f"Browser profile used for approval: \"{args.browser}\", signed in to claude.ai as <{args.label}>.")

    proof.say("\n--- 1. the store starts empty")
    _, listed = proof.target_json("list")
    proof.check("store-empty", listed is not None and listed["count"] == 0)

    proof.say(f"\n--- 2. a sign-in for <{args.wrong_label}>, which the profile is NOT signed in as, is refused")
    rc, answer, text = proof.sign_in(wrong)
    proof.say(f"sign-in exit {rc}: {text.strip()}")
    proof.check("wrong-account-refused", rc != 0 and ("wrong-account" in text or "approval-failed" in text))
    _, listed = proof.target_json("list")
    proof.check("nothing-kept", listed is not None and listed["count"] == 0)

    proof.say(f"\n--- 3. a sign-in for <{args.label}> adds it as a spare")
    rc, first, text = proof.sign_in(account)
    proof.say(f"sign-in exit {rc}: {json.dumps(first, indent=2) if first else text.strip()}")
    proof.check("added", rc == 0 and bool(first) and first.get("result") == "added"
                and first.get("accountEmail") == account)

    proof.say("\n--- 4. the new spare reads with its own key and says when it was signed in")
    _, usage = proof.target_json("usage")
    row = next((r for r in (usage or {}).get("accounts", []) if r["accountEmail"] == account), None)
    proof.check("reads", bool(row) and row["readError"] is None and row["sevenDay"] is not None)
    proof.check("signed-in-time-reported", bool(row) and bool(first)
                and row["signedInAtUtc"] == first.get("signedInAtUtc") and row["signInExpiresAtUtc"] is not None)

    proof.say(f"\n--- 5. signing <{args.label}> in again renews the same spare in place")
    rc, second, text = proof.sign_in(account)
    proof.say(f"sign-in exit {rc}: {json.dumps(second, indent=2) if second else text.strip()}")
    proof.check("renewed-in-place", rc == 0 and bool(second) and bool(first) and second.get("result") == "renewed"
                and second.get("configDir") == first.get("configDir"))
    proof.check("new-sign-in-time", bool(second) and bool(first)
                and second.get("signedInAtUtc", "") >= first.get("signedInAtUtc", "~"))

    proof.say("\n--- 6. the renewed spare reads; one spare is held; no other spare folder was left")
    _, usage = proof.target_json("usage")
    row = next((r for r in (usage or {}).get("accounts", []) if r["accountEmail"] == account), None)
    proof.check("renewed-reads", bool(row) and row["readError"] is None
                and row["signedInAtUtc"] == (second or {}).get("signedInAtUtc"))
    _, listed = proof.target_json("list")
    spares = [a for a in (listed or {}).get("accounts", []) if a["role"] == "spare"]
    proof.check("one-spare", len(spares) == 1 and spares[0]["email"] == account)
    # The refused sign-in for account B must have left no folder of its own behind.
    if args.remote:
        done = subprocess.run(["ssh", "-o", "BatchMode=yes", args.remote,
                               f"ls -A {shlex.quote(args.remote_root + '/store/spares')} | wc -l"],
                              capture_output=True, text=True)
        count = done.stdout.strip() if done.returncode == 0 else "?"
    else:
        count = str(len(list((Path(args.test_root) / "store" / "spares").iterdir())))
    proof.check("one-spare-folder", count == "1", f"{count} folder(s) under spares")

    proof.say(f"\n--- 7. switch <{args.label}> in, then renew the ACTIVE sign-in in place")
    done, switched = proof.target_json("switch", account)
    proof.check("now-active", bool(switched) and switched.get("activeAccountEmailAfter") == account)
    rc, third, text = proof.sign_in(account)
    proof.say(f"sign-in exit {rc}: {json.dumps(third, indent=2) if third else text.strip()}")
    default_dir = (listed or {}).get("defaultConfigDir")
    proof.check("active-renewed-in-place", rc == 0 and bool(third) and third.get("result") == "renewed"
                and third.get("role") == "active" and third.get("configDir") == default_dir)
    _, usage = proof.target_json("usage", "--active")
    row = next((r for r in (usage or {}).get("accounts", []) if r["accountEmail"] == account), None)
    proof.check("active-renewed-reads", bool(row) and row["isActive"] and row["readError"] is None
                and row["signedInAtUtc"] == (third or {}).get("signedInAtUtc"))
    _, listed = proof.target_json("list")
    proof.check("one-account-active", bool(listed) and [(a["email"], a["role"]) for a in listed["accounts"]]
                == [(account, "active")])

    proof.say("\n--- 8. delete the test root and check no test sign-in is left")
    proof.check("cleaned", clean(args, default_dir, (listed or {}).get("storeDir")))

    proof.say("\nPROOF " + ("FAILED" if proof.failed else "PASSED"))
    return 1 if proof.failed else 0


if __name__ == "__main__":
    sys.exit(main())
