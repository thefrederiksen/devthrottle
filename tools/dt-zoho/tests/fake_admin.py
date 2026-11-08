"""The admin part of the in-memory Zoho: organisation, domains, users, groups, and the account's aliases, IMAP switch,
forwards, vacation reply and signatures.

Its answers copy the shapes Zoho gave (proof\\prover-a\\evidence\\zoho-admin-batch.txt, zoho-alias.txt,
zoho-mail-batch2.txt and zoho-mail-batch3.txt, and the read-only probe of 8 November 2026):
- the organisation id sits in the account's `policyId.zoid`;
- `.../organization/{zoid}/accounts/` with a trailing slash answers 404 URL_RULE_NOT_CONFIGURED;
- aliases change on `.../accounts/{zuid}`, the IMAP switch on `.../accounts/{accountId}`, each with zuid in the body;
- a forward reads back as `{"mailForwardTo", "type": "USER", "status": "UNCONFIRMED"}`, and the key is absent when
  there is none;
- a vacation reply with `sendTo: "org"` answers 500; the stored days are MM/DD/YYYY with no time, `sendTo` 0;
- a signature answers `{"name", "signatureType", "id", "position", "content"}`.

INVENTED, not seen at Zoho: the error codes DOMAIN_NOT_IN_ORG, INVALID_ZUID, INVALID_ACCOUNT and VACATION_STATE.
The proof never sent Zoho those mistakes, so a test that meets one proves dt-zoho's handling of an error answer,
not what Zoho would say.
"""

from __future__ import annotations

import json
from typing import Callable, List, Optional

from src.transport import Response

ZOID = "110003647476"
ZUID = "110003647767"


def _ok(data=None, code=200) -> Response:
    body = {"status": {"code": code, "description": "success"}}
    if data is not None:
        body["data"] = data
    return Response(200, json.dumps(body).encode())


def _error(status: int, code: str) -> Response:
    return Response(status, json.dumps({"data": {"errorCode": code},
                                        "status": {"code": status, "description": "Invalid Input"}}).encode())


