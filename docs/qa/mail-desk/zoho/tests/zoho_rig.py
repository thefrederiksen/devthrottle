"""The shared rig for the Zoho reader's tests (docs/qa/mail-desk/zoho/CONTRACT.md).

It builds on phase 2's rig (docs/qa/mail-desk/phase-2/tests/conftest.py), loaded under its own module name as the IMAP
rig does: a fresh screen.py per test, every CC_ variable removed, scratch homes, a fixed clock and machine name.
Beside them, a fake dt-zoho and a fake cc-secrets stand in for the two programs at the process boundary: shutil.which
and subprocess.run in screen.py are replaced, so no process starts. The fake records EVERY command it was asked to
run, so "nothing was filed" is that log holding no move while it holds the reads.

The fake answers in dt-zoho's own shapes (tools/dt-zoho/src/cli.py and cli_write.py: accounts --json, folders --json,
list --json newest first with each arrival time in local time with its offset, and move --from-folder's refusal
"Error: message <id> is not in /Inbox (...). Nothing was moved.") and in cc-secrets run --json's
(tools/cc-secrets/src/cli.py: exitCode, timedOut, stdout, stderr, or outcome refused). Like Zoho, its spam moves the
whole conversation and its move only the message named (dt-zoho's README, "Spam acts on the whole conversation").

No real address, key or mail text: example.com, example.org and example.net only. No network, no port.

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/zoho/tests -q
"""

import importlib.util
import json
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

sys.dont_write_bytecode = True  # a __pycache__ beside the check would end up in the published template

HERE = Path(__file__).resolve().parent
PHASE2 = HERE.parents[1] / "phase-2" / "tests" / "conftest.py"

_spec = importlib.util.spec_from_file_location("mail_desk_phase2_rig_for_zoho", PHASE2)
phase2 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(phase2)

NOW = phase2.NOW            # 2026-10-18T12:00:00Z
MACHINE = phase2.MACHINE
channel = phase2.channel

BOX = "desk@example.com"
OTHER = "second.desk@example.org"
ENTRIES = ["zoho-api-client-secret", "zoho-api-client-id", "zoho-api-refresh-token"]
# What cc-secrets would put in each variable. The check must never see them, so no test output may hold one.
KEY_VALUES = {"zoho-api-client-secret": "fake-client-secret-5f3a", "zoho-api-client-id": "1000.FAKECLIENTID",
              "zoho-api-refresh-token": "1000.fake-refresh-token-9c1e"}
INBOX, SENT, ARCHIVE, SPAM, TRASH = "9000000000002008", "9000000000002012", "9000000000003011", "9000000000002013", "9000000000002014"
NEWSLETTER = "9000000000003006"   # a folder the owner made: Zoho gives it the type Inbox too
ZONE = timezone(timedelta(hours=-5))   # the account's time zone; dt-zoho prints arrival times in local time


def mail(received, sender="Some One <someone@example.com>", subject="Hello", summary="A short note.",
         attachments=False, to="", cc="", thread=None):
    """One stored message: its arrival time (UTC, to the second) and what dt-zoho shows of it."""
    return {"received": received, "from": sender, "subject": subject, "summary": summary, "attachments": attachments,
            "to": to, "cc": cc, "thread": thread}


class Mailbox:
    """One Zoho account's messages: id -> {"folder", ...}. Ids count up from a large number, as Zoho's do."""

    FOLDERS = [(INBOX, "Inbox", "/Inbox", "Inbox"), (NEWSLETTER, "Newsletter", "/Newsletter", "Inbox"),
               (SENT, "Sent", "/Sent", "Sent"), (ARCHIVE, "Archive", "/Archive", "Archive"),
               (SPAM, "Spam", "/Spam", "Spam"), (TRASH, "Trash", "/Trash", "Trash")]

    def __init__(self, address, without=()):
        self.address = address
        self.folders = [f for f in self.FOLDERS if f[3] not in without or f[2] != f"/{f[3]}"]
        self.messages = {}
        self.next_id = 1794167752063002500

    def add(self, folder, message):
        mid = str(self.next_id)
        self.next_id += 100
        self.messages[mid] = dict(message, folder=folder)
        return mid

    def inbox(self, *messages):
        return [self.add(INBOX, m) for m in messages]

    def held(self, folder):
        return sorted(mid for mid, m in self.messages.items() if m["folder"] == folder)

    def path(self, folder_id):
        return next(f[2] for f in self.FOLDERS if f[0] == folder_id)


