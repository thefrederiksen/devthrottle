"""Writes the Your Throttle contract fixtures (final inspection finding F-01, and the field inventory of F-08).

ONE WIRE OBJECT, TWO REAL CONSUMERS, ONE RENDERED ANSWER. Each fixture here is a hostile `GET /stats/data`
"throttle" object and the answer a correct consumer renders from it. The browser client (client-core's
real normalizer, then the real Cockpit and mobile pages) and the mentor report (the real throttle.py
checker, metrics.py adapter and render_report ring row) are each fed the SAME object and must print the
SAME answer - or refuse it the same way. The fixtures are hostile on purpose: the headline disagrees with
the counts and the buckets, every per-row and per-hour share disagrees with the counts beside it, and the
two page summaries disagree with the rows they summarise, so a consumer that recomputes ANYTHING prints a
different number and fails. That is the class of defect the inspector reproduced by changing one constant
in the browser normalizer while every test stayed green.

THE RENDERED ANSWER IS THE WHOLE ANSWER (fix-round finding F-01). It used to be two percentages; the report
was printing 57 per cent beside "you spoke 8" and the contract could not see it. Now it is every value a
page or the report puts in front of the reader from this object: both rings' arcs, numbers and both counts,
every surface segment's width, label, count and percent, every hour's split, every agent and repository
row's printed shares, and both tab summaries. The browser tests read these off the rendered DOM; the
report's tests read the headline part off the rendered page, the email parts and the metrics block.

Run this file to regenerate the fixtures and manifest.json (the SHA-256 of each fixture). The mentor
harness carries a copy of this directory under tools/mentor/tests/contract; its test pins the same
digests, so a fixture changed on one side and not the other is a red on the side that was not updated.

    python tools/throttle-conformance/contract/make_fixtures.py

ASCII only. Nothing here is a real person's data: the counts are the shape of the owner's 2026-W35 as the
mission recorded it, and the hostile counts are invented.
"""
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent

DEFINITION = ("The shared figure is computed over activity_events rows where EventType is turn-submitted and "
              "InputOrigin is present, in sessions a person started, grouped by the origin's modality and surface.")
# The mentor's fixture week: Toronto 2026-W35, Monday 24 August 00:00 to Monday 31 August 00:00 local.
FROM_UTC = "2026-08-24T04:00:00Z"
TO_UTC = "2026-08-31T04:00:00Z"
CHOICES = [{"days": 1, "label": "Last 24 hours"}, {"days": 7, "label": "Last 7 days"},
           {"days": 14, "label": "Last 14 days"}, {"days": 30, "label": "Last 30 days"}]
SURFACES = (("desktop", "Desktop"), ("cockpit", "Cockpit"), ("phone", "Phone"), ("unknown", "Unknown"))


def share(turns, denominator):
    if denominator == 0:
        return {"turns": turns, "share": None, "percent": None}
    fraction = turns / denominator
    return {"turns": turns, "share": fraction, "percent": int(fraction * 100.0 + 0.5)}


def remainder(denominator, turns):
    """The count on a ring's other side, HOSTILE: 38 less than the subtraction gives. A consumer that
    subtracts the count from the denominator prints 1538 for the phone ring; one that renders prints 1500."""
    return denominator - turns - 38 if denominator else 0


def surfaces(counts, denominator):
    out = []
    for surface, label in SURFACES:
        entry = share(counts.get(surface, 0), denominator)
        entry.update({"surface": surface, "label": label, "remainder": remainder(denominator, counts.get(surface, 0))})
        out.append(entry)
    return out


def headline(denominator, voice, typed, by_surface):
    phone = by_surface.get("phone", 0)
    return {
        "denominator": denominator,
        "hasData": denominator > 0,
        "voice": share(voice, denominator),
        "typed": share(typed, denominator),
        "phone": dict(share(phone, denominator), remainder=remainder(denominator, phone)),
        "surfaces": surfaces(by_surface, denominator),
    }


