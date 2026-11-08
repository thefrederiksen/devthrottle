"""Zoho Mail admin calls dt-zoho offers: read the organisation, its domains, users and groups; and on the signed-in
account, aliases, the IMAP switch, forwarding, the vacation reply and signatures.

Only what passed in the proof of 8 November 2026 (proof\\prover-a.md, admin rows) is offered. What Zoho does that
shaped the code:

- the organisation id (zoid) is in the account's `policyId.zoid`;
- `GET .../organization/{zoid}/accounts/` with a trailing slash answers 404 URL_RULE_NOT_CONFIGURED; without it, 200;
- an alias is added and removed on the account's zuid (`.../accounts/{zuid}`), while the IMAP switch is set on its
  accountId (`.../accounts/{accountId}`), each with `zuid` in the body;
- a forward is added as UNCONFIRMED: Zoho mails the target to confirm, and forwards nothing until it does;
- a vacation reply with `sendTo: "org"`, a documented value, answers 500 at both the user and the admin address;
  `sendTo: "all"` works. The stored window keeps only the day, not the time;
- switching IMAP on shows in the account's answer only after 1.4 to 2 seconds (switching off shows at once; run 8
  November 2026), so its read-back waits, up to READ_BACK_TRIES reads half a second apart;
- domains are read only: dt-zoho never adds, verifies or changes a domain or its DNS.
"""

from __future__ import annotations

import time
from datetime import date
from typing import Callable, List, Optional

from .client import ZohoClient
from .errors import DtZohoError


# --------------------------------------------------------------------------------------------- organisation


def org_id(acct: dict) -> str:
    """The organisation id of an account record (mail.account_record)."""
    zoid = acct.get("zoid")
    if zoid is None:
        raise DtZohoError("Zoho's account answer carries no organisation id (policyId.zoid), which every admin call "
                          "needs.")
    return str(zoid)


def organisation(client: ZohoClient, zoid: str) -> dict:
    raw = client.mail("GET", f"/api/organization/{zoid}")["data"]
    return {"id": zoid, "name": raw.get("orgName"), "plan": raw.get("planType"),
            "primary_domain": raw.get("primaryDomainName"), "mailboxes": raw.get("mailboxCount"),
            "licences": raw.get("licenseCount"), "users": raw.get("usersCount"), "groups": raw.get("groupCount"),
            "super_admin": raw.get("superAdminFullName")}


def domains(client: ZohoClient, zoid: str) -> List[dict]:
    data = client.mail("GET", f"/api/organization/{zoid}/domains")["data"]
    found = data.get("domainVO") or [] if isinstance(data, dict) else data or []
    return [{"name": d.get("domainName"), "primary": bool(d.get("primary")), "verified": bool(d.get("verificationStatus")),
             "mx": d.get("mxstatus"), "spf": bool(d.get("spfstatus")), "dkim": bool(d.get("dkimstatus")),
             "domain_alias": bool(d.get("isDomainAlias"))} for d in found]


def users(client: ZohoClient, zoid: str) -> List[dict]:
    # No trailing slash: with one Zoho answers 404 URL_RULE_NOT_CONFIGURED.
    found = client.mail("GET", f"/api/organization/{zoid}/accounts")["data"] or []
    return [{"email": u.get("primaryEmailAddress") or u.get("mailboxAddress"), "name": u.get("displayName"),
             "role": u.get("role"), "mailbox": u.get("mailboxStatus"), "imap_enabled": u.get("imapAccessEnabled"),
             "aliases": [a.get("mailId") for a in u.get("emailAddress") or [] if a.get("isAlias") and a.get("mailId")],
             "zuid": str(u.get("zuid")) if u.get("zuid") is not None else None,
             "account_id": str(u.get("accountId")) if u.get("accountId") is not None else None} for u in found]


def groups(client: ZohoClient, zoid: str) -> List[dict]:
    data = client.mail("GET", f"/api/organization/{zoid}/groups")["data"] or {}
    # The proof's organisation had no group, so only the list's own keys are known; a group's fields are passed on.
    return [{"name": g.get("name") or g.get("groupName"), "email": g.get("emailId") or g.get("mailId"),
             "id": str(g.get("zgid")) if g.get("zgid") is not None else None} for g in data.get("groups") or []]


# --------------------------------------------------------------------------------------------- the account's own


def _admin_account(client: ZohoClient, zoid: str, key: str, body: dict) -> None:
    client.mail("PUT", f"/api/organization/{zoid}/accounts/{key}", json_body=body)


def add_alias(client: ZohoClient, zoid: str, zuid: str, alias: str) -> None:
    _admin_account(client, zoid, zuid, {"zuid": zuid, "mode": "addEmailAlias", "emailAlias": [alias]})


