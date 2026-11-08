"""The admin commands of dt-zoho: read the organisation, its domains, users and groups; and on the signed-in account,
aliases, the IMAP switch, forwarding, the vacation reply and signatures. They register on the one Typer app in cli.py,
which imports this module at its end.

Every change is read back from Zoho, field by field, before dt-zoho reports it done."""

from __future__ import annotations

from datetime import date, datetime
from typing import Optional

import typer

from . import admin, config, mail, output, write
from .cli import AccountOpt, JsonOpt, _client, _command, _report, app
from .errors import DtZohoError

alias_app = typer.Typer(no_args_is_help=True, help="The account's aliases: other addresses it receives and can send "
                                                   "from.")
imap_app = typer.Typer(no_args_is_help=True, help="Switch IMAP access to the account on or off (dt-email needs it on).")
forwarding_app = typer.Typer(no_args_is_help=True, help="Forward the account's incoming mail to another address.")
vacation_app = typer.Typer(no_args_is_help=True, help="The automatic vacation reply.")
signature_app = typer.Typer(no_args_is_help=True, help="The account's signatures.")
app.add_typer(alias_app, name="alias")
app.add_typer(imap_app, name="imap")
app.add_typer(forwarding_app, name="forwarding")
app.add_typer(vacation_app, name="vacation")
app.add_typer(signature_app, name="signature")


def _account(account: Optional[str]):
    client = _client()
    return client, mail.pick_account(client, account)


def _reread(client, acct: dict) -> dict:
    return mail.pick_account(client, acct["email"])


# --------------------------------------------------------------------------------------------- organisation


@app.command("org")
@_command
def org_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The organisation: its name, plan, primary domain, and how many mailboxes, licences, users and groups."""
    client, acct = _account(account)
    _report(admin.organisation(client, admin.org_id(acct)), json_output)


@app.command("domains")
@_command
def domains_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The organisation's domains and whether each is verified, with its mail records' status. Read only: dt-zoho
    never adds, verifies or changes a domain or its DNS."""
    client, acct = _account(account)
    records = admin.domains(client, admin.org_id(acct))
    if json_output:
        output.emit_json({"count": len(records), "domains": records})
        return
    output.emit_list("domains", ["name", "primary", "verified", "mx", "spf", "dkim"], records, None)


@app.command("users")
@_command
def users_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The organisation's users: address, name, role, mailbox status, IMAP and aliases. Read only."""
    client, acct = _account(account)
    records = admin.users(client, admin.org_id(acct))
    if json_output:
        output.emit_json({"count": len(records), "users": records})
        return
    rows = [dict(r, aliases=" ".join(r["aliases"])) for r in records]
    output.emit_list("users", ["email", "name", "role", "mailbox", "imap_enabled", "aliases"], rows, None)


@app.command("groups")
@_command
def groups_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The organisation's groups. Read only."""
    client, acct = _account(account)
    records = admin.groups(client, admin.org_id(acct))
    if json_output:
        output.emit_json({"count": len(records), "groups": records})
        return
    output.emit_list("groups", ["name", "email", "id"], records, None)


# --------------------------------------------------------------------------------------------- aliases


