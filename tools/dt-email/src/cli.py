"""Command line for dt-email: email over standard IMAP and SMTP, for any provider.

Every command that touches a mailbox reads the account's password from the environment variable named
in its account settings, supplied by cc-secrets:

    cc-secrets run zoho-mail-admin -- dt-email list
"""

from __future__ import annotations

import functools
import imaplib
import smtplib
import ssl
import sys
from datetime import date
from email.utils import getaddresses
from pathlib import Path
from typing import Callable, List, Optional

import typer

from . import __version__
from . import compose, config, imap_client, messages, output, smtp_client
from .errors import DtEmailError

TOOL = "dt-email"

app = typer.Typer(name=TOOL, add_completion=False, no_args_is_help=True,
                  help="Email over standard IMAP and SMTP, for any provider. The password comes from cc-secrets: "
                       "cc-secrets run <entry> -- dt-email <command>.")
account_app = typer.Typer(no_args_is_help=True, help="Set up the accounts dt-email signs in to (no secret is stored).")
recipients_app = typer.Typer(no_args_is_help=True,
                             help="Test mode: when a list is set, any other To, CC or BCC is refused before sending.")
folder_app = typer.Typer(no_args_is_help=True, help="Create, rename and delete folders.")
draft_app = typer.Typer(no_args_is_help=True, help="Save, list, send and delete drafts.")
app.add_typer(account_app, name="account")
app.add_typer(recipients_app, name="allowed-recipients")
app.add_typer(folder_app, name="folder")
app.add_typer(draft_app, name="draft")

AccountOpt = typer.Option(None, "--account", "-a", help="Account name. Default: the default account.")
JsonOpt = typer.Option(False, "--json", help="Print JSON with every field.")
FolderOpt = typer.Option("INBOX", "--folder", "-f", help="The folder the message is in.")


# --------------------------------------------------------------------------------------------- plumbing


def _fail(message: str, code: int = 1) -> None:
    """Print the error, name it for the failure reporter, and exit."""
    from cc_shared.tool_errors import note_failure

    note_failure(message)
    sys.stderr.write(f"Error: {output.ascii_text(message)}\n")
    raise typer.Exit(code)


def _command(func: Callable) -> Callable:
    """Turn every expected failure of a command into one clear error line and an exit code."""
    @functools.wraps(func)
    def wrapper(*args, **kwargs):
        try:
            return func(*args, **kwargs)
        except DtEmailError as exc:
            _fail(exc.message, exc.exit_code)
        except imaplib.IMAP4.error as exc:
            _fail(f"the IMAP server answered with an error: {exc}")
        except smtplib.SMTPException as exc:
            _fail(f"the SMTP server answered with an error: {exc}")
        except ssl.SSLError as exc:
            _fail(f"the secure connection failed: {exc}. Check the host, port and --imap-security/--smtp-security "
                  "of the account (dt-email account list).")
        except OSError as exc:
            _fail(f"could not reach the mail server: {exc}. Check the host and port of the account "
                  "(dt-email account list) and the network.")
    return wrapper


def _store(account_name: Optional[str]) -> imap_client.MailStore:
    cfg = config.load()
    acct = cfg.account(account_name)
    return imap_client.MailStore.open(acct, acct.password())


def _split(values: Optional[List[str]]) -> List[str]:
    """Addresses from repeated options, each of which may itself hold several, comma separated."""
    if not values:
        return []
    found = [a for _, a in getaddresses(values) if a]
    for a in found:
        if "@" not in a:
            raise DtEmailError(f"'{a}' is not an email address.", exit_code=2)
    return found


def _read_body(body: Optional[str], body_file: Optional[Path], html_file: Optional[Path]):
    if body is not None and body_file is not None:
        raise DtEmailError("pass --body or --body-file, not both.", exit_code=2)
    text = body
    if body_file is not None:
        text = body_file.read_text(encoding="utf-8")
    html = html_file.read_text(encoding="utf-8") if html_file is not None else None
    return text, html