def row_shares(turn_share, session_share, voice_share):
    """The three finished ratios on an agent or repository row, from the fractions given (None = no data)."""
    def pair(fraction):
        return (None, None) if fraction is None else (fraction, int(fraction * 100.0 + 0.5))
    turn, turn_pct = pair(turn_share)
    session, session_pct = pair(session_share)
    voice, voice_pct = pair(voice_share)
    return {"turnShare": turn, "turnPercent": turn_pct, "sessionShare": session, "sessionPercent": session_pct,
            "voiceShare": voice, "voicePercent": voice_pct}


# THE HOSTILE ROW SHARES: one agent, one repository, one hour, each with counts saying 10 turns, 8 spoken, 3
# sessions - and shares that say something else entirely. A consumer that divides prints 100, 80, 100; one
# that renders prints 57, 61, 33.
HOSTILE_ROW = row_shares(0.5683090705487122, 1.0 / 3.0, 0.6120)
HOSTILE_HOUR = {"voiceShare": 0.5683090705487122, "typedShare": 0.4316909294512878}


def agents_summary(has_data):
    if not has_data:
        return {"agentCount": 0, "totalTurns": 0, "totalSessions": 0, "voiceTurns": 0, "voiceShare": None, "voicePercent": None,
                "topAgentName": None, "topShare": None, "topPercent": None, "agentDrivenTurns": 0, "leverage": None,
                "leverageText": None, "hasData": False}
    # Hostile: the one row says 10 turns and 3 sessions; the summary says the owner's real week.
    return {"agentCount": 3, "totalTurns": 1786, "totalSessions": 129, "voiceTurns": 1015,
            "voiceShare": 0.5683090705487122, "voicePercent": 57,
            "topAgentName": "Claude Code", "topShare": 0.9182530794, "topPercent": 92,
            "agentDrivenTurns": 4, "leverage": 0.0022396416573348264, "leverageText": "0.0x", "hasData": True}


def repos_summary(has_data):
    if not has_data:
        return {"repoCount": 0, "totalTurns": 0, "totalSessions": 0, "voiceTurns": 0, "voiceShare": None, "voicePercent": None,
                "topRepoName": None, "topShare": None, "topPercent": None, "hasData": False}
    return {"repoCount": 4, "totalTurns": 1786, "totalSessions": 129, "voiceTurns": 1015,
            "voiceShare": 0.5683090705487122, "voicePercent": 57,
            "topRepoName": "repo-alpha", "topShare": 0.7368421052631579, "topPercent": 74, "hasData": True}


# Who runs the sessions (owner's ask, 2026-09-27): the four starter groups, finished by the library. Hostile like
# the rest: its human share is served as 27 per cent, and 64 of 238 sessions rounds to 27 - but its groups' own
# session shares are served as 30, 60, 5 and 5, which is not what 64, 150, 16 and 8 of 238 round to. A consumer
# prints the served fields.
STARTERS = {
    "sessions": 238, "turns": 3020, "hasData": True, "humanShare": 64 / 238, "humanPercent": 27,
    "groups": [
        {"kind": "human", "label": "Started by you", "sessions": 64, "turns": 812, "sessionShare": 0.3, "sessionPercent": 30},
        {"kind": "agent", "label": "Run by other sessions", "sessions": 150, "turns": 2040, "sessionShare": 0.6, "sessionPercent": 60},
        {"kind": "schedule", "label": "Run by a schedule", "sessions": 16, "turns": 120, "sessionShare": 0.05, "sessionPercent": 5},
        {"kind": "notRecorded", "label": "Starter not recorded", "sessions": 8, "turns": 48, "sessionShare": 0.05, "sessionPercent": 5},
    ],
}