class Done:
    def __init__(self, code, out, err=""):
        self.returncode, self.stdout, self.stderr = code, out.encode("utf-8"), err.encode("utf-8")


class FakeTools:
    """dt-zoho and cc-secrets, as far as the Zoho reader uses them."""

    def __init__(self, base):
        self.base = base         # the Director's folder (phase 2's World.base)
        self.boxes = {}          # address -> Mailbox: every mailbox the one Zoho key reaches
        self.roots = []          # the cc-director folders holding dt-zoho settings
        self.secrets = set(ENTRIES)
        self.installed = {"dt-zoho", "cc-secrets"}
        self.log = []            # every dt-zoho command, as a list of its arguments (dt-zoho itself left out)
        self.wrapped = []        # every cc-secrets command line, whole
        self.envs = []           # the environment each process was started with
        self.fail = None         # an "Error: ..." line, "not-json", "secrets-broken" or "timeout"
        self.fail_only = None    # a dt-zoho command name: the failure is only for that command
        self.fail_for = None     # an address: the failure is only for commands about that mailbox

    def settings(self, instance="default"):
        """dt-zoho settings saved the way dt-zoho settings set saves them: under the Director's own folder (None),
        under an instance folder, where a Director session's CC_DIRECTOR_ROOT points, or cc-storage's user folder."""
        root = (self.base.parents[2] / ".cc-director" if instance == "cc-storage"
                else self.base if instance is None else self.base / "instances" / instance)
        path = root / "config" / "dt-zoho" / "config.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"data_centre": "ca"}), encoding="utf-8")
        self.roots.append(str(root))
        return root

    def account(self, address=BOX, instance="default", **box):
        if not self.roots:
            self.settings(instance)
        self.boxes[address] = Mailbox(address, **box)
        return self.boxes[address]

    # ------------------------------------------------------------------------------------------------ the boundary

    def which(self, name):
        return f"/fake/bin/{name}" if name in self.installed else None

    def run(self, argv, capture_output=False, stdin=None, timeout=None, env=None):
        assert capture_output and stdin == subprocess.DEVNULL and timeout, "every process is bounded and has no input"
        assert env is not None and env.get("CC_DIRECTOR_ROOT"), "dt-zoho is always told which settings to read"
        self.envs.append(env)
        program = Path(argv[0]).name
        assert program == "cc-secrets", f"dt-zoho ran without cc-secrets: {argv}"
        self.wrapped.append(argv)
        split = argv.index("--")
        options, inner = argv[1:split], argv[split + 1:]
        assert options == ["run", ENTRIES[0], "--with", ENTRIES[1], "--with", ENTRIES[2], "--timeout", options[7],
                           "--json"], argv
        assert inner[0] == "dt-zoho", argv
        args = inner[1:]
        self.log.append(args)
        missing = [e for e in ENTRIES if e not in self.secrets]
        if missing:
            return Done(2, json.dumps({"entry": missing[0], "outcome": "refused",
                                       "reason": f"No secret named '{missing[0]}' is on this machine."}))
        assert env["CC_DIRECTOR_ROOT"] in self.roots, "dt-zoho was pointed at a folder that holds no dt-zoho settings"
        failure = self.fail
        if self.fail_only is not None and args[0] != self.fail_only:
            failure = None
        if self.fail_for is not None and ("--account" not in args or args[args.index("--account") + 1] != self.fail_for):
            failure = None
        if failure == "secrets-broken":
            return Done(1, "", "error: the cc-secrets store is locked by another process; close it and try again")
        if failure == "timeout":
            return Done(1, json.dumps({"entry": ENTRIES[0], "entries": ENTRIES, "exitCode": -9, "timedOut": True,
                                       "stdout": "", "stderr": ""}))
        code, out, err = self.dt_zoho(args)
        if failure == "not-json":
            code, out = 0, "<html>a captive portal</html>"
        elif isinstance(failure, str) and failure.startswith("Error:"):
            code, out, err = 1, "", failure + "\n"
        return Done(code, json.dumps({"entry": ENTRIES[0], "entries": ENTRIES, "exitCode": code, "timedOut": False,
                                      "stdout": out, "stderr": err}))

    # ------------------------------------------------------------------------------------------------ dt-zoho

    def dt_zoho(self, args):
        assert args[-1] == "--json", args
        if args == ["accounts", "--json"]:
            records = [{"account_id": str(n), "email": a, "name": "Desk", "default": n == 0, "zuid": None,
                        "imap_enabled": False, "time_zone": "America/Toronto", "addresses": [a, f"alias.{a}"],
                        "aliases": [f"alias.{a}"], "zoid": None} for n, a in enumerate(self.boxes)]
            return 0, json.dumps({"count": len(records), "accounts": records}), ""
        assert args[-3] == "--account", args
        box = self.boxes[args[-2]]
        command, rest = args[0], args[1:-3]
        if command == "folders":
            records = [{"id": i, "name": n, "path": p, "type": t, "imap_view": True, "parent_id": None}
                       for i, n, p, t in box.folders]
            return 0, json.dumps({"count": len(records), "account": box.address, "folders": records}), ""
        if command == "list":
            options = dict(zip(rest[0::2], rest[1::2]))
            folder, start, count = options["--folder"], int(options.get("--start", 1)), int(options.get("--count", 20))
            held = sorted((m for m in box.messages.items() if m[1]["folder"] == folder),
                          key=lambda kv: (kv[1]["received"], kv[0]), reverse=True)
            page = held[start - 1:start - 1 + count]
            records = [self.record(mid, m) for mid, m in page]
            return 0, json.dumps({"count": len(records), "fetched": len(records), "start": start,
                                  "next_start": start + len(records) if len(records) == count else None,
                                  "account": box.address, "folder": box.path(folder), "folder_id": folder,
                                  "label_id": None, "thread_id": None, "messages": records}), ""
        if command == "move":
            mid, to = rest[0], rest[rest.index("--to") + 1]
            source = rest[rest.index("--from-folder") + 1] if "--from-folder" in rest else None
            if source is not None:
                if mid not in box.messages:
                    return 1, "", f"Error: message {mid} is not in {box.path(source)} (Zoho does not have it). Nothing was moved.\n"
                if box.messages[mid]["folder"] != source:
                    return 1, "", (f"Error: message {mid} is not in {box.path(source)} (Zoho places it in "
                                   f"{box.path(box.messages[mid]['folder'])}). Nothing was moved.\n")
            box.messages[mid]["folder"] = to      # move: the message named, and nothing else
            extra = {"from": box.path(source)} if source else {}
            return 0, json.dumps({"moved": [mid], "count": 1, "to": box.path(to), **extra}), ""
        if command == "spam":
            # What Zoho does (never reached: the guard refuses it): the whole conversation, sent replies too.
            thread = box.messages[rest[0]]["thread"]
            for m in box.messages.values():
                if thread is not None and m["thread"] == thread:
                    m["folder"] = SPAM
            return 0, json.dumps({"spam": [rest[0]], "count": 1}), ""
        raise AssertionError(f"the fake dt-zoho does not know {args}")

    @staticmethod
    def record(mid, m):
        moment = datetime.strptime(m["received"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        return {"id": mid, "folder_id": m["folder"], "thread_id": m["thread"],
                "received": moment.astimezone(ZONE).isoformat(timespec="seconds"), "from": m["from"], "to": m["to"],
                "cc": m["cc"], "subject": m["subject"], "read": False, "flag": "flag_not_set",
                "attachments": m["attachments"], "size": "1000", "summary": m["summary"]}

    # ------------------------------------------------------------------------------------------------ reading the log

    def filed(self):
        return [a for a in self.log if a[0] in ("move", "spam", "archive")]

    def commands(self):
        return [a[0] for a in self.log]

    def inbox_lists(self):
        return [a for a in self.log if a[0] == "list" and a[a.index("--folder") + 1] == INBOX]


class ZohoWorld(phase2.World):
    def __init__(self, world, tools):
        super().__init__(world.screen, world.graph, world.sign_in, world.home, world.state, world.capsys)
        self.tools = tools

    def key(self, address, mid):
        return self.screen.short_key(address, mid)


world = phase2.world   # phase 2's fixture: fresh screen.py, scratch homes, the fake Graph and MSAL


@pytest.fixture
def zw(world, monkeypatch):
    """Phase 2's world, with the fake dt-zoho and cc-secrets in place of the programs."""
    tools = FakeTools(world.base)
    monkeypatch.setattr(world.screen.shutil, "which", tools.which)
    monkeypatch.setattr(world.screen.subprocess, "run", tools.run)
    return ZohoWorld(world, tools)


def zoho(address=BOX, mode="observe", **fields):
    return channel(address, mode=mode, provider="zoho", **fields)


def cursor(newest="2026-10-18T09:00:00Z", *ids):
    return {"provider": "zoho", "newest": newest, "idsAtNewest": sorted(ids)}


def section(context, address=BOX):
    return next(s for s in context["channels"] if s["address"] == address)