def _send(acct: config.Account, cfg: config.Config, msg, store: Optional[imap_client.MailStore]) -> dict:
    """Check the recipients, send, and save to Sent only when the account says the server does not."""
    rcpts = compose.recipients_of(msg)
    if not rcpts:
        raise DtEmailError("the message has no recipients. Pass --to, --cc or --bcc.", exit_code=2)
    config.check_recipients(cfg, rcpts)
    password = acct.password()
    smtp_client.send(acct, password, msg, rcpts)
    saved_uid = None
    if acct.save_sent:
        del msg["Bcc"]
        own = store or imap_client.MailStore.open(acct, password)
        try:
            saved_uid = own.append(own.special_folder("sent"), msg.as_bytes(), ["\\Seen"])
        finally:
            if store is None:
                own.close()
    return {"sent": True, "message_id": msg["Message-ID"], "from": msg["From"], "subject": msg["Subject"],
            "recipients": rcpts, "saved_to_sent_uid": saved_uid}


def _print_sent(result: dict, json_output: bool) -> None:
    if json_output:
        output.emit_json(result)
        return
    output.emit([output.kv_block([
        ("sent", True), ("message_id", result["message_id"]), ("from", result["from"]),
        ("subject", result["subject"]), ("recipients", ", ".join(result["recipients"])),
        ("saved_to_sent_uid", result.get("saved_to_sent_uid")),
        ("deleted_draft_uid", result.get("deleted_draft_uid"))])])


def _uids(values: List[str]) -> List[int]:
    found: List[int] = []
    for value in values:
        for piece in value.split(","):
            piece = piece.strip()
            if not piece.isdigit():
                raise DtEmailError(f"'{piece}' is not a message UID (a whole number from dt-email list).", exit_code=2)
            found.append(int(piece))
    return found


# --------------------------------------------------------------------------------------------- top level


def _version(value: bool) -> None:
    if value:
        typer.echo(f"{TOOL} {__version__}")
        raise typer.Exit()


@app.callback()
def main(version: bool = typer.Option(False, "--version", callback=_version, is_eager=True,
                                      help="Print the version and exit.")) -> None:
    """Email over standard IMAP and SMTP, for any provider."""


# ----- accounts


@account_app.command("add")
@_command
def account_add(
    name: str = typer.Argument(..., help="A short name for the account, used with --account."),
    email_address: str = typer.Option(..., "--email", help="The account's email address."),
    imap_host: str = typer.Option(..., "--imap-host", help="IMAP server, for example imap.zohocloud.ca."),
    smtp_host: str = typer.Option(..., "--smtp-host", help="SMTP server, for example smtp.zohocloud.ca."),
    secret_entry: str = typer.Option(..., "--secret-entry", help="The cc-secrets entry holding the password."),
    password_env: str = typer.Option(..., "--password-env", help="The variable cc-secrets puts the password in."),
    user: Optional[str] = typer.Option(None, "--user", help="Sign-in name. Default: the --email address."),
    imap_port: int = typer.Option(993, "--imap-port"),
    imap_security: str = typer.Option("ssl", "--imap-security", help="ssl or starttls."),
    smtp_port: int = typer.Option(465, "--smtp-port"),
    smtp_security: str = typer.Option("ssl", "--smtp-security", help="ssl or starttls."),
    save_sent: bool = typer.Option(False, "--save-sent/--no-save-sent",
                                   help="Save a copy in Sent after sending. Leave off for a server that saves sent "
                                        "mail itself (Zoho, Gmail, Fastmail), or every message is stored twice."),
    make_default: bool = typer.Option(False, "--default", help="Make this the default account."),
    json_output: bool = JsonOpt,
) -> None:
    """Add or replace an account. Stores host, port, sign-in name and WHICH cc-secrets entry holds the password,
    never the password."""
    for label, value in (("--imap-security", imap_security), ("--smtp-security", smtp_security)):
        if value not in config.SECURITY_CHOICES:
            raise DtEmailError(f"{label} must be one of {', '.join(config.SECURITY_CHOICES)}, not '{value}'.", 2)
    cfg = config.load()
    acct = config.Account(name=name, email=config.normalise_address(email_address),
                          user=user or email_address, imap_host=imap_host, imap_port=imap_port,
                          imap_security=imap_security, smtp_host=smtp_host, smtp_port=smtp_port,
                          smtp_security=smtp_security, secret_entry=secret_entry, password_env=password_env,
                          save_sent=save_sent)
    cfg.accounts[name] = acct
    if make_default or cfg.default is None:
        cfg.default = name
    path = config.save(cfg)
    data = {"account": name, **acct.to_dict(), "default": cfg.default == name, "config": str(path)}
    if json_output:
        output.emit_json(data)
    else:
        output.emit([output.kv_block(data.items()),
                     output.format_help([f"cc-secrets run {secret_entry} -- dt-email account check --account {name}"])])