def wire(turns, voice, typed, buckets, head, excluded=None):
    has_data = head["hasData"]
    return {
        "definition": DEFINITION,
        "unit": "submitted turns",
        "window": {"fromUtc": FROM_UTC, "toUtc": TO_UTC, "isDefault": False,
                   "label": "2026-08-24 04:00 to 2026-08-31 04:00 UTC", "kind": "explicit",
                   "days": None, "week": None, "choices": CHOICES},
        "ledger": {"retentionDays": 30, "earliestUtc": "2026-08-06T14:17:15Z"},
        "headline": head,
        "turns": turns,
        "voiceTurns": voice,
        "typedTurns": typed,
        "sessions": 3,
        "buckets": buckets,
        "hourlyTurns": [dict({"hour": "2026-08-30T13", "turns": turns, "voiceTurns": voice, "typedTurns": typed}, **HOSTILE_HOUR)],
        "agents": [dict({"agent": "ClaudeCode", "agentName": "Claude Code", "turns": turns, "voiceTurns": voice,
                         "typedTurns": typed, "sessions": 3, "agentDrivenTurns": 4}, **HOSTILE_ROW)],
        "repos": [dict({"repo": "owner/repo-alpha", "repoName": "repo-alpha", "turns": turns, "voiceTurns": voice,
                        "typedTurns": typed, "sessions": 3, "checkouts": ["D:/repo-alpha"]}, **HOSTILE_ROW)],
        "agentsSummary": agents_summary(has_data),
        "reposSummary": repos_summary(has_data),
        "reposUnattributedTurns": 0,
        "excluded": excluded or {"noInputOrigin": 662, "agentDriven": 4, "framework": 160, "unresolved": 498,
                                 "notStartedByYou": 1204},
        "agentDrivenTurns": 4,
        "starters": STARTERS,
    }


def rendered(w):
    """What a correct consumer renders from a wire object: the fields, verbatim, nothing divided."""
    head = w["headline"]
    return {
        "denominator": head["denominator"],
        "hasData": head["hasData"],
        "voiceTurns": head["voice"]["turns"],
        "typedTurns": head["typed"]["turns"],
        "phoneTurns": head["phone"]["turns"],
        "phoneRemainder": head["phone"]["remainder"],
        "voiceShare": head["voice"]["share"],
        "phoneShare": head["phone"]["share"],
        "voicePercent": head["voice"]["percent"],
        "typedPercent": head["typed"]["percent"],
        "phonePercent": head["phone"]["percent"],
        "surfaces": [{"surface": s["surface"], "label": s["label"], "turns": s["turns"], "share": s["share"],
                      "percent": s["percent"], "remainder": s["remainder"]} for s in head["surfaces"]],
        "hourly": [{"hour": h["hour"], "turns": h["turns"], "voiceTurns": h["voiceTurns"], "typedTurns": h["typedTurns"],
                    "voiceShare": h["voiceShare"], "typedShare": h["typedShare"]} for h in w["hourlyTurns"]],
        "agents": [{"agentName": a["agentName"], "turns": a["turns"], "sessions": a["sessions"],
                    "agentDrivenTurns": a["agentDrivenTurns"], "turnShare": a["turnShare"], "turnPercent": a["turnPercent"],
                    "sessionShare": a["sessionShare"], "sessionPercent": a["sessionPercent"],
                    "voiceShare": a["voiceShare"], "voicePercent": a["voicePercent"]} for a in w["agents"]],
        "agentsSummary": dict(w["agentsSummary"]),
        "repos": [{"repoName": r["repoName"], "turns": r["turns"], "sessions": r["sessions"],
                   "turnShare": r["turnShare"], "turnPercent": r["turnPercent"],
                   "sessionShare": r["sessionShare"], "sessionPercent": r["sessionPercent"],
                   "voiceShare": r["voiceShare"], "voicePercent": r["voicePercent"]} for r in w["repos"]],
        "reposSummary": dict(w["reposSummary"]),
    }


