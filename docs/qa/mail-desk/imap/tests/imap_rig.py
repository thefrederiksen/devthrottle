"""The shared rig for the IMAP reader's tests (docs/qa/mail-desk/imap/CONTRACT.md).

It builds on phase 2's rig (docs/qa/mail-desk/phase-2/tests/conftest.py), loaded under its own module name as the Gmail
rig does: a fresh screen.py per test, every CC_ variable removed, scratch homes, a fixed clock and machine name.
Beside them, a fake dt-email and a fake cc-secrets stand in for the two programs at the process boundary: shutil.which
and subprocess.run in screen.py are replaced, so no process starts. The fake records EVERY command it was asked to
run, so "nothing was filed" is that log holding no archive or move while it holds the reads.

The fake answers in dt-email's own shapes (tools/dt-email/src/cli.py: account list --json, list --json with
uidvalidity, folders --json, archive and move, and "Error: no message with UID ..." for a UID the folder does not hold)
and in cc-secrets run --json's (tools/cc-secrets/src/cli.py: exitCode, timedOut, stdout, stderr, or outcome refused).

No real address, password or mail text: example.com, example.org and example.net only. No network, no port.

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/imap/tests -q
"""

import importlib.util
import json
import subprocess
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

sys.dont_write_bytecode = True  # a __pycache__ beside the check would end up in the published template

HERE = Path(__file__).resolve().parent
PHASE2 = HERE.parents[1] / "phase-2" / "tests" / "conftest.py"

_spec = importlib.util.spec_from_file_location("mail_desk_phase2_rig_for_imap", PHASE2)
phase2 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(phase2)

NOW = phase2.NOW            # 2026-10-18T12:00:00Z
MACHINE = phase2.MACHINE
channel = phase2.channel
gateway_reason = phase2.gateway_reason

BOX = "desk@example.com"
OTHER = "second.desk@example.org"
MISSING = "Error: no message with UID {uid} in folder 'INBOX'. Nothing was changed. List the folder with: dt-email list --folder \"INBOX\""
RENUMBERED = ("Error: the UIDVALIDITY of folder 'INBOX' is {now} now, not {expected}, so its UIDs name other messages. "
              "Nothing was moved. List the folder again.")


def mail(received, sender="Some One <someone@example.com>", subject="Hello", text="A short note.", attachments=0,
         to=None, cc=None):
    """One stored message: its arrival time (UTC, to the second) and what dt-email would show of it."""
    return {"received": received, "from": sender, "subject": subject, "text": text, "attachments": attachments,
            "to": to, "cc": cc}


class Mailbox:
    """One IMAP account's folders. UIDs count up per folder from 1; `validity` is the Inbox's UIDVALIDITY."""

    def __init__(self, junk="Spam", sent="Sent Items", zone_hours=0):
        self.validity = 1
        self.junk, self.sent = junk, sent
        self.zone_hours = zone_hours   # the server's own zone, which IMAP's SINCE compares dates in
        self.folders = {"INBOX": {}, sent: {}, "Archive": {}, junk: {}}
        self.next_uid = {name: 1 for name in self.folders}

    def add(self, folder, message):
        uid = self.next_uid[folder]
        self.next_uid[folder] += 1
        self.folders[folder][uid] = message
        return uid

    def inbox(self, *messages):
        return [self.add("INBOX", m) for m in messages]

    def server_date(self, received):
        """The date of an arrival time in the server's own zone: what SINCE compares (RFC 3501)."""
        moment = datetime.strptime(received, "%Y-%m-%dT%H:%M:%SZ") + timedelta(hours=self.zone_hours)
        return moment.strftime("%Y-%m-%d")

    def renumber(self):
        """What a server does when it rebuilds a folder: a new UIDVALIDITY, and every message a new UID."""
        self.validity += 1
        old = self.folders["INBOX"]
        self.folders["INBOX"], self.next_uid["INBOX"] = {}, 1
        for uid in sorted(old):
            self.add("INBOX", old[uid])


class Done:
    def __init__(self, code, out, err=""):
        self.returncode, self.stdout, self.stderr = code, out.encode("utf-8"), err.encode("utf-8")