@alias_app.command("list")
@_command
def alias_list(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The account's aliases."""
    _client_, acct = _account(account)
    if json_output:
        output.emit_json({"count": len(acct["aliases"]), "account": acct["email"], "aliases": acct["aliases"]})
        return
    output.emit_list("aliases", ["alias"], [{"alias": a} for a in acct["aliases"]], None)


def _one_address(value: str, option: str) -> str:
    found = write.split_addresses([value])
    if len(found) != 1:
        raise DtZohoError(f"{option} takes one address, not '{value}'.", exit_code=2)
    return found[0]


@alias_app.command("add")
@_command
def alias_add(alias: str = typer.Argument(..., help="The new address, on one of the organisation's domains "
                                                    "(dt-zoho domains)."),
              account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Add an alias to the account. It can receive mail and be a --from at once."""
    address = _one_address(alias, "alias")
    client, acct = _account(account)
    if address.lower() in [a.lower() for a in acct["addresses"]]:
        raise DtZohoError(f"{address} is already one of {acct['email']}'s addresses.", exit_code=2)
    if acct["zuid"] is None:
        raise DtZohoError("Zoho's account answer carries no zuid, which adding an alias needs.")
    admin.add_alias(client, admin.org_id(acct), acct["zuid"], address)
    after = _reread(client, acct)
    if address.lower() not in [a.lower() for a in after["aliases"]]:
        raise DtZohoError(f"Zoho answered OK but {address} is not among {acct['email']}'s aliases when read back.")
    _report({"added": True, "alias": address, "account": acct["email"], "aliases": after["aliases"]}, json_output)


@alias_app.command("remove")
@_command
def alias_remove(alias: str = typer.Argument(..., help="The alias to remove (dt-zoho alias list)."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Remove an alias from the account. Mail to it is no longer delivered."""
    address = _one_address(alias, "alias")
    client, acct = _account(account)
    if address.lower() not in [a.lower() for a in acct["aliases"]]:
        raise DtZohoError(f"{address} is not an alias of {acct['email']} (aliases: "
                          f"{', '.join(acct['aliases']) or 'none'}).", exit_code=2)
    admin.remove_alias(client, admin.org_id(acct), acct["zuid"], address)
    after = _reread(client, acct)
    if address.lower() in [a.lower() for a in after["aliases"]]:
        raise DtZohoError(f"Zoho answered OK but {address} is still an alias of {acct['email']} when read back.")
    _report({"removed": True, "alias": address, "account": acct["email"], "aliases": after["aliases"]}, json_output)


# --------------------------------------------------------------------------------------------- IMAP


@imap_app.command("status")
@_command
def imap_status(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Whether IMAP access to the account is on."""
    _client_, acct = _account(account)
    _report({"account": acct["email"], "imap_enabled": acct["imap_enabled"]}, json_output)


def _switch_imap(account: Optional[str], enabled: bool, json_output: bool) -> None:
    client, acct = _account(account)
    admin.set_imap(client, admin.org_id(acct), acct["account_id"], acct["zuid"], enabled)
    # Switching on shows in Zoho's answer only after a second or two, so the read-back waits a bounded time.
    now = admin.read_back(lambda: _reread(client, acct)["imap_enabled"], enabled)
    if now is not enabled:
        shown = "missing" if now is None else ("on" if now else "off")
        raise DtZohoError(f"Zoho answered OK but IMAP for {acct['email']} still reads back as {shown} after "
                          f"{admin.READ_BACK_TRIES} reads.")
    _report({"account": acct["email"], "imap_enabled": enabled, "was": acct["imap_enabled"]}, json_output)


@imap_app.command("on")
@_command
def imap_on(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Switch IMAP access on."""
    _switch_imap(account, True, json_output)


@imap_app.command("off")
@_command
def imap_off(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Switch IMAP access off. Every IMAP sign-in then fails, dt-email's included, until it is switched on."""
    _switch_imap(account, False, json_output)


# --------------------------------------------------------------------------------------------- forwarding


def _print_forwards(acct: dict, records: list, json_output: bool, extra: Optional[dict] = None) -> None:
    if json_output:
        output.emit_json(dict(extra or {}, account=acct["email"], count=len(records), forwards=records))
        return
    if extra:
        _report(extra, False)
    output.emit_list("forwards", ["to", "status"], records, None,
                     ["UNCONFIRMED: Zoho mailed the target to confirm and forwards nothing until it does"]
                     if any(r["status"] == "UNCONFIRMED" for r in records) else [])


@forwarding_app.command("list")
@_command
def forwarding_list(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Where the account's incoming mail is forwarded, and whether each target has confirmed."""
    client, acct = _account(account)
    _print_forwards(acct, admin.forwards(admin.account_settings(client, acct["account_id"])), json_output)


@forwarding_app.command("add")
@_command
def forwarding_add(target: str = typer.Argument(..., help="The address to forward incoming mail to."),
                   account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Forward the account's incoming mail to an address. Zoho mails that address to confirm first."""
    address = _one_address(target, "the target")
    # Zoho mails the target a confirmation now, and every incoming message once it confirms.
    config.check_recipients(config.load(), [address])
    client, acct = _account(account)
    admin.add_forward(client, acct["account_id"], address)
    records = admin.forwards(admin.account_settings(client, acct["account_id"]))
    if address.lower() not in [(r["to"] or "").lower() for r in records]:
        raise DtZohoError(f"Zoho answered OK but no forward to {address} reads back.")
    _print_forwards(acct, records, json_output, {"added": address})


@forwarding_app.command("remove")
@_command
def forwarding_remove(target: str = typer.Argument(..., help="The forward's target (dt-zoho forwarding list)."),
                      account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Stop forwarding to an address."""
    address = _one_address(target, "the target")
    client, acct = _account(account)
    before = admin.forwards(admin.account_settings(client, acct["account_id"]))
    if address.lower() not in [(r["to"] or "").lower() for r in before]:
        raise DtZohoError(f"the account does not forward to {address} (forwards: "
                          f"{', '.join(r['to'] for r in before) or 'none'}).", exit_code=2)
    admin.remove_forward(client, acct["account_id"], address)
    records = admin.forwards(admin.account_settings(client, acct["account_id"]))
    if address.lower() in [(r["to"] or "").lower() for r in records]:
        raise DtZohoError(f"Zoho answered OK but the forward to {address} still reads back.")
    _print_forwards(acct, records, json_output, {"removed": address})


# --------------------------------------------------------------------------------------------- vacation reply


def _day(value: str, option: str) -> date:
    try:
        return datetime.strptime(value, "%Y-%m-%d").date()
    except ValueError:
        raise DtZohoError(f"{option} must be a day, YYYY-MM-DD, not '{value}'.", exit_code=2) from None


@vacation_app.command("show")
@_command
def vacation_show(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """The vacation reply, if one is set."""
    client, acct = _account(account)
    found = admin.vacation(admin.account_settings(client, acct["account_id"]))
    _report(dict(found or {}, account=acct["email"], set=found is not None), json_output)


@vacation_app.command("set")
@_command
def vacation_set(subject: str = typer.Option(..., "--subject"),
                 content: str = typer.Option(..., "--content", help="The reply's text."),
                 first: str = typer.Option(..., "--from", help="The first day, YYYY-MM-DD."),
                 last: str = typer.Option(..., "--to", help="The last day, YYYY-MM-DD."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Set the vacation reply, or replace the one set. It answers everyone who writes in during those days: Zoho's
    "only my organisation" setting answers 500, so it is not offered. Zoho keeps the days, not times."""
    first_day, last_day = _day(first, "--from"), _day(last, "--to")
    if last_day < first_day:
        raise DtZohoError("--to must be the same day as --from or later.", exit_code=2)
    test_mode = config.load().allowed_recipients is not None
    client, acct = _account(account)
    if test_mode:
        # An automatic reply goes to whoever writes in, which no allowed list can check. Zoho keeps the window in the
        # account's time zone, so "today" is the account's.
        earliest = admin.earliest_test_mode_start(acct["time_zone"])
        if first_day < earliest:
            raise DtZohoError("test mode is on, and a vacation reply answers whoever writes in, which the allowed list "
                              f"cannot check. In test mode --from must be {earliest.isoformat()} or later (two days "
                              f"after today in {acct['time_zone']}), and the reply removed before it starts "
                              "(dt-zoho vacation remove). Nothing was changed.", exit_code=2)
    exists = admin.vacation(admin.account_settings(client, acct["account_id"])) is not None
    admin.set_vacation(client, acct["account_id"], admin.vacation_body(subject, content, first_day, last_day), exists)
    found = admin.vacation(admin.account_settings(client, acct["account_id"]))
    wanted = {"subject": subject, "content": content, "from": first_day.isoformat(), "to": last_day.isoformat()}
    differs = [key for key, value in wanted.items() if found is None or found.get(key) != value]
    if differs:
        raise DtZohoError("Zoho answered OK but the vacation reply does not read back as set (differs: "
                          f"{', '.join(differs)}).")
    _report(dict(found, account=acct["email"], replaced=exists), json_output)


@vacation_app.command("remove")
@_command
def vacation_remove(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Remove the vacation reply."""
    client, acct = _account(account)
    if admin.vacation(admin.account_settings(client, acct["account_id"])) is None:
        raise DtZohoError(f"{acct['email']} has no vacation reply set.", exit_code=2)
    admin.remove_vacation(client, acct["account_id"])
    if admin.vacation(admin.account_settings(client, acct["account_id"])) is not None:
        raise DtZohoError("Zoho answered OK but the vacation reply still reads back.")
    _report({"removed": True, "account": acct["email"]}, json_output)


# --------------------------------------------------------------------------------------------- signatures


@signature_app.command("list")
@_command
def signature_list(json_output: bool = JsonOpt) -> None:
    """The signatures of the key's account."""
    records = admin.signatures(_client())
    if json_output:
        output.emit_json({"count": len(records), "signatures": records})
        return
    output.emit_list("signatures", ["id", "name", "position", "content"], records, None)


@signature_app.command("add")
@_command
def signature_add(name: str = typer.Option(..., "--name"), content: str = typer.Option(..., "--content"),
                  json_output: bool = JsonOpt) -> None:
    """Add a signature. (Its position is Zoho's 0, the only one proven.)"""
    client = _client()
    added = admin.add_signature(client, name, content, 0)
    _check_signature(client, added["id"], name, content)
    _report(dict(added, added=True), json_output)


def _check_signature(client, sig_id: str, name: str, content: str) -> None:
    stored = [s for s in admin.signatures(client) if s["id"] == sig_id]
    if not stored:
        raise DtZohoError(f"Zoho answered OK but signature {sig_id} is not there when read back.")
    if (stored[0]["name"], stored[0]["content"]) != (name, content):
        raise DtZohoError(f"Zoho answered OK but signature {sig_id} does not read back as set.")


@signature_app.command("change")
@_command
def signature_change(sig_id: str = typer.Argument(..., help="The signature's id (dt-zoho signature list)."),
                     name: Optional[str] = typer.Option(None, "--name"),
                     content: Optional[str] = typer.Option(None, "--content"),
                     json_output: bool = JsonOpt) -> None:
    """Change a signature's name or text; what is not given is kept."""
    if name is None and content is None:
        raise DtZohoError("nothing to change: pass --name or --content.", exit_code=2)
    client = _client()
    current = admin.find_signature(client, sig_id)
    new_name = name if name is not None else current["name"]
    new_content = content if content is not None else current["content"]
    changed = admin.change_signature(client, sig_id, new_name, new_content, int(current["position"] or 0))
    _check_signature(client, sig_id, new_name, new_content)
    _report(dict(changed, changed=True), json_output)


@signature_app.command("delete")
@_command
def signature_delete(sig_id: str = typer.Argument(..., help="The signature's id (dt-zoho signature list)."),
                     json_output: bool = JsonOpt) -> None:
    """Delete a signature."""
    client = _client()
    current = admin.find_signature(client, sig_id)
    admin.delete_signature(client, sig_id)
    if any(s["id"] == sig_id for s in admin.signatures(client)):
        raise DtZohoError(f"Zoho answered OK but signature {sig_id} still reads back.")
    _report({"deleted": True, "id": sig_id, "name": current["name"]}, json_output)