# The recorded shape of the owner's 2026-W35: 1786 counted, 1015 spoken (57 per cent), 248 from the phone
# (14 per cent) - the headline every hostile fixture below carries.
W35_SURFACES = {"desktop": 1531, "phone": 248, "unknown": 7}
W35_HEADLINE = headline(1786, 1015, 771, W35_SURFACES)
W35_BUCKETS = [
    {"modality": "typed", "surface": "desktop", "turns": 696},
    {"modality": "typed", "surface": "phone", "turns": 68},
    {"modality": "typed", "surface": "unknown", "turns": 7},
    {"modality": "voice", "surface": "desktop", "turns": 835},
    {"modality": "voice", "surface": "phone", "turns": 180},
]

FIXTURES = [
    {
        "name": "the-headline-is-rendered-not-the-counts",
        "why": ("The counts and the buckets agree with each other and say 8 of 10 turns were spoken (80 per cent), "
                "all 8 from the phone. The headline says 1015 of 1786 (57 per cent), 248 from the phone (14 per "
                "cent), and 1500 everywhere else (not the 1538 that subtracting gives). A consumer that renders the "
                "headline prints 57, 14 and 1500 beside 1015 and 248; one that divides the counts, re-totals the "
                "buckets, or subtracts for the other side of a ring prints 80, 100 and 1538 or 2. The one agent, repository and hour say 10 turns, 8 spoken, 3 "
                "sessions, and carry shares of 57, 33 and 61 per cent; the two summaries describe a week of 1786 "
                "turns the rows do not add up to. Both consumers must print every served field."),
        "wire": wire(10, 8, 2,
                     [{"modality": "voice", "surface": "phone", "turns": 8},
                      {"modality": "typed", "surface": "desktop", "turns": 2}],
                     W35_HEADLINE),
        "expected": None,
    },
    {
        "name": "the-percent-field-is-printed-not-re-rounded",
        "why": ("The headline's shares are the real 2026-W35 fractions but its percent fields are 61 spoken and "
                "9 from the phone - not what rounding those fractions gives (57 and 14). A consumer that prints "
                "the percent field prints 61 and 9; one that rounds the share for itself prints 57 and 14. Both "
                "consumers must print 61 and 9: the rounding is the library's, done once."),
        "wire": wire(1786, 1015, 771, W35_BUCKETS, {
            **W35_HEADLINE,
            "voice": {**W35_HEADLINE["voice"], "percent": 61},
            "typed": {**W35_HEADLINE["typed"], "percent": 39},
            "phone": {**W35_HEADLINE["phone"], "percent": 9},
            # And a Cockpit surface with turns, so the report draws its third ring: 100 turns is 5.6 per cent, the
            # percent field says 7, and the other side of that ring is served 38 short of the subtraction.
            "surfaces": [dict(s, percent=9) if s["surface"] == "phone"
                         else dict(s, turns=100, share=100 / 1786, percent=7, remainder=remainder(1786, 100)) if s["surface"] == "cockpit"
                         else s for s in W35_HEADLINE["surfaces"]],
        }),
        "expected": None,
    },
    {
        "name": "the-empty-state-is-the-librarys-ruling",
        "why": ("Nothing counted: the headline says hasData false with every share and percent null, while the "
                "counts below happen to say 10 turns. Both consumers must show NO number - the browser its empty "
                "state, the report a refusal to draw a ring - and neither may print 0% or 100%."),
        "wire": wire(10, 8, 2,
                     [{"modality": "voice", "surface": "phone", "turns": 8},
                      {"modality": "typed", "surface": "desktop", "turns": 2}],
                     headline(0, 0, 0, {})),
        "expected": None,
    },
    {
        "name": "an-answer-without-a-headline-is-refused",
        "why": ("The headline block is absent and the counts are the real 2026-W35. A consumer that rebuilds the "
                "headline from the counts prints 57 - and has just become the second computation ruling R3 "
                "forbids. Both consumers must refuse the answer."),
        "wire": {k: v for k, v in wire(1786, 1015, 771, W35_BUCKETS, W35_HEADLINE).items() if k != "headline"},
        "expected": {"outcome": "refused"},
    },
    {
        "name": "a-surface-the-consumers-do-not-know-is-refused",
        "why": ("The headline lists a fifth surface, 'watch'. Neither consumer may fold it into 'unknown' or drop "
                "it; both must refuse the answer, the same way the library refuses a bucket on that surface."),
        "wire": wire(1786, 1015, 771, W35_BUCKETS, {
            **W35_HEADLINE,
            "surfaces": W35_HEADLINE["surfaces"] + [dict(share(0, 1786), surface="watch", label="Watch", remainder=1786)],
        }),
        "expected": {"outcome": "refused"},
    },
    {
        "name": "a-modality-token-the-consumers-do-not-know-is-refused",
        "why": ("A bucket carries the modality 'spoken'. The library only ever writes 'voice' and 'typed'; a "
                "consumer that maps every other token to typed (or to voice) is guessing. Both must refuse."),
        "wire": wire(1786, 1015, 771,
                     [dict(b, modality="spoken") if b["modality"] == "voice" else b for b in W35_BUCKETS],
                     W35_HEADLINE),
        "expected": {"outcome": "refused"},
    },
]
for fixture in FIXTURES:
    if fixture["expected"] is None:
        outcome = "rendered" if fixture["wire"]["headline"]["hasData"] else "empty"
        fixture["expected"] = {"outcome": outcome, "rendered": rendered(fixture["wire"])}

