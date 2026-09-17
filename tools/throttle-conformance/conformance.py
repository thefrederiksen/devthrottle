"""The Your Throttle conformance check (mission "Clean up Your Throttle", phase three).

Two consumers read one substrate - the Gateway's submission ledger (activity_events, turn-submitted) - and
this check FAILS when they diverge. It computes the shared figure twice, over the same account and the
same week, and compares every number:

  1. THE LIBRARY: the Gateway's own ThrottleDefinition and ThrottleLedgerReader, the code behind
     GET /stats/data, run by tools/throttle-conformance against the hosted Gateway database (read-only).
  2. THE MENTOR REPORT'S SIDE: the mentor harness's own reader of the same ledger (tools/mentor/origin.py
     Ledger over tools/mentor/metrics.py load_events), fed from the harness's own extract of the table,
     with ruling R17's predicate applied - InputOrigin present, grouped by modality and surface.

A third, deliberately plain reading straight off the extract's JSON lines (no library, no harness) covers
what the mentor's reader does not carry - the per-agent split and the per-repository join through
session_history - so those are checked too.

The predicate is stated once, in the Gateway (ThrottleDefinition.Predicate), and the check ALSO asserts
the library reports that exact sentence - read out of ThrottleDefinition.cs at run time, never restated
here (final inspection finding F-08: a Python copy of the sentence was a second authority to maintain).

THE LIBRARY IS BUILT FROM SOURCE ON EVERY RUN (final inspection finding F-03). A dll that happens to exist
is not provenance: it can predate the source beside it, and then the library, this check and the mentor
report all agree with an implementation the deployed Gateway no longer runs. `dotnet build` is incremental,
so an unchanged tree costs seconds; a changed one is rebuilt, which is the point. The check records the
library's provenance - the commit and dirtiness of the product checkout and the dll's own digest - in its
report.

WHAT IS COMPARED (finding F-08): not only the counts and the buckets, but the library's FINISHED HEADLINE -
the denominator, every share and every rounded percent the two consumers print - the unit, the window's
kind, label and choices, the ledger's retention and earliest instant, the hourly voice and typed values, and
the repository display names and checkouts. Every field the field inventory names as read by the report is
compared here against the independent reading.

Usage (the connection string comes from cc-secrets, in the variable DEVTHROTTLE_GATEWAY_DB_CONNECTION):
  cc-secrets run devthrottle-gateway-db-connection --timeout 0 -- python tools/throttle-conformance/conformance.py --account soren --week 2026-W35
  cc-secrets run devthrottle-gateway-db-connection --timeout 0 -- python tools/throttle-conformance/conformance.py --account mario --week 2026-W34 --report out.md

Options:
  --account   a label from the mentor config's accounts (soren, mario)
  --week      an ISO week, in the account's time zone, Monday to Monday (the mentor's own week bounds)
  --mentor-dir      the mentor harness directory holding config.json, metrics.py and origin.py
                    (default: D:/ReposFred/devthrottle_internal/tools/mentor)
  --connection-file a file holding the Gateway database connection string (default: the environment
                    variable DEVTHROTTLE_GATEWAY_DB_CONNECTION, which cc-secrets run supplies)
  --report          write a markdown report here as well as printing the verdict
  --break-predicate DELIBERATELY misapply the predicate on the mentor side (drop null-send-source rows), to
                    prove the check goes red. Never green with this flag.

The library is BUILT FROM THE PINNED SOURCE AND RUN on every check (final inspection finding F-03). There is
no option to read its figure from a file: a check over a saved answer is a check of whatever produced that
file, not of the library in this checkout, and the fix-round inspector found the previous saved-answer option
labelling exactly that as "built from source this run".

Exit 0 when every number agrees. Exit 1 on any difference. Exit 2 on a usage or setup error.
"""
import argparse
import collections
import datetime as dt
import json
import os
import subprocess
import sys
from pathlib import Path
from zoneinfo import ZoneInfo

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEFAULT_MENTOR = Path("D:/ReposFred/devthrottle_internal/tools/mentor")


def fail(msg, code=2):
    print("ERROR: " + msg, file=sys.stderr)
    sys.exit(code)