@account_app.command("list")
@_command
def account_list(json_output: bool = JsonOpt) -> None:
    """List the accounts set up on this machine."""
    cfg = config.load()
    records = [{"name": a.name, "email": a.email, "imap_host": a.imap_host, "smtp_host": a.smtp_host,
                "secret_entry": a.secret_entry, "password_env": a.password_env, "save_sent": a.save_sent,
                "default": a.name == cfg.default} for a in sorted(cfg.accounts.values(), key=lambda a: a.name)]
    if json_output:
        output.emit_json({"accounts": records, "allowed_recipients": cfg.allowed_recipients})
        return
    output.emit_list("accounts", ["name", "email", "imap_host", "smtp_host", "secret_entry", "default"], records,
                     total=None, help_lines=[] if records else [
                         "dt-email account add <name> --email <address> --imap-host <host> --smtp-host <host> "
                         "--secret-entry <entry> --password-env <VARIABLE>"])


@account_app.command("remove")
@_command
def account_remove(name: str = typer.Argument(...), json_output: bool = JsonOpt) -> None:
    """Remove an account's settings. The password in cc-secrets is not touched."""
    cfg = config.load()
    if name not in cfg.accounts:
        raise DtEmailError(f"no account named '{name}'. List them with: dt-email account list")
    del cfg.accounts[name]
    if cfg.default == name:
        cfg.default = None
    config.save(cfg)
    if json_output:
        output.emit_json({"removed": name})
    else:
        output.emit([output.kv_block([("removed", name)])])


@account_app.command("default")
@_command
def account_default(name: str = typer.Argument(...), json_output: bool = JsonOpt) -> None:
    """Make an account the default."""
    cfg = config.load()
    cfg.account(name)
    cfg.default = name
    config.save(cfg)
    if json_output:
        output.emit_json({"default": name})
    else:
        output.emit([output.kv_block([("default", name)])])


