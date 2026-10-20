"""Seed a THROWAWAY Gateway for the phase 7 screenshots of the Mail Desk page (docs/qa/mail-desk/phase-7/CONTRACT.md,
section 4). Never the hosted Gateway: the address must be this machine (127.0.0.1 or localhost), and the database must
be the throwaway Gateway's own gateway.db.

Four scenarios, in the order the capture takes them on ONE Gateway and ONE account:
  empty        the Mail Desk switch ON and no mailbox on the desk      -> the page shows the empty sentence
  unpublished  three mailboxes (below), the mail-desk factory still a draft, so nothing has ever checked them
                                                                       -> every card: "Not checked yet - the mail-desk
                                                                          factory is not published", "Not known yet"
  on           the factory published, one screening run's last check, its kept checks and today's decisions
                                                                       -> each card its own signed-in state; the last
                                                                          check names the account's run, and on the
                                                                          failing mailbox says it has not been read since
  off          the switch OFF again                                    -> /mail-desk is "Page not found", no entry

The three mailboxes (contract section 4: one signed in with decisions today, one NOT signed in whose last check
failed with a sign-in sentence, one in observe mode, one in live mode):
  email@devthrottle.com   on devlinux                         observe, signed in, 7 emails decided today
  desk@example.com        on devlinux, then qa-north          observe, NOT signed in (its check failed)
  letters@example.org     on qa-north, then qa-south, then devlinux   LIVE, signed in, 1 email decided today
LIVE MODE IS ON AN EXAMPLE ADDRESS, never on email@devthrottle.com (the phase 7 Tech Lead, 19 October 2026): the owner
reads these pictures, and email@devthrottle.com shown as live would read as the real mailbox running live, which the
mission forbids in production.

LAST CHECK IS PER ACCOUNT, BY DESIGN (the phase 7 Tech Lead's ruling, 19 October 2026). One run of the account's Mail
Desk start checks every mailbox, so the start's last check is each mailbox's last check, and the fold reads it off the
start's trigger. Phase 3's kept checks cannot say when a mailbox was last checked: they hold only failures and the
first good check after one. A kept check with no published factory cannot happen, so `on` seeds the state a real
screening run leaves: the factory published, the start's trigger carrying that run's time, outcome and summary, and a
kept check for every mailbox at that same time.

How each part is written, and why:
  - The account: a self-hosted Gateway records no email for its one account ("local"), so the account's row in
    `tenants` is written here with email@devthrottle.com, as phase 1's proof did (phase-1/switch/README.md).
  - The switch: the administrator route POST /gateway/admin/mail-desk, the one way it is ever switched, with the
    throwaway Gateway's own administrator token (QA_ADMIN_TOKEN, the value the capture started it with).
  - The mailboxes and live mode: the page's own routes, POST /gateway/mail-desk/channels and /channels/mode, as a
    person (QA_PERSON_KEY, a browser device enrolled on the throwaway Gateway).
  - The factory: switching the Mail Desk on offers the built-in template as a DRAFT of the account's mail-desk
    factory (MailDeskTemplateOffer). `on` publishes that draft as a person through the factory's own route,
    POST /factory-definitions/mail-desk/publish, which creates the Mail Desk start's trigger.
  - The last check, the kept checks and the decisions: the trigger's LastCheckUtc, LastOutcome and LastReason, and rows
    in `mail_desk_channel_checks` and `mail_desk_decisions`. Nothing on a Gateway writes them but a screening run,
    which needs a real mailbox and a Director; this script never reaches one. They are written as that run would:
    outcome `screened` with the run's summary as its reason (MailDeskService.ScreenAsync), a first good check for each
    mailbox that read and a failure for the one that did not (MailDeskChannelCheckStore). On a Gateway from before
    phase 3 the kept-checks table does not exist and the script stops, saying so.

The repository is public: addresses are email@devthrottle.com and the reserved example domains only, machine names
are made up (devlinux is this machine's name, already in the mission), and every subject and sender below is invented.

Usage:
    QA_PERSON_KEY=... QA_ADMIN_TOKEN=... python3 seed.py --gateway http://127.0.0.1:<port> --db <gateway.db> --scenario empty
    (then unpublished, on and off, in that order)
"""

import argparse
import json
import os
import sqlite3
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

ACCOUNT_ID = "local"
ACCOUNT_EMAIL = "email@devthrottle.com"
ACTOR = "phase 7 QA seeding script"
WHY = "phase 7 QA screenshots on a throwaway Gateway"

