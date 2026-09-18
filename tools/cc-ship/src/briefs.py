"""Briefs for the sessions cc-ship spawns. A brief is a FILE in the run folder;
the session is only told where it is."""

from __future__ import annotations

import json
from pathlib import Path

from .contracts import ACTIONS, REVIEW_EXAMPLE

DONE_COMMAND = 'cc-devthrottle session done --reason "cc-ship {role} finished"'


def finish_steps(output: Path, role: str, indent: str = "    ") -> str:
    """How every spawned session ends: flag itself done, THEN write its done marker."""
    marker = output.with_name(output.name + ".done")
    return (f"{indent}{DONE_COMMAND.format(role=role)}\n\n"
            f"Only if that command succeeded, write the single word done to this file, "
            f"and then stop:\n\n{indent}{marker}")


def reviewer_brief(
    *,
    repo: Path,
    base: str,
    head: str,
    intent: Path,
    diff: Path,
    decisions: list[dict],
    output: Path,
    repo_rules: list[str],
    first_reviewed_head: str | None,
) -> str:
    rules = "\n".join(f"- {r}" for r in repo_rules) or "- (none declared)"
    def describe(d: dict) -> str:
        note = f" (owner: {d['note']})" if d["note"] else ""
        return (f"  - {d['id']}: \"{d['title']}\" in {d['file'] or '(whole change)'}: "
                f"{d['sequence']}{note}")

    closed = [d for d in decisions if d["decision"] in ("keep", "drop")]
    to_fix = [d for d in decisions if d["decision"] == "fix"]
    parts = []
    if closed:
        parts.append(
            "- CLOSED by the owner (he kept or dropped these). Do not raise them again. Only "
            "when you are CERTAIN a finding is the same failing behaviour as one of them may "
            "you report it with \"same_as_decision\" set to its id (for example \"D1\"); it "
            "is then recorded, not asked again. If you are not certain it is the same, report "
            "it normally without that field, so the owner can see it:\n"
            + "\n".join(describe(d) for d in closed))
    if to_fix:
        parts.append(
            "- The owner said these MUST BE FIXED. Check that each one really is fixed; if it "
            "is not, report it again as a finding (never with same_as_decision):\n"
            + "\n".join(describe(d) for d in to_fix))
    decisions_line = "\n".join(parts) or "- There are no recorded owner decisions on this branch yet."
    rereview = ""
    if first_reviewed_head:
        rereview = f"""
## This is a RE-REVIEW

- Every commit after {first_reviewed_head} is UNREVIEWED code. Review it as such.
- Earlier findings and the author's fix summaries are claims, not evidence. A test
  written in the same round as its code is part of that claim.
- If new defects sit in fix-round code that went beyond what a finding needed, raise ONE
  ask-owner finding recommending that round be reverted.
"""
    return f"""# cc-ship review brief

You are the REVIEWER of a finished change. You were not there when it was written.
Do not trust the author. You never edit, commit or push anything in the repository.
Your whole output is ONE file.

## What to read

- The repository, checked out at {repo} (you are in it). The change is `git diff {base}..{head}`.
- The same diff, saved: {diff}
- The owner's intent, in his words: {intent}. This is what the change is FOR.
{decisions_line}
{rereview}
## How to review

1. For new or changed logic, build at least one concrete input and trace it by hand,
   looking for a wrong result that raises no error.
2. Report a finding ONLY with a real sequence that happens in intended use. No
   hypothetical paths.
3. Simplification pass: list every branch, flag, fallback, alias or duplicated rule the
   change adds, in "simplification". Anything the intent does not need is ALSO a finding
   whose remedy is removal.
4. Give every finding an action, one of: {", ".join(ACTIONS)}.
   - auto-fix: correctness, error handling, security or performance, fixable without
     asking what the owner meant.
   - ask-owner: product behaviour; challenges the intent; or the smallest honest fix would
     grow the change (new stored state, schema change, retry or persistence machinery, a
     new subsystem). When in doubt, ask-owner.
   - note: information only.
5. Say what you checked ("checked") and what those checks do not cover ("not_covered").
6. Give "risk_level" (low, medium or high) and a one-sentence "risk_reason".

## This repository's rules

{rules}

## Your output

Write valid JSON to this exact path, and nowhere else:

    {output}

It must have exactly this shape (the values here are placeholders; "findings" is [] when
there are none; "line" may be null):

```json
{json.dumps(REVIEW_EXAMPLE, indent=2)}
```

Use ASCII only. Then run this command:

{finish_steps(output, "reviewer")}
"""