class FakeTools:
    """dt-email and cc-secrets, as far as the IMAP reader uses them."""

    def __init__(self, base):
        self.base = base         # the Director's folder (phase 2's World.base); dt-email's settings sit under it
        self.accounts = {}       # dt-email account name -> {"email", "secret_entry", "password_env", "root"}
        self.boxes = {}          # dt-email account name -> Mailbox
        self.secrets = set()     # cc-secrets entries on this machine
        self.installed = {"dt-email", "cc-secrets"}
        self.log = []            # every dt-email command, as a list of its arguments (dt-email itself left out)
        self.wrapped = []        # every cc-secrets command line, whole
        self.fail = {}           # account name -> an "Error: ..." line, "not-json", "secrets-broken" or "timeout"
        self.fail_only = None    # a dt-email command name: the failure is only for that command
        self.renumber_before_filing = False   # the server renumbers the Inbox after the run read it, before it files

    def account(self, address=BOX, name="desk", entry=None, variable="DESK_PASSWORD", instance="default", **box):
        """A dt-email account saved the way dt-email account add saves it: under the Director's own folder
        (instance None), or under an instance folder, where a Director session's CC_DIRECTOR_ROOT points."""
        entry = entry or f"{name}-password"
        # "cc-storage": where dt-email saves on Linux when run from a plain terminal (cc-storage's user folder,
        # ~/.cc-director), which is not the Director's folder there.
        root = (self.base.parents[2] / ".cc-director" if instance == "cc-storage"
                else self.base if instance is None else self.base / "instances" / instance)
        settings = root / "config" / "dt-email" / "config.json"
        settings.parent.mkdir(parents=True, exist_ok=True)
        settings.write_text("{}", encoding="utf-8")   # dt-email's own file; only the fake reads the accounts
        self.accounts[name] = {"email": address, "secret_entry": entry, "password_env": variable, "root": str(root)}
        self.secrets.add(entry)
        self.boxes[name] = Mailbox(**box)
        return self.boxes[name]

    # ------------------------------------------------------------------------------------------------ the boundary

    def which(self, name):
        return f"/fake/bin/{name}" if name in self.installed else None

    def run(self, argv, capture_output=False, stdin=None, timeout=None, env=None):
        assert capture_output and stdin == subprocess.DEVNULL and timeout, "every process is bounded and has no input"
        assert env is not None and env.get("CC_DIRECTOR_ROOT"), "dt-email is always told which settings to read"
        root = env["CC_DIRECTOR_ROOT"]
        program = Path(argv[0]).name
        if program == "cc-secrets":
            return self.secrets_run(argv, root)
        assert program == "dt-email", argv
        assert argv[1:] == ["account", "list", "--json"], f"dt-email ran without cc-secrets: {argv}"
        self.log.append(argv[1:])
        return Done(0, json.dumps({"accounts": [
            {"name": n, "email": a["email"], "imap_host": "imap.example.net", "smtp_host": "smtp.example.net",
             "secret_entry": a["secret_entry"], "password_env": a["password_env"], "save_sent": True, "default": False}
            for n, a in self.accounts.items() if a["root"] == root], "allowed_recipients": None}))

    def secrets_run(self, argv, root):
        self.wrapped.append(argv)
        split = argv.index("--")
        options, inner = argv[1:split], argv[split + 1:]
        assert options[0] == "run" and inner[0] == "dt-email", argv
        entry = options[1]
        assert options[2:] == ["--via", "env", "--env-name", options[5], "--timeout", options[7], "--json"], argv
        args = inner[1:]
        self.log.append(args)
        if entry not in self.secrets:
            return Done(2, json.dumps({"entry": entry, "outcome": "refused",
                                       "reason": f"No secret named '{entry}' is on this machine."}))
        name = args[args.index("--account") + 1]
        account = self.accounts[name]
        assert entry == account["secret_entry"] and options[5] == account["password_env"], argv
        assert root == account["root"], "dt-email was pointed at settings that do not hold this account"
        failure = self.fail.get(name)
        if self.fail_only is not None and args[0] != self.fail_only:
            failure = None
        if failure == "secrets-broken":
            return Done(1, "", "error: the cc-secrets store is locked")
        if failure == "timeout":
            return Done(1, json.dumps({"entry": entry, "entries": [entry], "exitCode": -9, "timedOut": True,
                                       "stdout": "", "stderr": ""}))
        code, out, err = self.dt_email(args, self.boxes[name])
        if failure == "not-json":
            code, out = 0, "<html>a captive portal</html>"
        elif isinstance(failure, str) and failure.startswith("Error:"):
            code, out, err = 1, "", failure + "\n"
        return Done(code, json.dumps({"entry": entry, "entries": [entry], "exitCode": code, "timedOut": False,
                                      "stdout": out, "stderr": err}))

    # ------------------------------------------------------------------------------------------------ dt-email

    def dt_email(self, args, box):
        assert args[-1] == "--json" and args[-3] == "--account", args
        command, rest = args[0], args[1:-3]
        if command == "folders":
            special = {"INBOX": None, box.sent: "sent", "Archive": "archive", box.junk: "junk"}
            folders = [{"name": n, "special": special[n], "messages": len(box.folders[n]), "unseen": 0}
                       for n in box.folders]
            return 0, json.dumps({"count": len(folders), "folders": folders}), ""
        if command == "list":
            return self.listing(rest, box)
        if command in ("archive", "move"):
            uid = int(rest[0])
            expected = int(rest[rest.index("--expect-uidvalidity") + 1]) if "--expect-uidvalidity" in rest else None
            if self.renumber_before_filing:
                box.renumber()
                self.renumber_before_filing = False
            if uid not in box.folders["INBOX"]:
                return 1, "", MISSING.format(uid=uid) + "\n"
            if expected is not None and expected != box.validity:
                return 1, "", RENUMBERED.format(now=box.validity, expected=expected) + "\n"
            to = "Archive" if command == "archive" else rest[2]
            box.add(to, box.folders["INBOX"].pop(uid))
            return 0, json.dumps({"moved": str(uid), "from": "INBOX", "to": to}), ""
        raise AssertionError(f"the fake dt-email does not know {args}")

    def listing(self, rest, box):
        options, i = {}, 0
        while i < len(rest):
            if rest[i] in ("--oldest", "--preview"):
                options[rest[i]] = True
                i += 1
            else:
                options[rest[i]] = rest[i + 1]
                i += 2
        folder = options.get("--folder", "INBOX")
        held = box.folders[folder]
        uids = sorted(held)
        if "--after-uid" in options:
            uids = [u for u in uids if u > int(options["--after-uid"])]
        if "--since" in options:
            uids = [u for u in uids if box.server_date(held[u]["received"]) >= options["--since"]]
        count = int(options.get("--count", 20))
        chosen = uids[:count] if "--oldest" in options else sorted(uids, reverse=True)[:count]
        records = []
        for uid in sorted(chosen, reverse="--oldest" not in options):
            m = held[uid]
            record = {"uid": uid, "date": m["received"], "received": m["received"], "from": m["from"],
                      "to": m["to"], "cc": m["cc"], "subject": m["subject"], "message_id": f"<{uid}@example.net>",
                      "flags": "", "unread": True, "flagged": False, "size": 100}
            if "--preview" in options:
                record.update({"preview": m["text"][:200], "attachments": m["attachments"]})
            records.append(record)
        return 0, json.dumps({"count": len(records), "total": len(uids), "folder": folder,
                              "uidvalidity": box.validity if folder == "INBOX" else 7, "messages": records}), ""

    # ------------------------------------------------------------------------------------------------ reading the log

    def filed(self):
        return [a for a in self.log if a[0] in ("archive", "move")]

    def commands(self):
        return [a[0] if a[0] != "account" else "account list" for a in self.log]


class ImapWorld(phase2.World):
    def __init__(self, world, tools):
        super().__init__(world.screen, world.graph, world.sign_in, world.home, world.state, world.capsys)
        self.tools = tools

    def key(self, address, validity, uid):
        return self.screen.short_key(address, f"{validity}.{uid}")


world = phase2.world   # phase 2's fixture: fresh screen.py, scratch homes, the fake Graph and MSAL


@pytest.fixture
def iw(world, monkeypatch):
    """Phase 2's world, with the fake dt-email and cc-secrets in place of the programs."""
    tools = FakeTools(world.base)
    monkeypatch.setattr(world.screen.shutil, "which", tools.which)
    monkeypatch.setattr(world.screen.subprocess, "run", tools.run)
    return ImapWorld(world, tools)


def imap(address=BOX, mode="observe", **fields):
    return channel(address, mode=mode, provider="imap", **fields)


def cursor(last_uid, newest="2026-10-18T09:00:00Z", validity=1):
    return {"provider": "imap", "uidValidity": validity, "lastUid": last_uid, "newest": newest}


def section(context, address=BOX):
    return next(s for s in context["channels"] if s["address"] == address)