SIGNED_IN_BOX = "email@devthrottle.com"
FAILING_BOX = "desk@example.com"
LIVE_BOX = "letters@example.org"

MAILBOXES = [
    {"address": SIGNED_IN_BOX, "machines": ["devlinux"]},
    {"address": FAILING_BOX, "machines": ["devlinux", "qa-north"]},
    {"address": LIVE_BOX, "machines": ["qa-north", "qa-south", "devlinux"]},
]

# The sentence a failed check carries names the fix (phase 1 contract, section 5). Invented nickname, made-up machine.
SIGN_IN_FAILURE = f"{FAILING_BOX} is not signed in on devlinux: run cc-outlook -a desk-example auth"

# The last screening run: four minutes ago, outcome `screened`, its summary worded exactly as MailDeskService words one.
RUN_MINUTES_AGO = 4
RUN_SUMMARY = ("1 decided, 7 already decided, 0 to read again, 0 kept after 3 failures, 0 stopped by the spend limit, "
               "1 of 3 mailboxes failing")
MAIL_DESK_FACTORY = "mail-desk"
MAIL_DESK_CHECK = "checks/screen.py"

# Today's decisions: (mailbox, decision, layer, minutes ago, sender, subject). Invented senders on example domains.
DECISIONS = [
    (SIGNED_IN_BOX, "route", "route-model", 95, "orders@shop.example", "Order 1042 needs a delivery date"),
    (SIGNED_IN_BOX, "route", "rule", 80, "billing@vendor.example", "Invoice for October"),
    (SIGNED_IN_BOX, "archive", "allow", 70, "news@letters.example", "Weekly digest"),
    (SIGNED_IN_BOX, "archive", "rule", 55, "receipts@shop.example", "Your receipt"),
    (SIGNED_IN_BOX, "archive", "rule", 40, "receipts@shop.example", "Your second receipt"),
    (SIGNED_IN_BOX, "junk", "block", 30, "prizes@lottery.example", "You have won"),
    (SIGNED_IN_BOX, "keep-for-owner", "sender-screen", 15, "friend@example.net", "Dinner on Friday?"),
    (LIVE_BOX, "archive", "rule", 20, "updates@service.example", "Service notice"),
]

# What a live decision did; an observe-mode decision did nothing, so it has no outcome.
LIVE_OUTCOMES = {"route": "seat-started", "archive": "filed", "junk": "filed", "keep-for-owner": "filed"}


def fail(message):
    print(f"[seed] {message}", file=sys.stderr)
    sys.exit(1)


def say(message):
    print(f"[seed] {message}", flush=True)


def local_only(gateway):
    host = urllib.parse.urlparse(gateway).hostname
    if host not in ("127.0.0.1", "localhost"):
        fail(f"{gateway} is not this machine; this script seeds a throwaway Gateway only")