def correction_brief(output: Path, problems: list[str], attempt: int, role: str) -> str:
    listed = "\n".join(f"- {p}" for p in problems)
    keep = "your findings" if role == "reviewer" else "your scenarios and evidence"
    return f"""# cc-ship: your {output.name} is invalid (correction {attempt} of 2)

The file {output} does not match the required shape:

{listed}

Rewrite {output} so that every problem above is gone, keeping {keep}. Then run:

{finish_steps(output, role)}
"""


VERIFY_EXAMPLE = {
    "verdict": "go",
    "scenarios": [
        {
            "name": "Short name of what a user does",
            "result": "pass",
            "live": True,
            "evidence": "evidence/scenario-1.png - what it shows",
            "reason": "",
        },
        {
            "name": "A scenario you could not run",
            "result": "untested",
            "live": False,
            "evidence": "",
            "reason": "What was missing that stopped you running it.",
        },
    ],
}


def verifier_brief(
    *,
    repo: Path,
    intent: Path,
    preview_url: str | None,
    browser_state: Path | None,
    evidence_dir: Path,
    output: Path,
    docs_only: bool = False,
) -> str:
    browser_session = f"verify-{output.parent.name}"
    if docs_only:
        surface = """## The live surface

This change touches only documents. There is nothing to run live. Confirm that from the
diff (git diff origin/main...HEAD --stat). If it is true, record one scenario per changed
document as untested, not live, with the reason "Nothing to run live: documents only",
and the verdict "no-surface". If the change does contain something that runs, say so in
a scenario and use the verdict "inconclusive".
"""
    elif preview_url and browser_state:
        surface = f"""## The live surface

A preview of this exact change is deployed at:

    {preview_url}

It is behind a sign-in. The file {browser_state} holds a browser state that gets you
past it. Drive it with playwright-cli, using this browser session name. Run these
from inside {evidence_dir} (cd there first) so the browser's own files never land in
the repository:

    playwright-cli -s={browser_session} open
    playwright-cli -s={browser_session} state-load "{browser_state}"
    playwright-cli -s={browser_session} goto {preview_url}
    playwright-cli -s={browser_session} snapshot
    playwright-cli -s={browser_session} screenshot --filename "{evidence_dir}/<scenario>.png"
    playwright-cli -s={browser_session} close

If a page shows a Vercel sign-in screen instead of the site, that scenario is
untested with the reason "preview sign-in bypass did not work" - never a pass - and if
you could not get into the preview at all, the verdict is "inconclusive".
Never print, copy or move the browser state file.
"""
    else:
        surface = """## The live surface

There is no deployed preview for this change. Work out what else can be run for real
(a built app, a command line), and run that. If nothing can be run, every scenario is
untested with the reason, and the verdict is "no-surface".
"""
    return f"""# cc-ship verify brief

You are the VERIFIER of a finished change. You did not write it and you do not trust
its author's report. You never edit, commit or push anything in {repo}.

## What the change is for

Read the owner's intent: {intent}. Derive a few named scenarios from it - things a user
actually does - and try each one on the real product.

{surface}
## Rules

- A "pass" or "fail" needs evidence: a screenshot or a short quoted output. Put files in
  {evidence_dir} and name them in "evidence".
- "untested" is honest and costs nothing. A guessed pass costs everything. When you did
  not run a scenario, say so, with a "reason" naming what was missing.
- "live" is true only when you drove the real product for that scenario.
- Verdict: any fail means "no-go"; "no-surface" only when there is genuinely nothing to
  run and every scenario is untested and not live; "inconclusive" when a surface exists
  but you could not drive it, or you ran things but cannot tell; "go" only when at least
  one scenario passed live and none failed.

## Your output

Write valid JSON to this exact path:

    {output}

in exactly this shape (placeholder values):

```json
{json.dumps(VERIFY_EXAMPLE, indent=2)}
```

Use ASCII only. Then run this command:

{finish_steps(output, "verifier")}
"""
