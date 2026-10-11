"""Secret transfers and the owner's approval of them, as the Gateway holds them (the Secret Handoff mission).

The Gateway is where a transfer waits for its one answer. It holds names, machines, the reason, who answered and where,
and how it ended - never a value and never an envelope - and it folds the sentences every surface shows (the summary,
the status, whether it can still be answered), so this module shows them as they come rather than deciding what a state
means.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List, Optional

from . import gateway_link

ROUTE = "gateway/secrets/transfers"

# A transfer id as the Gateway mints it. Anything else is refused before it is put into a path.
ID_SHAPE = re.compile(r"^[0-9a-f]{32}$")

WAITING = "waiting"

# How long `send` and `request` wait for a transfer to end: the fifteen minutes it can wait for an answer, and two
# more for the delivery that follows an approval given at the last moment.
WAIT_LIMIT_SECONDS = 17 * 60


@dataclass(frozen=True)
class Transfer:
    transfer_id: str
    entry: str
    target_name: str
    from_machine: str
    to_machine: str
    replace: bool
    asked_by: str
    reason: str
    state: str
    expires_utc: str
    answered_where: str
    approval_words: str
    outcome: str
    summary: str
    replace_note: str
    status_text: str
    can_answer: bool

    @staticmethod
    def from_dto(row: dict) -> "Transfer":
        def text(key: str) -> str:
            value = row.get(key)
            return "" if value is None else str(value)

        return Transfer(
            transfer_id=text("transferId"), entry=text("entry"), target_name=text("targetName"),
            from_machine=text("fromMachine"), to_machine=text("toMachine"), replace=bool(row.get("replace")),
            asked_by=text("askedBy"), reason=text("reason"), state=text("state"), expires_utc=text("expiresAtUtc"),
            answered_where=text("answeredWhere"), approval_words=text("approvalWords"), outcome=text("outcome"),
            summary=text("summary"), replace_note=text("replaceNote"), status_text=text("statusText"),
            can_answer=bool(row.get("canAnswer")))


def listed(link: Optional[gateway_link.Link] = None) -> List[Transfer]:
    """The transfers waiting for an answer and those that ended recently, newest first."""
    answer = gateway_link.get(ROUTE, link)
    rows = answer.get("transfers") if isinstance(answer, dict) else None
    if not isinstance(rows, list):
        raise gateway_link.GatewayShapeError("the Gateway's list of transfers has no 'transfers' list")
    return [Transfer.from_dto(row) for row in rows if isinstance(row, dict)]


def create(body: dict, link: Optional[gateway_link.Link] = None) -> Transfer:
    """Ask for a transfer. The Gateway answers with it waiting, or already approved when the owner's approval came
    with the asking."""
    return _one(gateway_link.post(ROUTE, body, link))


def find(transfer_id: str, link: Optional[gateway_link.Link] = None) -> Transfer:
    return _one(gateway_link.get(f"{ROUTE}/{_segment(transfer_id)}", link))


def answer(transfer_id: str, approve: bool, where: Optional[str], owner_approved: Optional[str],
           link: Optional[gateway_link.Link] = None) -> tuple:
    """Give the one answer. Returns (transfer, note). A transfer someone already answered, or that expired, is a
    GatewayRefusal with the Gateway's own sentence."""
    body = {"approve": approve, "deny": not approve}
    if where:
        body["where"] = where
    if owner_approved is not None:
        body["ownerApproved"] = owner_approved
    response = gateway_link.post(f"{ROUTE}/{_segment(transfer_id)}/answer", body, link)
    return _one(response), str(response.get("note") or "")


def _segment(transfer_id: str) -> str:
    from cc_shared.gateway import path_segment

    return path_segment(transfer_id)


def _one(response) -> Transfer:
    row = response.get("transfer") if isinstance(response, dict) else None
    if not isinstance(row, dict):
        raise gateway_link.GatewayShapeError("the Gateway's answer has no 'transfer'")
    return Transfer.from_dto(row)