def load_mentor(mentor_dir):
    sys.path.insert(0, str(mentor_dir))
    cwd = os.getcwd()
    os.chdir(mentor_dir)
    try:
        import metrics  # noqa: E402
        import origin   # noqa: E402
    finally:
        os.chdir(cwd)
    return metrics, origin


CONNECTION_ENV = "DEVTHROTTLE_GATEWAY_DB_CONNECTION"
CONNECTION_ENTRY = "devthrottle-gateway-db-connection"


def read_connection(args):
    if args.connection_file:
        return Path(args.connection_file).read_text(encoding="utf-8").strip()
    connection = os.environ.get(CONNECTION_ENV, "").strip()
    if not connection:
        fail(CONNECTION_ENV + " is not set. Run this through cc-secrets:\n"
             "  cc-secrets run " + CONNECTION_ENTRY + " --timeout 0 -- python tools/throttle-conformance/conformance.py "
             "--account <label> --week <week>")
    return connection


def library_provenance():
    """Where the library's code came from: the product checkout's commit and whether it is dirty, read with
    git at the moment of use. A dirty checkout is reported, not hidden - the number then comes from code no
    commit names."""
    def git(*args):
        run = subprocess.run(["git", "-C", str(REPO)] + list(args), capture_output=True, text=True)
        if run.returncode != 0:
            fail("git " + " ".join(args) + " failed in " + str(REPO) + ": " + run.stderr.strip())
        return run.stdout.strip()
    commit = git("rev-parse", "HEAD")
    dirty = git("status", "--porcelain", "--", "src/CcDirector.Gateway", "src/CcDirector.Core", "tools/throttle-conformance") != ""
    return {"commit": commit, "dirty": dirty}


def build_library():
    """Build the library tool from the pinned source, EVERY run (finding F-03), and return its dll and digest."""
    project = HERE / "ThrottleConformance.csproj"
    print("building the library tool from " + str(project))
    build = subprocess.run(["dotnet", "build", str(project), "-nologo", "-v", "q"], capture_output=True, text=True)
    if build.returncode != 0:
        fail("building the library tool failed:\n" + build.stdout + build.stderr)
    dll = HERE / "bin" / "Debug" / "net10.0" / "throttle-conformance.dll"
    if not dll.exists():
        fail("the build reported success but " + str(dll) + " is not there")
    import hashlib
    return dll, hashlib.sha256(dll.read_bytes()).hexdigest()


def run_library(tenant, start, end, connection, out_path):
    """Run the Gateway's own definition through tools/throttle-conformance, built from source first. Returns
    the figure dict and the digest of the dll that answered."""
    dll, dll_sha256 = build_library()
    cmd = ["dotnet", str(dll), "--tenant", tenant, "--from", start.isoformat(), "--to", end.isoformat(),
           "--connection", connection, "--out", str(out_path)]
    run = subprocess.run(cmd, capture_output=True, text=True)
    # stderr carries the one summary line (and never the connection string); surface it.
    for line in run.stderr.splitlines():
        print("  [library] " + line)
    if run.returncode != 0:
        fail("the library tool exited " + str(run.returncode), code=1)
    return json.loads(Path(out_path).read_text(encoding="utf-8")), dll_sha256