def remove_alias(client: ZohoClient, zoid: str, zuid: str, alias: str) -> None:
    _admin_account(client, zoid, zuid, {"zuid": zuid, "mode": "deleteEmailAlias", "emailAlias": [alias]})


def set_imap(client: ZohoClient, zoid: str, account_id: str, zuid: str, enabled: bool) -> None:
    _admin_account(client, zoid, account_id, {"zuid": zuid, "mode": "updateIMAPStatus", "imapAccessEnabled": enabled})


READ_BACK_TRIES = 30
READ_BACK_PAUSE_SECONDS = 0.5
_sleep: Callable[[float], None] = time.sleep


def read_back(read: Callable[[], object], wanted: object) -> object:
    """Read until Zoho's answer shows `wanted`, at most READ_BACK_TRIES times; the last value read is returned, so the
    caller reports a change that never showed."""
    value = read()
    for _ in range(READ_BACK_TRIES - 1):
        if value == wanted:
            break
        _sleep(READ_BACK_PAUSE_SECONDS)
        value = read()
    return value


def account_settings(client: ZohoClient, account_id: str) -> dict:
    return client.mail("GET", f"/api/accounts/{account_id}")["data"]


def forwards(settings: dict) -> List[dict]:
    return [{"to": f.get("mailForwardTo"), "status": f.get("status"), "type": f.get("type")}
            for f in settings.get("mailForward") or []]


def add_forward(client: ZohoClient, account_id: str, target: str) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}", json_body={
        "mode": "addMailForward", "isUserForward": True, "mailForward": [{"mailForwardTo": target}]})


def remove_forward(client: ZohoClient, account_id: str, target: str) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}", json_body={
        "mode": "deleteMailForward", "mailForward": [{"mailForwardTo": target}]})


# ----- the vacation reply

VACATION_SEND_TO = "all"   # "org", a documented value, answers 500 (proof, 8 November 2026)


def vacation(settings: dict) -> Optional[dict]:
    found = settings.get("vacationResponse") or []
    if not found:
        return None
    raw = found[0]
    return {"id": str(raw.get("vacationId")), "subject": raw.get("subject"), "content": raw.get("content"),
            "from": _iso_day(raw.get("fromDate")), "to": _iso_day(raw.get("toDate")),
            "replies_to": "everyone" if raw.get("sendTo") == 0 else raw.get("sendTo")}


def _iso_day(value: Optional[str]) -> Optional[str]:
    """Zoho's MM/DD/YYYY as YYYY-MM-DD."""
    if not value:
        return None
    month, day, year = value.split(" ")[0].split("/")
    return f"{year}-{month}-{day}"


def vacation_body(subject: str, content: str, first: date, last: date) -> dict:
    return {"subject": subject, "content": content, "sendTo": VACATION_SEND_TO, "sendingInt": 0,
            "fromDate": first.strftime("%m/%d/%Y") + " 00:00:00", "toDate": last.strftime("%m/%d/%Y") + " 23:59:59"}


def set_vacation(client: ZohoClient, account_id: str, body: dict, exists: bool) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}", json_body={
        "mode": "updateVacationReply" if exists else "addVacationReply", "vacationResponse": body})


def remove_vacation(client: ZohoClient, account_id: str) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}", json_body={"mode": "deleteVacationReply"})


# ----- signatures


def signature_record(raw: dict) -> dict:
    return {"id": str(raw.get("id")), "name": raw.get("name"), "content": raw.get("content"),
            "position": raw.get("position")}


def signatures(client: ZohoClient) -> List[dict]:
    return [signature_record(s) for s in client.mail("GET", "/api/accounts/signature")["data"] or []]


def find_signature(client: ZohoClient, sig_id: str) -> dict:
    for record in signatures(client):
        if record["id"] == sig_id:
            return record
    raise DtZohoError(f"no signature {sig_id}. See them with: dt-zoho signature list", exit_code=2)


def add_signature(client: ZohoClient, name: str, content: str, position: int) -> dict:
    return signature_record(client.mail("POST", "/api/accounts/signature", json_body={
        "name": name, "content": content, "position": position})["data"])


def change_signature(client: ZohoClient, sig_id: str, name: str, content: str, position: int) -> dict:
    return signature_record(client.mail("PUT", "/api/accounts/signature", json_body={
        "id": sig_id, "name": name, "content": content, "position": position})["data"])


def delete_signature(client: ZohoClient, sig_id: str) -> None:
    client.mail("DELETE", "/api/accounts/signature", query={"id": sig_id})