# ---------------------------------------------------------------- the field inventory (finding F-08)
#
# Every field on the library's answer, as the feed serves it, and which real consumer reads it. The C#
# side asserts the DTO has exactly these paths (a new DTO field must be added here on purpose); the
# browser and the report each assert that every path marked for them reaches their RENDERED output with
# the wire's value - the page's DOM, the report's page and email parts (fix-round finding F-08: the
# adapter keeping a field is proof about the adapter, not about the reader).
BOTH = ["browser", "report"]
BROWSER = ["browser"]
REPORT = ["report"]
INVENTORY = {
    "definition": BOTH,
    "unit": BOTH,
    "window.fromUtc": BOTH,
    "window.toUtc": BOTH,
    "window.isDefault": BROWSER,
    "window.label": BOTH,
    "window.kind": BROWSER,
    "window.days": BROWSER,
    "window.week": BROWSER,
    "window.choices[].days": BROWSER,
    "window.choices[].label": BROWSER,
    "ledger.retentionDays": BOTH,
    "ledger.earliestUtc": BOTH,
    "headline.denominator": BOTH,
    "headline.hasData": BOTH,
    "headline.voice.turns": BOTH,
    "headline.voice.share": BOTH,
    "headline.voice.percent": BOTH,
    "headline.typed.turns": BOTH,
    "headline.typed.share": REPORT,
    "headline.typed.percent": REPORT,
    "headline.phone.turns": BOTH,
    "headline.phone.share": BOTH,
    "headline.phone.percent": BOTH,
    "headline.phone.remainder": BOTH,
    "headline.surfaces[].surface": BOTH,
    "headline.surfaces[].label": BOTH,
    "headline.surfaces[].turns": BOTH,
    "headline.surfaces[].share": BOTH,
    "headline.surfaces[].percent": BOTH,
    "headline.surfaces[].remainder": REPORT,
    "turns": BOTH,
    "voiceTurns": BOTH,
    "typedTurns": BOTH,
    "sessions": BOTH,
    "buckets[].modality": BOTH,
    "buckets[].surface": BOTH,
    "buckets[].turns": BOTH,
    "hourlyTurns[].hour": BROWSER,
    "hourlyTurns[].turns": BROWSER,
    "hourlyTurns[].voiceTurns": BROWSER,
    "hourlyTurns[].typedTurns": BROWSER,
    "hourlyTurns[].voiceShare": BROWSER,
    "hourlyTurns[].typedShare": BROWSER,
    "agents[].agent": BROWSER,
    "agents[].agentName": BROWSER,
    "agents[].turns": BROWSER,
    "agents[].voiceTurns": BROWSER,
    "agents[].typedTurns": BROWSER,
    "agents[].sessions": BROWSER,
    "agents[].agentDrivenTurns": BROWSER,
    "agents[].turnShare": BROWSER,
    "agents[].turnPercent": BROWSER,
    "agents[].sessionShare": BROWSER,
    "agents[].sessionPercent": BROWSER,
    "agents[].voiceShare": BROWSER,
    "agents[].voicePercent": BROWSER,
    "agentsSummary.agentCount": BROWSER,
    "agentsSummary.totalTurns": BROWSER,
    "agentsSummary.totalSessions": BROWSER,
    "agentsSummary.voiceTurns": BROWSER,
    "agentsSummary.voiceShare": BROWSER,
    "agentsSummary.voicePercent": BROWSER,
    "agentsSummary.topAgentName": BROWSER,
    "agentsSummary.topShare": BROWSER,
    "agentsSummary.topPercent": BROWSER,
    "agentsSummary.agentDrivenTurns": BROWSER,
    "agentsSummary.leverage": BROWSER,
    "agentsSummary.leverageText": BROWSER,
    "agentsSummary.hasData": BROWSER,
    "repos[].repo": BROWSER,
    "repos[].repoName": BROWSER,
    "repos[].turns": BROWSER,
    "repos[].voiceTurns": BROWSER,
    "repos[].typedTurns": BROWSER,
    "repos[].sessions": BROWSER,
    "repos[].checkouts[]": BROWSER,
    "repos[].turnShare": BROWSER,
    "repos[].turnPercent": BROWSER,
    "repos[].sessionShare": BROWSER,
    "repos[].sessionPercent": BROWSER,
    "repos[].voiceShare": BROWSER,
    "repos[].voicePercent": BROWSER,
    "reposSummary.repoCount": BROWSER,
    "reposSummary.totalTurns": BROWSER,
    "reposSummary.totalSessions": BROWSER,
    "reposSummary.voiceTurns": BROWSER,
    "reposSummary.voiceShare": BROWSER,
    "reposSummary.voicePercent": BROWSER,
    "reposSummary.topRepoName": BROWSER,
    "reposSummary.topShare": BROWSER,
    "reposSummary.topPercent": BROWSER,
    "reposSummary.hasData": BROWSER,
    "reposUnattributedTurns": BROWSER,
    "excluded.noInputOrigin": BOTH,
    "excluded.agentDriven": BOTH,
    "excluded.framework": BOTH,
    "excluded.unresolved": BOTH,
    "excluded.notStartedByYou": BROWSER,
    "agentDrivenTurns": BOTH,
    "starters.sessions": BROWSER,
    "starters.turns": BROWSER,
    "starters.hasData": BROWSER,
    "starters.humanShare": BROWSER,
    "starters.humanPercent": BROWSER,
    "starters.groups[].kind": BROWSER,
    "starters.groups[].label": BROWSER,
    "starters.groups[].sessions": BROWSER,
    "starters.groups[].turns": BROWSER,
    "starters.groups[].sessionShare": BROWSER,
    "starters.groups[].sessionPercent": BROWSER,
}


def dump(path, value):
    """Write one file with LF endings and return the digest of that LF text. Every reader of manifest.json
    normalises CRLF to LF before digesting, because git writes CRLF into a fresh Windows checkout."""
    text = json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n"
    path.write_text(text, encoding="ascii", newline="\n")
    return hashlib.sha256(text.encode("ascii")).hexdigest()


def main():
    manifest = {"fixtures": {}, "inventory": None}
    for fixture in FIXTURES:
        name = fixture["name"] + ".json"
        manifest["fixtures"][name] = dump(HERE / name, fixture)
    manifest["inventory"] = dump(HERE / "field-inventory.json", {"fields": INVENTORY})
    dump(HERE / "manifest.json", manifest)
    print("wrote " + str(len(FIXTURES)) + " fixtures, the field inventory and manifest.json to " + str(HERE))


if __name__ == "__main__":
    main()