class FakeAdmin:
    def __init__(self, account: dict, new_id: Callable[[], str]):
        self.account = account              # the fake's one account record, shared with FakeZoho
        self.account["policyId"] = {"675000002144921": "Business Policy", "zoid": int(ZOID)}
        self._new_id = new_id
        self.forwards: List[dict] = []
        self.vacation: Optional[dict] = None
        self.signatures: List[dict] = []
        self.requests: List[tuple] = []     # (method, path, query, body) of every admin call
        # As Zoho did on 8 November 2026: after IMAP is switched on, the account answer shows the old value for a
        # few reads (1.4 to 2 seconds).
        self.imap_on_lag_reads = 3
        self._stale_imap_reads = 0
        self.org_domains = ["devthrottle.com", "example.com"]
        self.other_user = {"primaryEmailAddress": "email@devthrottle.com", "mailboxAddress": "email@devthrottle.com",
                           "displayName": "DevThrottle", "role": "member", "mailboxStatus": "enabled",
                           "imapAccessEnabled": True, "zuid": 110003647863, "accountId": "627096000000002002",
                           "emailAddress": [{"isAlias": False, "isPrimary": True, "mailId": "email@devthrottle.com"}]}

    # ----- routing

    def handle(self, method: str, path: str, query: dict, raw: Optional[bytes]) -> Optional[Response]:
        """The admin calls; None when the path is not one of them."""
        account_id = str(self.account["accountId"])
        org = f"/api/organization/{ZOID}"
        if path not in ("/api/accounts/signature", f"/api/accounts/{account_id}") and not path.startswith(org):
            return None
        body = json.loads(raw.decode()) if raw else {}
        self.requests.append((method, path, query, body))
        if path == "/api/accounts/signature":
            return self._signature(method, query, body)
        if path == f"/api/accounts/{account_id}":
            if method == "GET":
                return _ok(self._settings())
            if method == "PUT":
                return self._user_put(body)
        if method not in ("GET", "PUT"):
            return None
        rest = path[len(org):]
        if method == "GET":
            if rest == "":
                return _ok({"orgName": "DevThrottle", "planType": "paid", "primaryDomainName": "devthrottle.com",
                            "mailboxCount": 2, "licenseCount": 2, "usersCount": 2, "groupCount": 0,
                            "superAdminFullName": "Soren Frederiksen", "zoid": int(ZOID)})
            if rest == "/domains":
                return _ok({"domainVO": [
                    {"domainName": d, "primary": i == 0, "verificationStatus": True, "mxstatus": "enabled",
                     "spfstatus": True, "dkimstatus": i == 0, "isDomainAlias": False}
                    for i, d in enumerate(self.org_domains)]})
            if rest == "/accounts":
                return _ok([self.other_user, dict(self.account, primaryEmailAddress="soren@devthrottle.com",
                                                  displayName="Soren Frederiksen", role="super_admin",
                                                  mailboxStatus="enabled")])
            if rest == "/accounts/":
                return _error(404, "URL_RULE_NOT_CONFIGURED")
            if rest == "/groups":
                return _ok({"count": 0, "groups": [], "domains": list(self.org_domains)})
            return _error(404, "URL_RULE_NOT_CONFIGURED")
        return self._admin_put(rest, body)

    # ----- the account's settings

    def account_answer(self) -> dict:
        """The account as `GET /api/accounts` shows it, IMAP lag included."""
        if self._stale_imap_reads:
            self._stale_imap_reads -= 1
            return dict(self.account, imapAccessEnabled=False)
        return self.account

    def _settings(self) -> dict:
        data = {k: v for k, v in self.account.items() if k != "policyId"}
        if self.forwards:
            data["mailForward"] = [dict(f) for f in self.forwards]
        if self.vacation:
            data["vacationResponse"] = [dict(self.vacation)]
        return data

    def _admin_put(self, rest: str, body: dict) -> Response:
        mode = body.get("mode")
        key = rest[len("/accounts/"):] if rest.startswith("/accounts/") else None
        if str(body.get("zuid")) != ZUID:
            return _error(400, "INVALID_ZUID")
        if mode in ("addEmailAlias", "deleteEmailAlias"):
            if key != ZUID:
                return _error(404, "INVALID_ACCOUNT")
            for alias in body["emailAlias"]:
                if mode == "addEmailAlias":
                    if alias.split("@")[-1] not in self.org_domains:
                        return _error(400, "DOMAIN_NOT_IN_ORG")
                    self.account["emailAddress"].append({"isAlias": True, "isPrimary": False, "mailId": alias,
                                                         "isConfirmed": True})
                else:
                    self.account["emailAddress"] = [a for a in self.account["emailAddress"] if a["mailId"] != alias]
            return _ok()
        if mode == "updateIMAPStatus":
            if key != str(self.account["accountId"]):
                return _error(404, "INVALID_ACCOUNT")
            self.account["imapAccessEnabled"] = bool(body["imapAccessEnabled"])
            self._stale_imap_reads = self.imap_on_lag_reads if body["imapAccessEnabled"] else 0
            return _ok()
        raise AssertionError(f"the fake Zoho does not know the admin mode {mode}")

    def _user_put(self, body: dict) -> Response:
        mode = body.get("mode")
        if mode == "addMailForward":
            for f in body["mailForward"]:
                self.forwards.append({"mailForwardTo": f["mailForwardTo"], "type": "USER", "status": "UNCONFIRMED"})
            return _ok()
        if mode == "deleteMailForward":
            gone = {f["mailForwardTo"] for f in body["mailForward"]}
            self.forwards = [f for f in self.forwards if f["mailForwardTo"] not in gone]
            return _ok()
        if mode in ("addVacationReply", "updateVacationReply"):
            vr = body["vacationResponse"]
            if vr.get("sendTo") == "org":
                return Response(500, json.dumps({"status": {"code": 500, "description": "Internal Error"},
                                                 "data": {"moreInfo": "VacationSendTo.getSendToDetails is null"}}).encode())
            if (mode == "addVacationReply") == (self.vacation is not None):
                return _error(400, "VACATION_STATE")
            vid = self.vacation["vacationId"] if self.vacation else self._new_id()
            self.vacation = {"replyType": 0, "sendTo": 0, "subject": vr["subject"], "content": vr["content"],
                             "fromDate": vr["fromDate"].split(" ")[0], "toDate": vr["toDate"].split(" ")[0],
                             "vacationId": vid}
            return _ok([{"vacationId": int(vid)}])
        if mode == "deleteVacationReply":
            self.vacation = None
            return _ok()
        raise AssertionError(f"the fake Zoho does not know the account mode {mode}")

    # ----- signatures

    def _signature(self, method: str, query: dict, body: dict) -> Response:
        if method == "GET":
            return _ok([dict(s) for s in self.signatures])
        if method == "POST":
            sig = {"name": body["name"], "signatureType": "USER_SIGNATURE", "id": self._new_id(),
                   "position": body.get("position", 0), "content": body["content"]}
            self.signatures.append(sig)
            return _ok(dict(sig), code=201)
        if method == "PUT":
            for sig in self.signatures:
                if sig["id"] == str(body["id"]):
                    sig.update(name=body["name"], content=body["content"], position=body.get("position", 0))
                    return _ok(dict(sig))
            return _error(404, "INVALID_SIGNATURE")
        if method == "DELETE":
            before = len(self.signatures)
            self.signatures = [s for s in self.signatures if s["id"] != query.get("id")]
            return _ok() if len(self.signatures) < before else _error(404, "INVALID_SIGNATURE")
        return _error(405, "METHOD")