def mentor_side(metrics, origin, account, tz, extract, start, end, break_predicate):
    """The mentor harness's reading of the same ledger, with the R17 predicate applied."""
    world = metrics.World(account["label"], tz)
    metrics.load_events(world, extract)
    ledger = origin.Ledger(world.events)
    rows = [r for rows in ledger.by_session.values() for r in rows if start <= r["ts"] < end]
    buckets = collections.Counter()
    excluded = collections.Counter()
    for r in rows:
        if break_predicate and r["source"] is None:
            # THE KNOWN-BAD INPUT: pretend the terminal-typed turns (null send source) are not turns. This is
            # exactly defect one, and the check must go red on it.
            excluded["noInputOrigin"] += 1
            excluded["unresolved"] += 1
            continue
        if r["origin"] is None:
            excluded["noInputOrigin"] += 1
            if r["source"] == "Agent":
                excluded["agentDriven"] += 1
            elif r["source"] == "Framework":
                excluded["framework"] += 1
            else:
                excluded["unresolved"] += 1
            continue
        modality, surface = r["origin"]
        buckets[(modality, surface)] += 1
    # The mentor's Ledger keys rows by session already; count the sessions that had a counted turn.
    counted_sessions = {
        s for s, srows in ledger.by_session.items()
        if any(start <= r["ts"] < end and r["origin"] is not None and not (break_predicate and r["source"] is None)
               for r in srows)
    }
    earliest_in_window = min((r["ts"] for r in rows), default=None)
    return {
        "earliestInWindow": earliest_in_window.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") if earliest_in_window else None,
        "turns": sum(buckets.values()),
        "voiceTurns": sum(v for (m, _), v in buckets.items() if m == "voice"),
        "typedTurns": sum(v for (m, _), v in buckets.items() if m == "typed"),
        "sessions": len(counted_sessions),
        "buckets": {m + "/" + s: v for (m, s), v in sorted(buckets.items())},
        "excluded": {k: excluded.get(k, 0) for k in ("noInputOrigin", "agentDriven", "framework", "unresolved")},
        "agentDrivenTurns": excluded.get("agentDriven", 0),
        "rowsInWindow": len(rows),
    }


def predicate_from_source():
    """The R17 predicate as ThrottleDefinition.cs states it - the ONE place it is written (finding F-08)."""
    source = (REPO / "src" / "CcDirector.Gateway" / "Throttle" / "ThrottleDefinition.cs").read_text(encoding="utf-8")
    import re
    match = re.search(r'public const string Predicate =\s*((?:"[^"]*"\s*\+?\s*)+);', source)
    if match is None:
        fail("ThrottleDefinition.cs no longer states the Predicate constant in the shape this check reads")
    return "".join(re.findall(r'"([^"]*)"', match.group(1)))


def headline_side(mentor):
    """The finished headline the independent reading implies - the check's own division, which is what it is
    for: an independent reader that agrees with the library's finished answer."""
    counted = mentor["turns"]
    by_surface = collections.Counter()
    for key, turns in mentor["buckets"].items():
        by_surface[key.split("/", 1)[1]] += turns

    def share(part):
        if counted == 0:
            return {"turns": part, "share": None, "percent": None}
        return {"turns": part, "share": part / counted, "percent": int(part / counted * 100.0 + 0.5)}

    return {
        "denominator": counted,
        "hasData": counted > 0,
        "voice": share(mentor["voiceTurns"]),
        "typed": share(mentor["typedTurns"]),
        "phone": share(by_surface.get("phone", 0)),
        "surfaces": {surface: share(by_surface.get(surface, 0)) for surface in ("desktop", "cockpit", "phone", "unknown")},
    }


