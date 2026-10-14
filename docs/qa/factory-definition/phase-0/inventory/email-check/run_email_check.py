"""Phase 0 (g): run the Outreach mail check by hand under `cc-secrets run`, the way the Director will.

Two runs, so the variable-name rule is exercised both ways:
  A. ONE secret:  cc-secrets run outreach-mail-client-secret --via env --env-name OUTREACH_MAIL_CLIENT_SECRET
  B. TWO secrets: cc-secrets run outreach-mail-client-secret --with outreach-mail-client-id
The tenant id (and in run A the client id) are settings, not secrets; they are read with `cc-secrets get`
and handed to the check in the variables the rule names, as the Director will hand over declared settings.

Each run gets a fresh state file named in CC_CHECK_STATE_FILE. The contract is then checked: exit 0, one
JSON object, an integer `count`, a `context` of at most 8 KB, and a state file written back.

What is printed is REDACTED for the record: a sender becomes "<someone>@<their domain>" and a subject is
withheld, apart from a bounce marker it starts with. Message bodies are never fetched at all.

Usage:  python run_email_check.py
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(HERE, "check_new_mail.py")
SECRETS = shutil.which("cc-secrets")
if SECRETS is None:
    sys.exit("ERROR: cc-secrets is not on the PATH; it is installed with DevThrottle's tools.")
with open(os.path.join(HERE, "mail-rules.json"), encoding="utf-8") as fh:
    MARKERS = json.load(fh)["bounceSubjectMarkers"]


def setting(name):
    p = subprocess.run([SECRETS, "get", name], capture_output=True, text=True)
    return p.returncode, (p.stdout.strip() if p.returncode == 0 else (p.stderr or p.stdout).strip())


def redact(context):
    out = []
    for line in context.splitlines():
        m = re.match(r"^(\S+) from (\S+) - (.*) \[(\w+)\]$", line)
        if not m:
            out.append(line if line.endswith("more not shown)") else "<line withheld>")
            continue
        when, sender, subject, folder = m.groups()
        domain = sender.split("@", 1)[1] if "@" in sender else "(none)"
        marker = next((k for k in MARKERS if subject.lower().startswith(k.lower())), None)
        out.append(f"{when} from <someone>@{domain} - {marker + ': <rest withheld>' if marker else '<subject withheld>'} [{folder}]")
    return "\n".join(out)


def one_run(title, args, extra_env):
    print(f"=== {title}")
    shown = "cc-secrets run " + " ".join(args) + " -- python check_new_mail.py"
    print(f"$ {shown}")
    with tempfile.TemporaryDirectory() as tmp:
        state = os.path.join(tmp, "state.json")
        open(state, "w").close()
        env = dict(os.environ, CC_CHECK_STATE_FILE=state, **extra_env)
        p = subprocess.run([SECRETS, "run", *args, "--", sys.executable, CHECK], capture_output=True, text=True,
                           env=env, stdin=subprocess.DEVNULL)
        lines = [ln.rstrip() for ln in p.stdout.splitlines() if ln.strip() and not ln.startswith("[cc-secrets]")]
        print(f"    exit {p.returncode}")
        for ln in (p.stderr + "\n" + "\n".join(ln for ln in p.stdout.splitlines() if ln.startswith("[cc-secrets]"))).splitlines():
            if ln.strip():
                print(f"    {ln.rstrip()}")
        if p.returncode != 0:
            print("    CONTRACT: failed check (non-zero exit), recorded with the reason above")
            print()
            return False
        if len(lines) != 1:
            print(f"    CONTRACT BROKEN: {len(lines)} lines on standard output, one JSON object expected")
            print()
            return False
        result = json.loads(lines[0])
        ok = (isinstance(result.get("count"), int) and len(result.get("context", "").encode("utf-8")) <= 8192
              and os.path.getsize(state) > 0)
        print(f"    count: {result['count']}")
        print(f"    context bytes: {len(result['context'].encode('utf-8'))} (cap 8192)")
        print(f"    state file written back: {os.path.getsize(state)} bytes")
        print("    context, redacted:")
        for ln in redact(result["context"]).splitlines() or ["(empty)"]:
            print(f"        {ln}")
        print(f"    CONTRACT: {'kept' if ok else 'BROKEN'}")
        print()
        return ok


def main():
    code, tenant = setting("outreach-mail-tenant-id")
    print(f"cc-secrets get outreach-mail-tenant-id: {'held' if code == 0 else tenant}")
    code2, client = setting("outreach-mail-client-id")
    print(f"cc-secrets get outreach-mail-client-id: {'held' if code2 == 0 else client}")
    print()
    base = {"OUTREACH_MAIL_TENANT_ID": tenant} if code == 0 else {}
    one_run("A. One secret, --env-name by the rule",
            ["outreach-mail-client-secret", "--via", "env", "--env-name", "OUTREACH_MAIL_CLIENT_SECRET"],
            dict(base, **({"OUTREACH_MAIL_CLIENT_ID": client} if code2 == 0 else {})))
    one_run("B. Two secrets, --with, each in the variable its entry names",
            ["outreach-mail-client-secret", "--with", "outreach-mail-client-id"], base)
    return 0


if __name__ == "__main__":
    sys.exit(main())
