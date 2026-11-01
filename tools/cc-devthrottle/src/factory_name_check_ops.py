"""`cc-devthrottle factory name-check`: every seat that says CEO, or whose name is not a role word of its factory.

The rule and every sentence are the Gateway's; nothing is decided here. The Fleet Manager reads it and flags what
it finds to the owner. The route is mapped only while factory agents are switched on, so a 404 is said in words.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The name check, folded on the Gateway.
NAME_CHECK_ROUTE = "gateway/factory/name-check"

#: What a 404 means: the switch is off, so the Gateway cannot say it in its own words.
FEATURE_OFF = (
    "factory agents are switched off for this account, so the Gateway does not serve the name check "
    "(it answered 404). A Gateway older than this command answers the same way."
)


def _reason(exc: gateway.GatewayError) -> str:
    return FEATURE_OFF if exc.status == 404 else str(exc)


def _fail(message: str) -> None:
    axi_cli.fail(message, [axi_cli.CHECK_GATEWAY])


def name_check(json_output: bool) -> None:
    """Print the Gateway's name check over every factory of the account."""
    try:
        answer = gateway.get_json(NAME_CHECK_ROUTE)
    except gateway.GatewayError as exc:
        _fail(_reason(exc))
    findings = answer.get("findings") if isinstance(answer, dict) else None
    if not isinstance(findings, list):
        # Absent is not empty: an answer with no list must never read as "every factory passes".
        axi_cli.fail("the Gateway answered with no list of findings; this command will not report that as a pass.",
                     [axi_cli.CHECK_GATEWAY])
    # The same holds for the two facts printed beside the findings: a missing count is not "0 factories checked",
    # and a missing rule is not an empty rule. A bool is not a count, though Python calls it an int.
    checked = answer.get("factoriesChecked")
    if not isinstance(checked, int) or isinstance(checked, bool):
        axi_cli.fail("the Gateway's answer does not say how many factories it checked; this command will not "
                     "report that as none.", [axi_cli.CHECK_GATEWAY])
    rule = answer.get("ruleText")
    if not isinstance(rule, str) or not rule.strip():
        axi_cli.fail("the Gateway's answer does not state the rule it checked the names against; this command "
                     "will not print findings without it.", [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {"factory": f.get("factory"), "seat": f.get("seatId"), "kind": f.get("kind"), "text": f.get("text")}
        for f in findings
    ]
    blocks = [
        axi_output.format_count(len(records)),
        f"factories-checked: {checked}",
        axi_cli.ascii_text(f"rule: {rule}"),
    ]
    if records:
        blocks.append(axi_output.render_list("findings", ["factory", "seat", "kind", "text"], records))
    elif answer.get("passText"):
        blocks.append(axi_cli.ascii_text(str(answer["passText"])))
    blocks.append(axi_output.format_help(["cc-devthrottle factory status --factory <id>"]))
    axi_output.write_blocks(sys.stdout, *blocks)
