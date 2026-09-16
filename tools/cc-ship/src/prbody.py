"""The pull request text: judgeable in one screen, long material linked, not pasted."""

from __future__ import annotations

import json

from text import ascii_safe

INTENT_LINES = 30
ATTESTATION_PREFIX = "<!-- ship-attestation:v1 "


def _cell(value: str) -> str:
    return ascii_safe(value).replace("|", "/").replace("\n", " ").strip()


def testing_section(verify: dict | None, step_state: str, checks: list[str],
                    checks_state: str, links: dict[str, str]) -> list[str]:
    lines = ["## Testing"]
    if step_state == "skipped" or verify is None:
        lines.append("Verdict: SKIPPED - no verifier ran (verify.surface is 'skip' in .ship.yaml)")
    else:
        scenarios = verify["scenarios"]
        live = sum(1 for s in scenarios if s["live"])
        if verify["verdict"] == "no-surface" and verify.get("docs_only"):
            lines.append("Verdict: NO-SURFACE - Nothing to run live: documents only")
        else:
            lines.append(f"Verdict: {verify['verdict'].upper()} - {live} of {len(scenarios)} "
                         "scenarios run live")
        lines += ["", "| Scenario | Result | Live | Evidence |", "|---|---|---|---|"]
        for s in scenarios:
            evidence = s["evidence"] if s["result"] != "untested" else f"Not run: {s['reason']}"
            evidence = _cell(evidence)
            for name, url in links.items():
                if name in evidence:
                    evidence = evidence.replace(name, f"[{name}]({url})")
            lines.append(f"| {_cell(s['name'])} | {s['result'].upper()} | "
                         f"{'yes' if s['live'] else 'no'} | {evidence} |")
    lines.append("")
    if checks_state == "skipped":
        lines.append("Local checks: SKIPPED - .ship.yaml on main declares none")
    else:
        lines.append(f"Local checks: {', '.join(f'`{c}`' for c in checks)} (passed)")
    return lines


def review_section(run: dict, owner_decisions: list[dict]) -> list[str]:
    history = run["history"]
    found = sum(h["found"] for h in history)
    fixed = sum(len(h["fixed_next_round"]) for h in history)
    kept = [d for d in owner_decisions if d["decision"] == "keep"]
    rounds = len(history)
    agents = sorted({h["agent"] for h in history})
    lines = ["## Review",
             f"Reviewed by {', '.join(agents)} in {rounds} round{'s' if rounds != 1 else ''}. "
             f"Found {found}, fixed {fixed}, owner kept {len(kept)}."]
    for h in history:
        for title in h["fixed_next_round"]:
            lines.append(f"- r{h['round']} fixed: {_cell(title)}")
        for note in h["notes"]:
            lines.append(f"- r{h['round']} note: {_cell(note)}")
    for d in owner_decisions:
        label = {"keep": "OWNER KEPT", "drop": "OWNER DROPPED", "fix": "OWNER SAID FIX"}[d["decision"]]
        note = f' (owner: "{_cell(d["note"])}")' if d["note"] else ""
        lines.append(f"- {label}: {_cell(d['title'])}{note}")
    lines.append("Pre-answered by mission brief: none")
    return lines


def attestation(head: str, steps: dict, verdict: str | None, risk: str) -> str:
    payload = {"head": head, "steps": steps, "verdict": verdict, "risk": risk}
    return f"{ATTESTATION_PREFIX}{json.dumps(payload, separators=(',', ':'))} -->"


def build(*, run: dict, intent: str, changed: list[str], added: int, deleted: int,
          subjects: list[str], checks: list[str], owner_decisions: list[dict],
          links: dict[str, str], head: str) -> str:
    intent_lines = ascii_safe(intent).strip().splitlines()
    if intent_lines and intent_lines[0].lstrip().startswith("# "):
        intent_lines = intent_lines[1:]
    if len(intent_lines) > INTENT_LINES:
        intent_lines = intent_lines[:INTENT_LINES] + ["", "(intent shortened; full text in the run folder)"]
    risk = run["risk"]
    lines = ["## Intent", *[line.rstrip() for line in intent_lines], "",
             "## What changed",
             f"{len(changed)} files, +{added} -{deleted}."]
    lines += [f"- {_cell(s)}" for s in subjects]
    shown = changed[:15]
    lines.append("Files: " + ", ".join(f"`{_cell(f)}`" for f in shown)
                 + (f" and {len(changed) - len(shown)} more" if len(changed) > len(shown) else ""))
    lines.append("")
    lines += testing_section(run["verify"], run["steps"]["verify"], checks,
                             run["steps"]["checks"], links)
    lines.append("")
    lines += review_section(run, owner_decisions)
    lines.append("")
    lines.append(f"## Risk: {risk['level'].upper()}")
    reason = f"Reviewer said {risk['reviewer_level']}: {_cell(risk['reviewer_reason'])}"
    lines.append(reason)
    for raised in risk["raised_by"]:
        lines.append(f"Raised by rule: {_cell(raised)}")
    skipped = [step for step, state in run["steps"].items() if state != "completed"]
    if skipped:
        lines.append(f"SKIPPED steps: {', '.join(skipped)} - this pull request cannot merge automatically.")
    lines.append("")
    lines.append(attestation(head, run["steps"], (run["verify"] or {}).get("verdict"), risk["level"]))
    return "\n".join(lines) + "\n"