@account_app.command("check")
@_command
def account_check(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Sign in to IMAP and SMTP and report what the servers offer. Sends nothing."""
    cfg = config.load()
    acct = cfg.account(account)
    password = acct.password()
    with imap_client.MailStore.open(acct, password) as store:
        caps = store.capabilities()
        folders = store.folders()
    conn = smtp_client.connect(acct)
    try:
        conn.login(acct.user, password)
        smtp_features = sorted(conn.esmtp_features.keys()) if hasattr(conn, "esmtp_features") else []
    finally:
        if conn.sock is not None:
            conn.quit()
    data = {"account": acct.name, "imap": "signed in", "smtp": "signed in", "imap_capabilities": caps,
            "has_move": "MOVE" in caps, "has_uidplus": "UIDPLUS" in caps, "folders": len(folders),
            "smtp_features": smtp_features}
    if json_output:
        output.emit_json(data)
    else:
        output.emit([output.kv_block([(k, " ".join(v) if isinstance(v, list) else v) for k, v in data.items()])])


# ----- allowed recipients


@recipients_app.command("show")
@_command
def recipients_show(json_output: bool = JsonOpt) -> None:
    """Show the allowed recipients. 'off' means no restriction."""
    cfg = config.load()
    if json_output:
        output.emit_json({"allowed_recipients": cfg.allowed_recipients})
        return
    if cfg.allowed_recipients is None:
        output.emit(["allowed_recipients: off (any recipient may be used)"])
        return
    output.emit_list("allowed_recipients", ["address"], [{"address": a} for a in cfg.allowed_recipients], None)


@recipients_app.command("set")
@_command
def recipients_set(addresses: List[str] = typer.Argument(..., help="Every address that may receive mail."),
                   json_output: bool = JsonOpt) -> None:
    """Turn test mode on: only these addresses may be a To, CC or BCC. Replaces any earlier list."""
    cfg = config.load()
    cfg.allowed_recipients = sorted({config.normalise_address(a) for a in addresses})
    config.save(cfg)
    if json_output:
        output.emit_json({"allowed_recipients": cfg.allowed_recipients})
    else:
        output.emit_list("allowed_recipients", ["address"], [{"address": a} for a in cfg.allowed_recipients], None)


@recipients_app.command("clear")
@_command
def recipients_clear(json_output: bool = JsonOpt) -> None:
    """Turn test mode off: any recipient may be used."""
    cfg = config.load()
    cfg.allowed_recipients = None
    config.save(cfg)
    if json_output:
        output.emit_json({"allowed_recipients": None})
    else:
        output.emit(["allowed_recipients: off (any recipient may be used)"])


# ----- folders


@app.command("folders")
@_command
def folders_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List folders with their special use (drafts, sent, trash, junk, archive) and message counts."""
    with _store(account) as store:
        folders = store.folders(with_counts=True)
    records = [{"name": f.name, "special": f.special, "messages": f.messages, "unseen": f.unseen,
                "flags": " ".join(f.flags)} for f in folders]
    if json_output:
        output.emit_json({"count": len(records), "folders": records})
        return
    output.emit_list("folders", ["name", "special", "messages", "unseen"], records, None,
                     ['dt-email list --folder "<name>"'])


@folder_app.command("create")
@_command
def folder_create(name: str = typer.Argument(..., help="Folder name; use the server's separator for a nested one."),
                  account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Create a folder."""
    with _store(account) as store:
        store.create_folder(name)
    _report({"created": name}, json_output)


@folder_app.command("rename")
@_command
def folder_rename(old: str = typer.Argument(...), new: str = typer.Argument(...),
                  account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Rename a folder."""
    with _store(account) as store:
        store.rename_folder(old, new)
    _report({"renamed": old, "to": new}, json_output)


@folder_app.command("delete")
@_command
def folder_delete(name: str = typer.Argument(...), account: Optional[str] = AccountOpt,
                  json_output: bool = JsonOpt) -> None:
    """Delete a folder and the messages in it. Some servers move it into Trash instead; that is reported."""
    with _store(account) as store:
        moved_to = store.delete_folder(name)
    data = {"deleted": name, "server_moved_it_to": moved_to}
    if moved_to and not json_output:
        output.emit([output.kv_block(data.items()),
                     f"note: the server kept the folder in Trash. Delete it there too with: "
                     f'dt-email folder delete "{moved_to}"'])
        return
    _report(data, json_output)


def _report(data: dict, json_output: bool) -> None:
    if json_output:
        output.emit_json(data)
    else:
        output.emit([output.kv_block(data.items())])


# ----- reading


def _summaries(store: imap_client.MailStore, folder: str, uids: List[int], count: int) -> List[dict]:
    newest = sorted(uids, reverse=True)[:count]
    items = store.fetch_summaries(folder, newest)
    return sorted((messages.summary(i) for i in items), key=lambda r: r["uid"], reverse=True)


def _print_summaries(records: List[dict], total: int, fields: Optional[str], json_output: bool, folder: str) -> None:
    if json_output:
        output.emit_json({"count": len(records), "total": total, "folder": folder, "messages": records})
        return
    from cc_shared.axi_output import parse_fields_or_exit

    chosen = parse_fields_or_exit(fields, messages.SUMMARY_FIELDS, messages.SUMMARY_DEFAULT)
    help_lines = [f'dt-email read <uid> --folder "{folder}"'] if records else []
    output.emit_list("messages", chosen, records, total, help_lines)


CountOpt = typer.Option(20, "--count", "-n", min=1, help="How many of the newest messages to show.")
FieldsOpt = typer.Option(None, "--fields", help="Fields to show: " + ",".join(messages.SUMMARY_FIELDS) + ".")


@app.command("list")
@_command
def list_command(folder: str = typer.Option("INBOX", "--folder", "-f"), count: int = CountOpt,
                 unread: bool = typer.Option(False, "--unread", help="Only unread messages."),
                 fields: Optional[str] = FieldsOpt, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """List the newest messages in a folder, newest first."""
    with _store(account) as store:
        all_uids = store.search(folder, ["ALL"])
        uids = store.search(folder, ["UNSEEN"]) if unread else all_uids
        records = _summaries(store, folder, uids, count)
    _print_summaries(records, len(all_uids), fields, json_output, folder)


def _imap_date(value: str, label: str) -> str:
    try:
        parsed = date.fromisoformat(value)
    except ValueError as exc:
        raise DtEmailError(f"{label} must be a date like 2026-11-07, not '{value}'.", exit_code=2) from exc
    months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
    return f"{parsed.day:02d}-{months[parsed.month - 1]}-{parsed.year}"


def _search_term(value: str, label: str) -> str:
    if not value.isascii():
        raise DtEmailError(f"{label} must be plain ASCII text; dt-email does not send non-ASCII search terms.", 2)
    return imap_client.quote(value)


@app.command("search")
@_command
def search_command(
    text: Optional[str] = typer.Argument(None, help="Words anywhere in the message (headers or body)."),
    from_: Optional[str] = typer.Option(None, "--from", help="Sender contains this."),
    to: Optional[str] = typer.Option(None, "--to", help="To contains this."),
    subject: Optional[str] = typer.Option(None, "--subject", help="Subject contains this."),
    body: Optional[str] = typer.Option(None, "--body", help="Body contains this."),
    since: Optional[str] = typer.Option(None, "--since", help="On or after this date (2026-11-07)."),
    before: Optional[str] = typer.Option(None, "--before", help="Before this date (2026-11-07)."),
    unread: bool = typer.Option(False, "--unread"), flagged: bool = typer.Option(False, "--flagged"),
    folder: str = typer.Option("INBOX", "--folder", "-f"), count: int = CountOpt,
    fields: Optional[str] = FieldsOpt, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt,
) -> None:
    """Search a folder on the server. A message saved or moved in the last minute or so may not be found yet:
    the server's search index lags; use the UID a command printed instead."""
    criteria: List[str] = []
    if text is not None:
        criteria += ["TEXT", _search_term(text, "the search text")]
    for key, value, label in (("FROM", from_, "--from"), ("TO", to, "--to"), ("SUBJECT", subject, "--subject"),
                              ("BODY", body, "--body")):
        if value is not None:
            criteria += [key, _search_term(value, label)]
    if since is not None:
        criteria += ["SINCE", _imap_date(since, "--since")]
    if before is not None:
        criteria += ["BEFORE", _imap_date(before, "--before")]
    if unread:
        criteria.append("UNSEEN")
    if flagged:
        criteria.append("FLAGGED")
    if not criteria:
        raise DtEmailError("give something to search for: text, --from, --to, --subject, --body, --since, "
                           "--before, --unread or --flagged. To see everything use: dt-email list", exit_code=2)
    with _store(account) as store:
        uids = store.search(folder, criteria)
        records = _summaries(store, folder, uids, count)
    _print_summaries(records, len(uids), fields, json_output, folder)


BODY_LIMIT = 4000


@app.command("read")
@_command
def read_command(uid: int = typer.Argument(..., help="The message UID, from dt-email list."), folder: str = FolderOpt,
                 html: bool = typer.Option(False, "--html", help="Show the HTML part instead of the plain text."),
                 full: bool = typer.Option(False, "--full", help=f"Show the whole body, not the first {BODY_LIMIT} characters."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Read one message. Does not mark it read (use dt-email mark)."""
    with _store(account) as store:
        item = store.fetch_message(folder, uid)
    record = messages.full_record(uid, folder, item.flags(), item.literal, html)
    if json_output:
        output.emit_json(record)
        return
    text = record["body"] or ""
    shown = text if full or len(text) <= BODY_LIMIT else text[:BODY_LIMIT]
    blocks = [output.kv_block((k, record[k]) for k in (
        "uid", "folder", "date", "from", "to", "cc", "bcc", "reply_to", "subject", "message_id", "in_reply_to", "flags"))]
    if record["attachments"]:
        blocks.append(output.render_list("attachments", ["index", "filename", "type", "size"], record["attachments"]))
    note = "" if shown is text else f" (truncated, {len(text)} chars total - use --full)"
    blocks.append(f"body: {record['body_type'] or 'none'}{note}")
    blocks.append(output.ascii_text(shown).rstrip("\n"))
    help_lines = [f'dt-email reply {uid} --folder "{folder}" --body "<text>"']
    if record["attachments"]:
        help_lines.append(f'dt-email download {uid} --folder "{folder}" --out <folder on disk>')
    blocks.append(output.format_help(help_lines))
    output.emit(blocks)


@app.command("attachments")
@_command
def attachments_command(uid: int = typer.Argument(...), folder: str = FolderOpt,
                        account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List a message's attachments."""
    with _store(account) as store:
        item = store.fetch_message(folder, uid)
    records = [a.record() for a in messages.attachments(compose.parse(item.literal))]
    if json_output:
        output.emit_json({"count": len(records), "uid": uid, "attachments": records})
        return
    output.emit_list("attachments", ["index", "filename", "type", "size"], records, None,
                     [f'dt-email download {uid} --folder "{folder}" --out <folder on disk>'] if records else [])


@app.command("download")
@_command
def download_command(uid: int = typer.Argument(...), out: Path = typer.Option(..., "--out", help="Folder to save into."),
                     index: Optional[List[int]] = typer.Option(None, "--index", help="Only this attachment (repeat)."),
                     overwrite: bool = typer.Option(False, "--overwrite", help="Replace files that already exist."),
                     folder: str = FolderOpt, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Save a message's attachments to a folder on disk."""
    with _store(account) as store:
        item = store.fetch_message(folder, uid)
    found = messages.attachments(compose.parse(item.literal))
    if index:
        known = {a.index for a in found}
        unknown = [i for i in index if i not in known]
        if unknown:
            raise DtEmailError(f"message {uid} has no attachment {', '.join(map(str, unknown))}; it has "
                               f"{len(found)}. List them with: dt-email attachments {uid}", exit_code=2)
        found = [a for a in found if a.index in index]
    out.mkdir(parents=True, exist_ok=True)
    targets = [(a, out / a.filename) for a in found]
    clashes = [str(p) for _, p in targets if p.exists()]
    if clashes and not overwrite:
        raise DtEmailError(f"these files already exist: {', '.join(clashes)}. Nothing was saved. "
                           "Pass --overwrite to replace them, or choose another --out.")
    records = []
    for att, path in targets:
        data = att.data()
        path.write_bytes(data)
        records.append({"index": att.index, "filename": att.filename, "path": str(path.resolve()), "size": len(data)})
    if json_output:
        output.emit_json({"count": len(records), "uid": uid, "saved": records})
        return
    output.emit_list("saved", ["index", "filename", "size", "path"], records, None)


# ----- sending


ToOpt = typer.Option(None, "--to", help="Recipient (repeat, or comma separated).")
CcOpt = typer.Option(None, "--cc", help="Copy to (repeat, or comma separated).")
BccOpt = typer.Option(None, "--bcc", help="Blind copy to (repeat, or comma separated). Never shown to others.")
BodyOpt = typer.Option(None, "--body", help="Plain text body.")
BodyFileOpt = typer.Option(None, "--body-file", help="Read the plain text body from this file (UTF-8).")
HtmlFileOpt = typer.Option(None, "--html-file", help="HTML body from this file (UTF-8); sent with the plain text "
                                                       "as its alternative when both are given.")
AttachOpt = typer.Option(None, "--attach", help="Attach this file (repeat).")
FromOpt = typer.Option(None, "--from", help="Send from this address: the account's own or an alias the provider "
                                            "has set up for it. Default: the account's address.")


@app.command("send")
@_command
def send_command(to: Optional[List[str]] = ToOpt, cc: Optional[List[str]] = CcOpt, bcc: Optional[List[str]] = BccOpt,
                 subject: str = typer.Option(..., "--subject", "-s"), body: Optional[str] = BodyOpt,
                 body_file: Optional[Path] = BodyFileOpt, html_file: Optional[Path] = HtmlFileOpt,
                 attach: Optional[List[Path]] = AttachOpt, from_: Optional[str] = FromOpt,
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Send a new message."""
    cfg = config.load()
    acct = cfg.account(account)
    text, html = _read_body(body, body_file, html_file)
    msg = compose.new_message(from_ or acct.email, _split(to), _split(cc), _split(bcc), subject, text, html,
                              [str(p) for p in attach or []])
    _print_sent(_send(acct, cfg, msg, None), json_output)


def _original(store: imap_client.MailStore, folder: str, uid: int):
    return compose.parse(store.fetch_message(folder, uid).literal)


@app.command("reply")
@_command
def reply_command(uid: int = typer.Argument(..., help="The UID of the message to reply to."), folder: str = FolderOpt,
                  all_: bool = typer.Option(False, "--all", help="Reply to everyone: the sender, plus the original "
                                                                 "To and CC except this account."),
                  body: Optional[str] = BodyOpt, body_file: Optional[Path] = BodyFileOpt,
                  html_file: Optional[Path] = HtmlFileOpt, attach: Optional[List[Path]] = AttachOpt,
                  from_: Optional[str] = FromOpt, account: Optional[str] = AccountOpt,
                  json_output: bool = JsonOpt) -> None:
    """Reply (or reply to all), in the same thread: In-Reply-To and References point at the original."""
    cfg = config.load()
    acct = cfg.account(account)
    text, html = _read_body(body, body_file, html_file)
    with imap_client.MailStore.open(acct, acct.password()) as store:
        original = _original(store, folder, uid)
        own = [acct.email] + ([from_] if from_ else [])
        msg = compose.reply(original, from_ or acct.email, own, all_, text, html, [str(p) for p in attach or []])
        result = _send(acct, cfg, msg, store)
    _print_sent(result, json_output)


@app.command("forward")
@_command
def forward_command(uid: int = typer.Argument(..., help="The UID of the message to forward."), folder: str = FolderOpt,
                    to: Optional[List[str]] = ToOpt, cc: Optional[List[str]] = CcOpt,
                    bcc: Optional[List[str]] = BccOpt, body: Optional[str] = BodyOpt,
                    body_file: Optional[Path] = BodyFileOpt, html_file: Optional[Path] = HtmlFileOpt,
                    attach: Optional[List[Path]] = AttachOpt, from_: Optional[str] = FromOpt,
                    account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Forward a message with the original attached whole, keeping the thread (References)."""
    cfg = config.load()
    acct = cfg.account(account)
    text, html = _read_body(body, body_file, html_file)
    with imap_client.MailStore.open(acct, acct.password()) as store:
        original = _original(store, folder, uid)
        msg = compose.forward(original, from_ or acct.email, _split(to), _split(cc), _split(bcc), text, html,
                              [str(p) for p in attach or []])
        result = _send(acct, cfg, msg, store)
    _print_sent(result, json_output)


# ----- drafts


@draft_app.command("save")
@_command
def draft_save(to: Optional[List[str]] = ToOpt, cc: Optional[List[str]] = CcOpt, bcc: Optional[List[str]] = BccOpt,
               subject: str = typer.Option("", "--subject", "-s"), body: Optional[str] = BodyOpt,
               body_file: Optional[Path] = BodyFileOpt, html_file: Optional[Path] = HtmlFileOpt,
               attach: Optional[List[Path]] = AttachOpt, from_: Optional[str] = FromOpt,
               replace: Optional[int] = typer.Option(None, "--replace", help="Replace this draft UID (IMAP cannot "
                                                                              "edit in place: the new version is "
                                                                              "saved, then the old one deleted)."),
               account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Save a draft in the Drafts folder. Sends nothing."""
    cfg = config.load()
    acct = cfg.account(account)
    text, html = _read_body(body, body_file, html_file)
    if text is None and html is None:
        text = ""
    msg = compose.new_message(from_ or acct.email, _split(to), _split(cc), _split(bcc), subject, text, html,
                              [str(p) for p in attach or []])
    with imap_client.MailStore.open(acct, acct.password()) as store:
        drafts = store.special_folder("drafts")
        if replace is not None:
            store.require_present(drafts, [replace])
        uid = store.append(drafts, msg.as_bytes(), ["\\Draft", "\\Seen"])
        if replace is not None:
            store.delete_permanently(drafts, [replace])
    _report({"saved_draft_uid": uid, "folder": drafts, "replaced_uid": replace, "subject": subject}, json_output)


@draft_app.command("list")
@_command
def draft_list(count: int = CountOpt, fields: Optional[str] = FieldsOpt, account: Optional[str] = AccountOpt,
               json_output: bool = JsonOpt) -> None:
    """List drafts, newest first."""
    with _store(account) as store:
        drafts = store.special_folder("drafts")
        uids = store.search(drafts, ["ALL"])
        records = _summaries(store, drafts, uids, count)
    _print_summaries(records, len(uids), fields, json_output, drafts)


@draft_app.command("send")
@_command
def draft_send(uid: int = typer.Argument(..., help="The draft's UID."), account: Optional[str] = AccountOpt,
               json_output: bool = JsonOpt) -> None:
    """Send a draft as it is saved, then delete it from Drafts."""
    cfg = config.load()
    acct = cfg.account(account)
    with imap_client.MailStore.open(acct, acct.password()) as store:
        drafts = store.special_folder("drafts")
        msg = compose.parse(store.fetch_message(drafts, uid).literal)
        if not msg.get("From"):
            msg["From"] = acct.email
        if not msg.get("Message-ID"):
            raise DtEmailError(f"draft {uid} has no Message-ID header; save it again with dt-email draft save.")
        result = _send(acct, cfg, msg, store)
        store.delete_permanently(drafts, [uid])
    result["deleted_draft_uid"] = uid
    _print_sent(result, json_output)


@draft_app.command("delete")
@_command
def draft_delete(uids: List[str] = typer.Argument(..., help="Draft UIDs."), account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """Delete drafts permanently."""
    numbers = _uids(uids)
    with _store(account) as store:
        drafts = store.special_folder("drafts")
        store.delete_permanently(drafts, numbers)
    _report({"deleted": ",".join(map(str, numbers)), "folder": drafts, "permanent": True}, json_output)


# ----- flags, moving, deleting

MARKS = {"read": (True, "\\Seen"), "unread": (False, "\\Seen"), "flagged": (True, "\\Flagged"),
         "unflagged": (False, "\\Flagged")}


@app.command("mark")
@_command
def mark_command(uids: List[str] = typer.Argument(..., help="Message UIDs (space or comma separated)."),
                 as_: str = typer.Option(..., "--as", help="read, unread, flagged or unflagged."),
                 folder: str = FolderOpt, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Mark messages read, unread, flagged or unflagged."""
    if as_ not in MARKS:
        raise DtEmailError(f"--as must be one of {', '.join(MARKS)}, not '{as_}'.", exit_code=2)
    add, flag = MARKS[as_]
    numbers = _uids(uids)
    with _store(account) as store:
        result = store.store_flags(folder, numbers, add, [flag])
    records = [{"uid": u, "flags": " ".join(result.get(u, []))} for u in numbers]
    if json_output:
        output.emit_json({"count": len(records), "marked": as_, "messages": records})
        return
    output.emit_list("marked", ["uid", "flags"], records, None)


@app.command("move")
@_command
def move_command(uids: List[str] = typer.Argument(...), to: str = typer.Option(..., "--to", help="Destination folder."),
                 folder: str = FolderOpt, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Move messages to another folder."""
    numbers = _uids(uids)
    with _store(account) as store:
        store.move(folder, numbers, to)
    _report({"moved": ",".join(map(str, numbers)), "from": folder, "to": to}, json_output)


@app.command("archive")
@_command
def archive_command(uids: List[str] = typer.Argument(...), folder: str = FolderOpt,
                    account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Move messages to the Archive folder."""
    numbers = _uids(uids)
    with _store(account) as store:
        archive = store.special_folder("archive")
        store.move(folder, numbers, archive)
    _report({"moved": ",".join(map(str, numbers)), "from": folder, "to": archive}, json_output)


@app.command("delete")
@_command
def delete_command(uids: List[str] = typer.Argument(...), folder: str = FolderOpt,
                   permanent: bool = typer.Option(False, "--permanent", help="Remove for good instead of moving to Trash."),
                   account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Delete messages: move them to Trash, or with --permanent remove them for good."""
    numbers = _uids(uids)
    with _store(account) as store:
        if permanent:
            store.delete_permanently(folder, numbers)
            data = {"deleted": ",".join(map(str, numbers)), "folder": folder, "permanent": True}
        else:
            trash = store.special_folder("trash")
            if folder.lower() == trash.lower():
                raise DtEmailError(f"these messages are already in {trash}. To remove them for good: "
                                   f"dt-email delete {' '.join(map(str, numbers))} --folder \"{trash}\" --permanent",
                                   exit_code=2)
            store.move(folder, numbers, trash)
            data = {"deleted": ",".join(map(str, numbers)), "folder": folder, "moved_to": trash, "permanent": False}
    _report(data, json_output)


def tool_main() -> None:
    """The console-script entry point, through the shared failure reporter."""
    from cc_shared.tool_errors import run_tool

    run_tool(lambda: app(prog_name=TOOL), TOOL, app=app)