def raw_side(extract_events, extract_sessions, start, end):
    """A plain reading of the extract for what the mentor's Ledger does not carry: agent kind and repository."""
    def parse(s):
        return dt.datetime.fromisoformat(s.replace("Z", "+00:00"))
    history = {}
    with open(extract_sessions, encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            history[row["SessionId"]] = (row.get("RepoName") or None, row.get("RepoPath") or None)
    agents = collections.defaultdict(lambda: {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": set(), "agentDrivenTurns": 0})
    repos = collections.defaultdict(lambda: {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": set(),
                                             "repoName": "", "checkouts": set()})
    unattributed = 0
    hours = collections.Counter()
    hours_by_modality = collections.Counter()
    with open(extract_events, encoding="utf-8") as f:
        for line in f:
            row = json.loads(line)
            if row.get("EventType") != "turn-submitted":
                continue
            ts = parse(row["OccurredUtc"])
            if not (start <= ts < end):
                continue
            agent = row.get("AgentKind") or ""
            origin_token = row.get("InputOrigin")
            if not origin_token:
                if row.get("SendSource") == "Agent":
                    agents[agent]["agentDrivenTurns"] += 1
                continue
            modality = origin_token.split("/", 1)[0]
            a = agents[agent]
            a["turns"] += 1
            a["voiceTurns" if modality == "voice" else "typedTurns"] += 1
            a["sessions"].add(row["SessionId"])
            hour_key = ts.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H")
            hours[hour_key] += 1
            hours_by_modality[(hour_key, modality)] += 1
            name, path = history.get(row["SessionId"], (None, None))
            if name:
                key = name
                leaf = name.rstrip("/").split("/")[-1]
            elif path:
                key = path.rstrip("/\\").replace("\\", "/").split("/")[-1]
                leaf = key
            else:
                unattributed += 1
                continue
            r = repos[key]
            r["turns"] += 1
            r["voiceTurns" if modality == "voice" else "typedTurns"] += 1
            r["sessions"].add(row["SessionId"])
            r["repoName"] = leaf
            if path:
                r["checkouts"].add(path)
    return {
        "agents": {k: {"turns": v["turns"], "voiceTurns": v["voiceTurns"], "typedTurns": v["typedTurns"],
                       "sessions": len(v["sessions"]), "agentDrivenTurns": v["agentDrivenTurns"]}
                   for k, v in agents.items()},
        "repos": {k: {"turns": v["turns"], "voiceTurns": v["voiceTurns"], "typedTurns": v["typedTurns"],
                      "sessions": len(v["sessions"]), "repoName": v["repoName"],
                      "checkouts": sorted(v["checkouts"])} for k, v in repos.items()},
        "reposUnattributedTurns": unattributed,
        "hourlyTurns": dict(hours),
        "hourlyByModality": {"%s/%s" % key: value for key, value in hours_by_modality.items()},
    }


def compare(library, mentor, raw, predicate_expected, window_expected):
    """Every difference, as a list of one-line strings. Empty means the consumers agree."""
    diffs = []

    def eq(name, a, b):
        if a != b:
            diffs.append("%s: library=%s mentor-side=%s" % (name, a, b))

    if library.get("definition") != predicate_expected:
        diffs.append("definition: the library does not report the R17 predicate verbatim: %r" % library.get("definition"))
    eq("unit", library.get("unit"), "submitted turns")

    # THE FINISHED HEADLINE (finding F-08): what both consumers print, against the independent reading's
    # own division. A share is compared to nine decimal places; a percent exactly.
    head = library.get("headline")
    if not isinstance(head, dict):
        diffs.append("headline: the library serves no headline block")
    else:
        expected = headline_side(mentor)
        eq("headline.denominator", head.get("denominator"), expected["denominator"])
        eq("headline.hasData", head.get("hasData"), expected["hasData"])

        def eq_share(name, got, want):
            if not isinstance(got, dict):
                diffs.append("%s: the library serves no share block" % name)
                return
            eq(name + ".turns", got.get("turns"), want["turns"])
            eq(name + ".percent", got.get("percent"), want["percent"])
            a, b = got.get("share"), want["share"]
            if (a is None) != (b is None) or (a is not None and round(a, 9) != round(b, 9)):
                diffs.append("%s.share: library=%s mentor-side=%s" % (name, a, b))

        for key in ("voice", "typed", "phone"):
            eq_share("headline." + key, head.get(key), expected[key])
        # The other side of the phone ring, served rather than subtracted by a consumer (fix-round F-01).
        if isinstance(head.get("phone"), dict):
            eq("headline.phone.remainder", head["phone"].get("remainder"), expected["denominator"] - expected["phone"]["turns"])
        served = head.get("surfaces") if isinstance(head.get("surfaces"), list) else []
        eq("headline.surfaces (order)", [s.get("surface") for s in served], list(expected["surfaces"]))
        for entry in served:
            surface = entry.get("surface")
            if surface in expected["surfaces"]:
                eq_share("headline.surfaces[%s]" % surface, entry, expected["surfaces"][surface])
                eq("headline.surfaces[%s].remainder" % surface, entry.get("remainder"),
                   expected["denominator"] - expected["surfaces"][surface]["turns"])
                if not isinstance(entry.get("label"), str) or not entry["label"]:
                    diffs.append("headline.surfaces[%s].label: the library serves no label" % surface)

    # The window statement, the selector's choices and the ledger's reach - every field the report reads.
    window = library.get("window") if isinstance(library.get("window"), dict) else {}
    eq("window.fromUtc", window.get("fromUtc"), window_expected[0])
    eq("window.toUtc", window.get("toUtc"), window_expected[1])
    eq("window.kind", window.get("kind"), "explicit")
    eq("window.isDefault", window.get("isDefault"), False)
    if not isinstance(window.get("label"), str) or not window["label"]:
        diffs.append("window.label: the library serves no label")
    choices = window.get("choices")
    if not isinstance(choices, list) or [c.get("days") for c in choices] != [1, 7, 14, 30]:
        diffs.append("window.choices: library=%r, the selector's four lengths expected" % (choices,))
    ledger = library.get("ledger") if isinstance(library.get("ledger"), dict) else {}
    eq("ledger.retentionDays", ledger.get("retentionDays"), 30)
    earliest = ledger.get("earliestUtc")
    if earliest is not None and not isinstance(earliest, str):
        diffs.append("ledger.earliestUtc: library=%r, an instant or null expected" % (earliest,))
    if earliest is not None and mentor["rowsInWindow"] > 0 and mentor.get("earliestInWindow") and earliest > mentor["earliestInWindow"]:
        diffs.append("ledger.earliestUtc: library=%s is later than a row the extract holds in the window (%s)" % (earliest, mentor["earliestInWindow"]))
    eq("turns", library["turns"], mentor["turns"])
    eq("voiceTurns", library["voiceTurns"], mentor["voiceTurns"])
    eq("typedTurns", library["typedTurns"], mentor["typedTurns"])
    eq("sessions", library["sessions"], mentor["sessions"])
    lib_buckets = {b["modality"] + "/" + b["surface"]: b["turns"] for b in library["buckets"]}
    for key in sorted(set(lib_buckets) | set(mentor["buckets"])):
        eq("bucket " + key, lib_buckets.get(key, 0), mentor["buckets"].get(key, 0))
    for key in ("noInputOrigin", "agentDriven", "framework", "unresolved"):
        eq("excluded." + key, library["excluded"][key], mentor["excluded"][key])
    eq("agentDrivenTurns", library["agentDrivenTurns"], mentor["agentDrivenTurns"])

    # A finished ratio against the independent division: the share to nine places, the percent exactly,
    # both null exactly when the divisor is zero (fix-round finding F-01: the rows' ratios are the library's).
    def eq_ratio(name, got_share, got_percent, part, whole):
        want_share = None if whole == 0 else part / whole
        want_percent = None if whole == 0 else int(part / whole * 100.0 + 0.5)
        if (got_share is None) != (want_share is None) or (got_share is not None and round(got_share, 9) != round(want_share, 9)):
            diffs.append("%s share: library=%s independent=%s" % (name, got_share, want_share))
        if got_percent != want_percent:
            diffs.append("%s percent: library=%s independent=%s" % (name, got_percent, want_percent))

    lib_agents = {a["agent"]: a for a in library["agents"]}
    raw_agent_turns = sum(a["turns"] for a in raw["agents"].values())
    raw_agent_sessions = sum(a["sessions"] for a in raw["agents"].values())
    for key in sorted(set(lib_agents) | set(raw["agents"])):
        la = lib_agents.get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "agentDrivenTurns": 0})
        ra = raw["agents"].get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "agentDrivenTurns": 0})
        for field in ("turns", "voiceTurns", "typedTurns", "sessions", "agentDrivenTurns"):
            if la[field] != ra[field]:
                diffs.append("agent %s.%s: library=%s raw-extract=%s" % (key or "(unknown)", field, la[field], ra[field]))
        if key in lib_agents:
            eq_ratio("agent %s turn" % (key or "(unknown)"), la.get("turnShare"), la.get("turnPercent"), ra["turns"], raw_agent_turns)
            eq_ratio("agent %s session" % (key or "(unknown)"), la.get("sessionShare"), la.get("sessionPercent"), ra["sessions"], raw_agent_sessions)
            eq_ratio("agent %s voice" % (key or "(unknown)"), la.get("voiceShare"), la.get("voicePercent"), ra["voiceTurns"], ra["turns"])
    summary = library.get("agentsSummary") if isinstance(library.get("agentsSummary"), dict) else {}
    raw_top_agent = max(raw["agents"].items(), key=lambda kv: kv[1]["turns"], default=(None, {"turns": 0}))
    eq("agentsSummary.agentCount", summary.get("agentCount"), sum(1 for a in raw["agents"].values() if a["turns"] > 0))
    eq("agentsSummary.totalTurns", summary.get("totalTurns"), raw_agent_turns)
    eq("agentsSummary.totalSessions", summary.get("totalSessions"), raw_agent_sessions)
    eq("agentsSummary.voiceTurns", summary.get("voiceTurns"), sum(a["voiceTurns"] for a in raw["agents"].values()))
    eq_ratio("agentsSummary voice", summary.get("voiceShare"), summary.get("voicePercent"),
             sum(a["voiceTurns"] for a in raw["agents"].values()), raw_agent_turns)
    eq_ratio("agentsSummary top", summary.get("topShare"), summary.get("topPercent"),
             raw_top_agent[1]["turns"], raw_agent_turns if raw_top_agent[1]["turns"] > 0 else 0)
    eq("agentsSummary.agentDrivenTurns", summary.get("agentDrivenTurns"), mentor["agentDrivenTurns"])
    want_leverage = None if raw_agent_turns == 0 else mentor["agentDrivenTurns"] / raw_agent_turns
    got_leverage = summary.get("leverage")
    if (got_leverage is None) != (want_leverage is None) or (got_leverage is not None and round(got_leverage, 9) != round(want_leverage, 9)):
        diffs.append("agentsSummary.leverage: library=%s independent=%s" % (got_leverage, want_leverage))
    eq("agentsSummary.leverageText", summary.get("leverageText"), None if want_leverage is None else "%.1fx" % want_leverage)
    eq("agentsSummary.hasData", summary.get("hasData"), raw_agent_turns > 0 or mentor["agentDrivenTurns"] > 0)

    lib_repos = {r["repo"]: r for r in library["repos"]}
    raw_repo_turns = sum(r["turns"] for r in raw["repos"].values())
    raw_repo_sessions = sum(r["sessions"] for r in raw["repos"].values())
    for key in sorted(set(lib_repos) | set(raw["repos"])):
        lr = lib_repos.get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "repoName": "", "checkouts": []})
        rr = raw["repos"].get(key, {"turns": 0, "voiceTurns": 0, "typedTurns": 0, "sessions": 0, "repoName": "", "checkouts": []})
        for field in ("turns", "voiceTurns", "typedTurns", "sessions", "repoName", "checkouts"):
            if lr[field] != rr[field]:
                diffs.append("repo %s.%s: library=%s raw-extract=%s" % (key, field, lr[field], rr[field]))
        if key in lib_repos:
            eq_ratio("repo %s turn" % key, lr.get("turnShare"), lr.get("turnPercent"), rr["turns"], raw_repo_turns)
            eq_ratio("repo %s session" % key, lr.get("sessionShare"), lr.get("sessionPercent"), rr["sessions"], raw_repo_sessions)
            eq_ratio("repo %s voice" % key, lr.get("voiceShare"), lr.get("voicePercent"), rr["voiceTurns"], rr["turns"])
    summary = library.get("reposSummary") if isinstance(library.get("reposSummary"), dict) else {}
    raw_top_repo = max(raw["repos"].items(), key=lambda kv: kv[1]["turns"], default=(None, {"turns": 0, "repoName": None}))
    eq("reposSummary.repoCount", summary.get("repoCount"), len(raw["repos"]))
    eq("reposSummary.totalTurns", summary.get("totalTurns"), raw_repo_turns)
    eq("reposSummary.totalSessions", summary.get("totalSessions"), raw_repo_sessions)
    eq("reposSummary.voiceTurns", summary.get("voiceTurns"), sum(r["voiceTurns"] for r in raw["repos"].values()))
    eq_ratio("reposSummary voice", summary.get("voiceShare"), summary.get("voicePercent"),
             sum(r["voiceTurns"] for r in raw["repos"].values()), raw_repo_turns)
    eq_ratio("reposSummary top", summary.get("topShare"), summary.get("topPercent"), raw_top_repo[1]["turns"], raw_repo_turns)
    eq("reposSummary.topRepoName", summary.get("topRepoName"), raw_top_repo[1].get("repoName") if raw["repos"] else None)
    eq("reposSummary.hasData", summary.get("hasData"), raw_repo_turns > 0)
    if library["reposUnattributedTurns"] != raw["reposUnattributedTurns"]:
        diffs.append("reposUnattributedTurns: library=%s raw-extract=%s" % (library["reposUnattributedTurns"], raw["reposUnattributedTurns"]))
    lib_hours = {h["hour"]: h["turns"] for h in library["hourlyTurns"]}
    for key in sorted(set(lib_hours) | set(raw["hourlyTurns"])):
        if lib_hours.get(key, 0) != raw["hourlyTurns"].get(key, 0):
            diffs.append("hour %s: library=%s raw-extract=%s" % (key, lib_hours.get(key, 0), raw["hourlyTurns"].get(key, 0)))
    # The hourly VOICE and TYPED values, not only the totals (finding F-08).
    lib_hours_mod = {}
    for h in library["hourlyTurns"]:
        lib_hours_mod[h["hour"] + "/voice"] = h["voiceTurns"]
        lib_hours_mod[h["hour"] + "/typed"] = h["typedTurns"]
        if h["voiceTurns"] + h["typedTurns"] != h["turns"]:
            diffs.append("hour %s: the library's voice %s + typed %s is not its turns %s" % (h["hour"], h["voiceTurns"], h["typedTurns"], h["turns"]))
        # The hour's finished split against the raw counts' division (fix-round finding F-01).
        raw_voice = raw["hourlyByModality"].get(h["hour"] + "/voice", 0)
        raw_typed = raw["hourlyByModality"].get(h["hour"] + "/typed", 0)
        raw_total = raw_voice + raw_typed
        for name, got, part in (("voiceShare", h.get("voiceShare"), raw_voice), ("typedShare", h.get("typedShare"), raw_typed)):
            want = None if raw_total == 0 else part / raw_total
            if (got is None) != (want is None) or (got is not None and round(got, 9) != round(want, 9)):
                diffs.append("hour %s %s: library=%s independent=%s" % (h["hour"], name, got, want))
    for key in sorted(set(lib_hours_mod) | set(raw["hourlyByModality"])):
        if lib_hours_mod.get(key, 0) != raw["hourlyByModality"].get(key, 0):
            diffs.append("hour %s: library=%s raw-extract=%s" % (key, lib_hours_mod.get(key, 0), raw["hourlyByModality"].get(key, 0)))
    return diffs


