"""Final risk: the HIGHEST of the reviewer's label and the repository's rules.
Rules can raise the label, never lower it (issue 2935, "Risk")."""

from __future__ import annotations

from dataclasses import dataclass

import config

ORDER = {"low": 0, "medium": 1, "high": 2}


@dataclass
class RiskInputs:
    reviewer_level: str
    reviewer_reason: str
    changed_files: list[str]
    added: int
    deleted: int
    fix_rounds: int
    verdict: str                  # go | no-go | inconclusive | no-surface
    any_untested: bool
    has_surface: bool             # the change has something to run
    owner_kept_errors: list[str]  # titles of error-severity findings the owner kept


def compute(inputs: RiskInputs, cfg: config.ShipConfig) -> dict:
    raised: list[tuple[str, str]] = []
    # Schema, migration, authentication, key and tenant code are declared per
    # repository in .ship.yaml risk.high_paths; a name guess misfires ("authoring").
    high_hits = [f for f in inputs.changed_files if config.matches(f, cfg.high_paths)]
    if high_hits:
        raised.append(("high", f"touches a high-risk path: {', '.join(high_hits[:5])}"))
    if inputs.verdict == "inconclusive":
        raised.append(("high", "the verifier's verdict was inconclusive"))
    if inputs.owner_kept_errors:
        raised.append(("high", "the owner kept something the reviewer flagged as an error: "
                               + "; ".join(inputs.owner_kept_errors)))
    if inputs.added + inputs.deleted > 400:
        raised.append(("medium", f"{inputs.added + inputs.deleted} changed lines (over 400)"))
    if inputs.fix_rounds > 1:
        raised.append(("medium", f"{inputs.fix_rounds} fix rounds"))
    if inputs.any_untested and inputs.has_surface:
        raised.append(("medium", "a scenario was not tested on a change that has something to run"))
    if inputs.deleted > 150:
        raised.append(("medium", f"{inputs.deleted} deleted lines (over 150)"))

    level = inputs.reviewer_level
    for rule_level, _ in raised:
        if ORDER[rule_level] > ORDER[level]:
            level = rule_level
    return {
        "level": level,
        "reviewer_level": inputs.reviewer_level,
        "reviewer_reason": inputs.reviewer_reason,
        "raised_by": [reason for rule_level, reason in raised
                      if ORDER[rule_level] > ORDER[inputs.reviewer_level]],
    }