def call(gateway, method, path, body, token):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(gateway + path, data=data, method=method, headers={
        "Authorization": f"Bearer {token}", "Content-Type": "application/json", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            return res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8")


def expect(what, answer, statuses):
    status, body = answer
    if status not in statuses:
        fail(f"{what}: the Gateway answered {status} {body[:300]}")
    say(f"{what}: {status}")
    return json.loads(body) if body else None


# ---------------------------------------------------------------------------------------------- the database


def connect(db):
    if not os.path.isfile(db) or os.path.basename(db) != "gateway.db":
        fail(f"{db} is not a gateway.db")
    c = sqlite3.connect(db)
    tables = {r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    for needed in ("tenants", "mail_desk_channels", "mail_desk_decisions"):
        if needed not in tables:
            fail(f"{db} has no {needed} table; it is not a Gateway with the Mail Desk")
    if "triggers" not in tables:
        fail(f"{db} has no triggers table")
    if "mail_desk_channel_checks" not in tables:
        fail(f"{db} has no mail_desk_channel_checks table: this Gateway is from before Mail Desk phase 3, whose kept "
             "checks say whether each mailbox is signed in")
    return c


def columns(c, table):
    return [r[1] for r in c.execute(f"PRAGMA table_info({table})")]


def insert(c, table, row):
    have = columns(c, table)
    missing = [k for k in row if k not in have]
    if missing:
        fail(f"{table} has no column(s) {missing}; its columns are {have}")
    c.execute(f"INSERT INTO {table} ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))


def stamp(when):
    """A time the way the Gateway's SQLite columns hold one (EF Core: no zone, seven fraction digits)."""
    return when.astimezone(timezone.utc).replace(tzinfo=None).strftime("%Y-%m-%d %H:%M:%S.%f") + "0"


def new_id(like):
    """A new Guid, in the same letter case the Gateway wrote `like` in."""
    value = str(uuid.uuid4())
    return value.upper() if like.isupper() else value


def ensure_account(c):
    row = c.execute("SELECT Email FROM tenants WHERE Id = ?", (ACCOUNT_ID,)).fetchone()
    if row is not None:
        if row[0] != ACCOUNT_EMAIL:
            fail(f"the account {ACCOUNT_ID} already carries the email {row[0]!r}; this is not a throwaway Gateway seeded by this script")
        say(f"account {ACCOUNT_ID} ({ACCOUNT_EMAIL}): already written")
        return
    insert(c, "tenants", {"Id": ACCOUNT_ID, "AccountSubject": "qa-phase-7-account", "Email": ACCOUNT_EMAIL,
                          "CreatedAtUtc": stamp(datetime.now(timezone.utc))})
    c.commit()
    say(f"account {ACCOUNT_ID} ({ACCOUNT_EMAIL}): written")


def channel_ids(c):
    return {address: cid for cid, address in c.execute(
        "SELECT Id, Address FROM mail_desk_channels WHERE tenant_id = ? AND RemovedUtc IS NULL", (ACCOUNT_ID,))}


def write_checks(c, ids, checked):
    """What one screening run keeps for each mailbox it checked: a first good check, or the failure's sentence."""
    for address, ok, sentence in ((SIGNED_IN_BOX, 1, None), (FAILING_BOX, 0, SIGN_IN_FAILURE), (LIVE_BOX, 1, None)):
        cid = ids[address]
        insert(c, "mail_desk_channel_checks", {
            "Id": new_id(cid), "tenant_id": ACCOUNT_ID, "ChannelId": cid, "TriggerRunId": None,
            "CheckedUtc": stamp(checked), "Machine": "devlinux", "Ok": ok, "Sentence": sentence, "Detail": None})
        say(f"check for {address}: {'read the mailbox' if ok else 'failed - ' + sentence}")
    c.commit()


def write_last_check(c, checked):
    """The Mail Desk start's trigger, as the screening run left it. The publish created it; there must be exactly one."""
    have = columns(c, "triggers")
    for needed in ("Factory", "CheckPath", "LastCheckUtc", "LastOutcome", "LastReason"):
        if needed not in have:
            fail(f"triggers has no column {needed}; its columns are {have}")
    rows = c.execute("SELECT Id FROM triggers WHERE tenant_id = ? AND Factory = ? AND CheckPath = ?",
                     (ACCOUNT_ID, MAIL_DESK_FACTORY, MAIL_DESK_CHECK)).fetchall()
    if len(rows) != 1:
        fail(f"expected the publish to create one Mail Desk start, found {len(rows)}")
    c.execute("UPDATE triggers SET LastCheckUtc = ?, LastOutcome = ?, LastReason = ? WHERE Id = ?",
              (stamp(checked), "screened", RUN_SUMMARY, rows[0][0]))
    c.commit()
    say(f"the Mail Desk start's last check: screened, {RUN_SUMMARY}")


def minutes_ago(now, minutes):
    """`minutes` before `now`, squeezed so it is still TODAY. The page counts decisions since local midnight in the
    account's time zone, which on a throwaway Gateway is this machine's own (TimeZoneConfig). Taken shortly after
    midnight, the seed's 95 minutes would fall on yesterday and the pictures would show the wrong counts, so every time
    is scaled into the time since local midnight, keeping its order. Later in the day nothing is scaled."""
    local_now = now.astimezone()
    since = (local_now - local_now.replace(hour=0, minute=0, second=0, microsecond=0)).total_seconds() / 60
    longest = max(m for *_, m, _, _ in DECISIONS) + 1  # the received time is one minute before the decision
    scale = 1.0 if since > longest + 1 else since * 0.9 / longest
    return now - timedelta(minutes=minutes * scale)


def write_decisions(c, ids, now):
    for n, (address, decision, layer, minutes, sender, subject) in enumerate(DECISIONS, start=1):
        cid = ids[address]
        live = address == LIVE_BOX
        at = minutes_ago(now, minutes)
        insert(c, "mail_desk_decisions", {
            "Id": new_id(cid), "tenant_id": ACCOUNT_ID, "ChannelId": cid, "MessageKey": f"qa-phase-7-message-{n}",
            "ReceivedUtc": stamp(minutes_ago(now, minutes + 1)), "SenderAddress": sender, "Subject": subject,
            "Layer": layer, "Decision": decision,
            "RouteFactory": "qa-orders" if decision == "route" else None,
            "RouteSeat": "orders-desk" if decision == "route" else None,
            "Reason": "invented for the phase 7 screenshots", "Confidence": None, "CostMicros": None,
            "Mode": "live" if live else "observe", "Outcome": LIVE_OUTCOMES.get(decision) if live else None,
            "OutcomeDetail": None, "DecidedUtc": stamp(at), "AttemptCount": 1, "RulesVersion": 0})
    c.commit()
    say(f"decisions: {len(DECISIONS)} written, every one today")


# ---------------------------------------------------------------------------------------------- the scenarios


def switch(gateway, admin, on):
    expect(f"switch Mail Desk {'on' if on else 'off'} for {ACCOUNT_EMAIL}",
           call(gateway, "POST", "/gateway/admin/mail-desk",
                {"account": ACCOUNT_ID, "enabled": on, "actor": ACTOR, "reason": WHY}, admin), (200,))


def publish_factory(gateway, key):
    """Publish the draft the switch offered, as a person, through the factory's own route."""
    status, body = call(gateway, "GET", f"/factory-definitions/{MAIL_DESK_FACTORY}/history", None, key)
    if status != 200:
        fail(f"read the mail-desk factory's versions: the Gateway answered {status} {body[:300]}")
    history = json.loads(body)
    versions = history.get("versions") if isinstance(history, dict) else history
    if not versions:
        fail(f"the mail-desk factory has no version; switching the Mail Desk on offers one. The answer was {body[:300]}")
    newest = max(v["version"] for v in versions)
    expect(f"publish the mail-desk factory, version {newest} (the template the switch offered)",
           call(gateway, "POST", f"/factory-definitions/{MAIL_DESK_FACTORY}/publish", {"version": newest}, key), (200,))


def add_mailboxes(gateway, key):
    for box in MAILBOXES:
        expect(f"add {box['address']} on {', '.join(box['machines'])}",
               call(gateway, "POST", "/gateway/mail-desk/channels",
                    {"address": box["address"], "provider": "outlook", "machines": box["machines"], "why": WHY}, key),
               (201,))
    expect(f"put {LIVE_BOX} in live mode (this throwaway Gateway only)",
           call(gateway, "POST", "/gateway/mail-desk/channels/mode",
                {"address": LIVE_BOX, "mode": "live", "why": WHY}, key), (200,))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--gateway", required=True)
    parser.add_argument("--db", required=True)
    parser.add_argument("--scenario", required=True, choices=("empty", "unpublished", "on", "off"))
    args = parser.parse_args()
    key, admin = os.environ.get("QA_PERSON_KEY"), os.environ.get("QA_ADMIN_TOKEN")
    if not key or not admin:
        fail("set QA_PERSON_KEY (an enrolled browser device of the throwaway Gateway) and QA_ADMIN_TOKEN")
    gateway = args.gateway.rstrip("/")
    local_only(gateway)
    c = connect(args.db)
    try:
        ensure_account(c)
        if args.scenario == "off":
            switch(gateway, admin, False)
            return
        switch(gateway, admin, True)
        if args.scenario == "empty":
            if channel_ids(c):
                fail("the account already has mailboxes; the empty scenario is taken before the on scenario")
            return
        if args.scenario == "unpublished":
            if channel_ids(c):
                fail("the account already has mailboxes; the unpublished scenario is taken once, before the on scenario")
            add_mailboxes(gateway, key)
            return
        ids = channel_ids(c)
        if set(ids) != {box["address"] for box in MAILBOXES}:
            fail("the on scenario follows the unpublished one: the three mailboxes must already be on the desk")
        publish_factory(gateway, key)
        now = datetime.now(timezone.utc)
        checked = minutes_ago(now, RUN_MINUTES_AGO)
        write_last_check(c, checked)
        write_checks(c, ids, checked)
        write_decisions(c, ids, now)
    finally:
        c.close()


if __name__ == "__main__":
    main()