def share(voice, total):
    return "%.2f%%" % (100.0 * voice / total) if total else "n/a"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--account", required=True)
    ap.add_argument("--week", required=True, help="ISO week, e.g. 2026-W35")
    ap.add_argument("--mentor-dir", default=str(DEFAULT_MENTOR))
    ap.add_argument("--connection-file")
    ap.add_argument("--report")
    ap.add_argument("--break-predicate", action="store_true")
    args = ap.parse_args()

    mentor_dir = Path(args.mentor_dir)
    if not (mentor_dir / "config.json").exists():
        fail("no config.json in " + str(mentor_dir))
    cfg = json.loads((mentor_dir / "config.json").read_text(encoding="utf-8"))
    account = next((a for a in cfg["accounts"] if a["label"] == args.account), None)
    if account is None:
        fail("account '%s' is not in the mentor config (%s)" % (args.account, ", ".join(a["label"] for a in cfg["accounts"])))
    tz = account["time_zone"]
    tenant = account["tenant_id"]
    data_root = Path(cfg["data_root"]) / "accounts" / account["label"] / "raw" / "db"
    extract_events = data_root / "activity_events.jsonl"
    extract_sessions = data_root / "session_history.jsonl"
    for p in (extract_events, extract_sessions):
        if not p.exists():
            fail("mentor extract missing: " + str(p))

    metrics, origin = load_mentor(mentor_dir)
    start, end = metrics.week_bounds(args.week, tz)
    start_utc = start.astimezone(dt.timezone.utc)
    end_utc = end.astimezone(dt.timezone.utc)
    print("account %s (tenant %s, %s)  week %s = %s .. %s" % (account["label"], tenant, tz, args.week,
                                                             start_utc.isoformat(), end_utc.isoformat()))

    # The library, built from this checkout's source and run, every time (finding F-03). run_library builds
    # before it runs and returns the digest of the dll that answered, so the provenance line below names the
    # code that produced THIS figure and nothing else.
    provenance = library_provenance()
    connection = read_connection(args)
    out_path = Path(os.environ.get("TEMP", ".")) / ("throttle-library-%s-%s.json" % (account["label"], args.week))
    library, provenance["dll_sha256"] = run_library(tenant, start_utc, end_utc, connection, out_path)

    mentor = mentor_side(metrics, origin, account, tz, extract_events, start, end, args.break_predicate)
    raw = raw_side(extract_events, extract_sessions, start, end)

    predicate = predicate_from_source()
    window_expected = (start_utc.strftime("%Y-%m-%dT%H:%M:%SZ"), end_utc.strftime("%Y-%m-%dT%H:%M:%SZ"))
    diffs = compare(library, mentor, raw, predicate, window_expected)

    lines = []
    lines.append("# Your Throttle conformance - %s, %s" % (account["label"], args.week))
    lines.append("")
    lines.append("Window: %s to %s (%s, Monday to Monday)." % (start_utc.isoformat(), end_utc.isoformat(), tz))
    lines.append("")
    lines.append("Library provenance: built from source this run; product checkout commit %s%s; dll sha256 %s."
                 % (provenance["commit"], " (DIRTY - uncommitted changes under the library's source)" if provenance["dirty"] else "",
                    provenance["dll_sha256"]))
    lines.append("")
    lines.append("| figure | library (Gateway code over the hosted database) | mentor side (harness reader over its extract) |")
    lines.append("|---|---:|---:|")
    for name in ("turns", "voiceTurns", "typedTurns", "sessions"):
        lines.append("| %s | %s | %s |" % (name, library[name], mentor[name]))
    head = library.get("headline") or {}
    lines.append("| spoken share (the library's headline percent against the independent division) | %s%% | %s |"
                 % ((head.get("voice") or {}).get("percent"), share(mentor["voiceTurns"], mentor["turns"])))
    lines.append("| phone share (the library's headline percent against the independent division) | %s%% | %s |"
                 % ((head.get("phone") or {}).get("percent"), share(headline_side(mentor)["phone"]["turns"], mentor["turns"])))
    lib_buckets = {b["modality"] + "/" + b["surface"]: b["turns"] for b in library["buckets"]}
    for key in sorted(set(lib_buckets) | set(mentor["buckets"])):
        lines.append("| bucket %s | %s | %s |" % (key, lib_buckets.get(key, 0), mentor["buckets"].get(key, 0)))
    for key in ("noInputOrigin", "agentDriven", "framework", "unresolved"):
        lines.append("| excluded.%s | %s | %s |" % (key, library["excluded"][key], mentor["excluded"][key]))
    lines.append("")
    lines.append("Per-agent and per-repository splits and the hourly series were compared against a plain reading of the extract; "
                 "%d agents, %d repositories, %d hours." % (len(raw["agents"]), len(raw["repos"]), len(raw["hourlyTurns"])))
    lines.append("")
    if diffs:
        lines.append("## FAIL - the consumers diverge")
        lines.append("")
        for d in diffs:
            lines.append("- " + d)
    else:
        lines.append("## PASS - every number agrees")
    if args.break_predicate:
        lines.append("")
        lines.append("(run with --break-predicate: the mentor side deliberately dropped null-send-source rows)")
    text = "\n".join(lines)
    print(text)
    if args.report:
        Path(args.report).write_text(text + "\n", encoding="utf-8")
    sys.exit(1 if diffs else 0)


if __name__ == "__main__":
    main()
