"""The writing commands of dt-zoho: test mode, send, reply, drafts, marks, moves, delete, spam, labels, folders,
threads. They register on the one Typer app in cli.py, which imports this module at its end."""

from __future__ import annotations

from pathlib import Path
from typing import List, Optional

import typer

from . import config, mail, output, write
from .cli import (AccountOpt, CountOpt, FieldsOpt, FolderOpt, JsonOpt, StartOpt, _client, _command, _message_fields,
                  _print_messages, _report, app)
from .errors import DtZohoError

recipients_app = typer.Typer(no_args_is_help=True,
                             help="Test mode: only the listed addresses may be a To, CC or BCC of anything sent.")
draft_app = typer.Typer(no_args_is_help=True, help="Save, list and delete drafts.")
label_app = typer.Typer(no_args_is_help=True, help="Create, rename and delete labels; put them on messages or take "
                                                   "them off.")
folder_app = typer.Typer(no_args_is_help=True, help="Create, rename, move, empty and delete folders.")
thread_app = typer.Typer(no_args_is_help=True, help="Mark or label every message in a thread at once.")
app.add_typer(recipients_app, name="allowed-recipients")
app.add_typer(draft_app, name="draft")
app.add_typer(label_app, name="label")
app.add_typer(folder_app, name="folder")
app.add_typer(thread_app, name="thread")

ToOpt = typer.Option(None, "--to", help="Recipient (repeat, or comma separated).")
CcOpt = typer.Option(None, "--cc", help="Copy to (repeat, or comma separated).")
BccOpt = typer.Option(None, "--bcc", help="Blind copy to (repeat, or comma separated). Never shown to others.")
BodyOpt = typer.Option(None, "--body", help="Plain text body.")
BodyFileOpt = typer.Option(None, "--body-file", help="Read the plain text body from this file (UTF-8).")
HtmlFileOpt = typer.Option(None, "--html-file", help="Send this file (UTF-8) as an HTML body instead of plain text.")
AttachOpt = typer.Option(None, "--attach", help="Attach this file (repeat).")
FromOpt = typer.Option(None, "--from", help="Send from this address: the account's own or one of its aliases "
                                            "(dt-zoho accounts). Default: the account's address.")
AtOpt = typer.Option(None, "--at", help="Send later, at this date and time: YYYY-MM-DD HH:MM. Zoho has no call to "
                                        "list or cancel a scheduled message.")
TimeZoneOpt = typer.Option(None, "--time-zone", help="The time zone of --at, for example America/Toronto. Default: "
                                                     "the account's time zone.")
IdsArg = typer.Argument(..., help="Message ids (dt-zoho list), space or comma separated.")
MarkOpt = typer.Option(..., "--as", help="One of: " + ", ".join(write.MARKS) + ".")


def _from_address(acct: dict, from_: Optional[str]) -> str:
    if from_ is None:
        return acct["email"]
    own = {a.lower() for a in acct["addresses"]} | {(acct["email"] or "").lower()}
    if from_.strip().lower() not in own:
        raise DtZohoError(f"--from {from_} is not one of the account's addresses ({', '.join(acct['addresses'])}). "
                          "An alias must be added to the account first.", exit_code=2)
    return from_.strip()


def _print_sent(record: dict, json_output: bool) -> None:
    if json_output:
        output.emit_json(dict(record, sent=record["scheduled_for"] is None, scheduled=record["scheduled_for"] is not None))
        return
    _report({k: v for k, v in record.items() if v}, False)


def _ids_record(key: str, ids: List[str], **extra) -> dict:
    return {key: ids, "count": len(ids), **extra}


# --------------------------------------------------------------------------------------------- test mode


def _show_recipients(allowed: Optional[List[str]], json_output: bool) -> None:
    if json_output:
        output.emit_json({"allowed_recipients": allowed})
    elif allowed is None:
        output.emit(["allowed_recipients: off (any recipient may be used)"])
    else:
        output.emit_list("allowed_recipients", ["address"], [{"address": a} for a in allowed], None)


@recipients_app.command("show")
@_command
def recipients_show(json_output: bool = JsonOpt) -> None:
    """Show the allowed recipients. 'off' means no restriction."""
    _show_recipients(config.load().allowed_recipients, json_output)


@recipients_app.command("set")
@_command
def recipients_set(addresses: List[str] = typer.Argument(..., help="Every address that may receive mail."),
                   json_output: bool = JsonOpt) -> None:
    """Turn test mode on: only these addresses may be a To, CC or BCC. Replaces any earlier list."""
    settings = config.load()
    settings.allowed_recipients = sorted({config.normalise_address(a) for a in addresses})
    config.save(settings)
    _show_recipients(settings.allowed_recipients, json_output)


@recipients_app.command("clear")
@_command
def recipients_clear(json_output: bool = JsonOpt) -> None:
    """Turn test mode off: any recipient may be used."""
    settings = config.load()
    settings.allowed_recipients = None
    config.save(settings)
    _show_recipients(None, json_output)


# --------------------------------------------------------------------------------------------- sending


@app.command("send")
@_command
def send_command(to: Optional[List[str]] = ToOpt, cc: Optional[List[str]] = CcOpt, bcc: Optional[List[str]] = BccOpt,
                 subject: str = typer.Option(..., "--subject", "-s"), body: Optional[str] = BodyOpt,
                 body_file: Optional[Path] = BodyFileOpt, html_file: Optional[Path] = HtmlFileOpt,
                 attach: Optional[List[Path]] = AttachOpt, from_: Optional[str] = FromOpt,
                 at: Optional[str] = AtOpt, time_zone: Optional[str] = TimeZoneOpt,
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Send a new message, now or at a set time."""
    to_list, cc_list, bcc_list = write.split_addresses(to), write.split_addresses(cc), write.split_addresses(bcc)
    if not to_list:
        raise DtZohoError("pass --to: Zoho needs at least one To address.", exit_code=2)
    content, mail_format = write.read_body(body, body_file, html_file)
    files = write.check_files(attach)
    write.check_at(at, time_zone)
    config.check_recipients(config.load(), to_list + cc_list + bcc_list)
    client = _client()
    acct = mail.pick_account(client, account)
    schedule = write.schedule_fields(at, time_zone or acct["time_zone"]) if at is not None else {}
    aid = acct["account_id"]
    uploads = [write.upload(client, aid, f) for f in files]
    answer = write.send(client, aid, write.message_body(_from_address(acct, from_), to_list, cc_list, bcc_list,
                                                       subject, content, mail_format, uploads, schedule))
    _print_sent(dict(write.sent_record(answer, bool(schedule)), attachments=[u["attachmentName"] for u in uploads]),
                json_output)


@app.command("reply")
@_command
def reply_command(message_id: str = typer.Argument(..., help="The message to reply to (dt-zoho list)."),
                  all_: bool = typer.Option(False, "--all", help="Reply to everyone: the sender, plus the original "
                                                                 "To and CC except this account."),
                  body: Optional[str] = BodyOpt, body_file: Optional[Path] = BodyFileOpt,
                  html_file: Optional[Path] = HtmlFileOpt, attach: Optional[List[Path]] = AttachOpt,
                  from_: Optional[str] = FromOpt, account: Optional[str] = AccountOpt,
                  json_output: bool = JsonOpt) -> None:
    """Reply (or reply to all) in the same thread: the reply names the original in In-Reply-To and References."""
    content, mail_format = write.read_body(body, body_file, html_file)
    files = write.check_files(attach)
    write.split_ids([message_id])
    client = _client()
    acct = mail.pick_account(client, account)
    aid = acct["account_id"]
    original = mail.original(client, aid, message_id)
    own = acct["addresses"] + [acct["email"]] + ([from_] if from_ else [])
    to_list, cc_list = write.reply_recipients(original, own, all_)
    config.check_recipients(config.load(), to_list + cc_list)
    uploads = [write.upload(client, aid, f) for f in files]
    body_data = write.message_body(_from_address(acct, from_), to_list, cc_list, [],
                                   write.reply_subject(write.original_subject(original)), content, mail_format,
                                   uploads)
    answer = write.reply(client, aid, message_id, body_data, all_)
    _print_sent(dict(write.sent_record(answer, False), in_reply_to=message_id,
                     attachments=[u["attachmentName"] for u in uploads]), json_output)


@app.command("forward", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
@_command
def forward_command(ctx: typer.Context) -> None:
    """Not possible through Zoho's API: says so and names the tool that can."""
    raise DtZohoError(write.FORWARD_REFUSAL, exit_code=2)


@app.command("filters", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
@_command
def filters_command(ctx: typer.Context) -> None:
    """Not possible through Zoho's API: says so and where filters are made."""
    raise DtZohoError(write.FILTERS_REFUSAL, exit_code=2)


# --------------------------------------------------------------------------------------------- drafts


@draft_app.command("save")
@_command
def draft_save(to: Optional[List[str]] = ToOpt, cc: Optional[List[str]] = CcOpt, bcc: Optional[List[str]] = BccOpt,
               subject: str = typer.Option("", "--subject", "-s"), body: Optional[str] = BodyOpt,
               body_file: Optional[Path] = BodyFileOpt, html_file: Optional[Path] = HtmlFileOpt,
               attach: Optional[List[Path]] = AttachOpt, from_: Optional[str] = FromOpt,
               account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Save a draft in Drafts. Nothing is sent. To change one, delete it and save it again."""
    to_list, cc_list, bcc_list = write.split_addresses(to), write.split_addresses(cc), write.split_addresses(bcc)
    content, mail_format = write.read_body(body, body_file, html_file)
    files = write.check_files(attach)
    client = _client()
    acct = mail.pick_account(client, account)
    aid = acct["account_id"]
    uploads = [write.upload(client, aid, f) for f in files]
    answer = write.send(client, aid, write.message_body(_from_address(acct, from_), to_list, cc_list, bcc_list,
                                                       subject, content, mail_format, uploads, {"mode": "draft"}))
    record = write.sent_record(answer, False)
    record.pop("scheduled_for")
    _report(dict(record, saved="Drafts"), json_output)


@draft_app.command("list")
@_command
def draft_list(count: int = CountOpt, start: int = StartOpt, fields: Optional[str] = FieldsOpt,
               account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List drafts, newest first."""
    chosen_fields = _message_fields(fields, False, json_output)
    client = _client()
    acct = mail.pick_account(client, account)
    drafts = mail.resolve_folder(client, acct["account_id"], "Drafts")
    records = mail.list_messages(client, acct["account_id"], drafts["id"], start, count, "all", None, None, False)
    _print_messages(records, len(records), start, count, chosen_fields, json_output,
                    {"account": acct["email"], "folder": drafts["path"], "folder_id": drafts["id"]},
                    f"dt-zoho draft list --count {count}", drafts["path"])


@draft_app.command("send", context_settings={"allow_extra_args": True, "ignore_unknown_options": True})
@_command
def draft_send(ctx: typer.Context) -> None:
    """Not possible through Zoho's API: says so and how to send it instead."""
    raise DtZohoError("Zoho's API has no call to send a saved draft. Send the message with dt-zoho send, then "
                      "delete the draft with: dt-zoho draft delete <id>", exit_code=2)


@draft_app.command("delete")
@_command
def draft_delete(message_ids: List[str] = IdsArg, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """Delete drafts for good."""
    ids = write.split_ids(message_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    drafts = mail.resolve_folder(client, acct["account_id"], "Drafts")
    for mid in ids:
        write.delete_message(client, acct["account_id"], drafts["id"], mid, permanent=True)
    _report(_ids_record("deleted", ids, folder=drafts["path"], permanent=True), json_output)


# --------------------------------------------------------------------------------------------- managing messages


def _mark_body(as_: str) -> dict:
    if as_ not in write.MARKS:
        raise DtZohoError(f"--as must be one of {', '.join(write.MARKS)}, not '{as_}'.", exit_code=2)
    return write.MARKS[as_]


def _update(message_ids: List[str], body: dict, account: Optional[str]) -> tuple:
    """Check the ids, then send one updatemessage call. Returns (ids, client, account)."""
    ids = write.split_ids(message_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    write.update_messages(client, acct["account_id"], ids, body)
    return ids, client, acct


@app.command("mark")
@_command
def mark_command(message_ids: List[str] = IdsArg, as_: str = MarkOpt, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """Mark messages read or unread, or set or clear their flag."""
    ids, _, _ = _update(message_ids, _mark_body(as_), account)
    _report(_ids_record("marked", ids, **{"as": as_}), json_output)


def _move(message_ids: List[str], to: str, account: Optional[str]) -> tuple:
    ids = write.split_ids(message_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    dest = mail.resolve_folder(client, acct["account_id"], to)
    write.update_messages(client, acct["account_id"], ids, {"mode": "moveMessage", "destfolderId": dest["id"]})
    return ids, dest


@app.command("move")
@_command
def move_command(message_ids: List[str] = IdsArg,
                 to: str = typer.Option(..., "--to", help="The folder to move them to: name, path or id."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Move messages to another folder."""
    ids, dest = _move(message_ids, to, account)
    _report(_ids_record("moved", ids, to=dest["path"]), json_output)


@app.command("archive")
@_command
def archive_command(message_ids: List[str] = IdsArg, account: Optional[str] = AccountOpt,
                    json_output: bool = JsonOpt) -> None:
    """Archive messages: Zoho moves them into the Archive folder."""
    ids, _, _ = _update(message_ids, {"mode": "archiveMails"}, account)
    _report(_ids_record("archived", ids), json_output)


@app.command("unarchive")
@_command
def unarchive_command(message_ids: List[str] = IdsArg,
                      to: str = typer.Option("Inbox", "--to", help="The folder to move them back to."),
                      account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Move archived messages out of Archive. (Zoho's own unarchive call answers OK and changes nothing, so this is
    a move.)"""
    ids, dest = _move(message_ids, to, account)
    _report(_ids_record("unarchived", ids, to=dest["path"]), json_output)


@app.command("delete")
@_command
def delete_command(message_ids: List[str] = IdsArg, folder: str = FolderOpt,
                   permanent: bool = typer.Option(False, "--permanent", help="Remove for good instead of moving to "
                                                                             "Trash."),
                   account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Delete messages: move them to Trash, or with --permanent remove them for good."""
    ids = write.split_ids(message_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    chosen = mail.resolve_folder(client, acct["account_id"], folder)
    if not permanent and (chosen["type"] or "").lower() == "trash":
        raise DtZohoError(f"these messages are already in {chosen['path']}. To remove them for good: "
                          f"dt-zoho delete {' '.join(ids)} --folder \"{chosen['path']}\" --permanent", exit_code=2)
    for mid in ids:
        write.delete_message(client, acct["account_id"], chosen["id"], mid, permanent)
    _report(_ids_record("deleted", ids, folder=chosen["path"], permanent=permanent), json_output)


@app.command("spam")
@_command
def spam_command(message_ids: List[str] = IdsArg, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """Mark messages as spam: Zoho moves them into Spam."""
    ids, _, _ = _update(message_ids, {"mode": "moveToSpam"}, account)
    _report(_ids_record("spam", ids), json_output)


@app.command("not-spam")
@_command
def not_spam_command(message_ids: List[str] = IdsArg, account: Optional[str] = AccountOpt,
                     json_output: bool = JsonOpt) -> None:
    """Mark messages as not spam: Zoho moves them back to Inbox."""
    ids, _, _ = _update(message_ids, {"mode": "markNotSpam"}, account)
    _report(_ids_record("not_spam", ids), json_output)


# --------------------------------------------------------------------------------------------- labels


LabelOpt = typer.Option(..., "--label", help="The label: name or id (dt-zoho labels).")
ColorOpt = typer.Option("#1E90FF", "--color", help="The label's colour, as #RRGGBB.")


def _check_color(color: str) -> None:
    hex_part = color[1:] if color.startswith("#") else ""
    if len(hex_part) != 6 or any(c not in "0123456789abcdefABCDEF" for c in hex_part):
        raise DtZohoError(f"--color must be #RRGGBB, not '{color}'.", exit_code=2)


@label_app.command("create")
@_command
def label_create(name: str = typer.Argument(..., help="The new label's name."), color: str = ColorOpt,
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Create a label."""
    _check_color(color)
    client = _client()
    acct = mail.pick_account(client, account)
    created = write.create_label(client, acct["account_id"], name, color)
    _report({"created": True, "id": str(created.get("labelId")), "name": name, "color": color}, json_output)


@label_app.command("rename")
@_command
def label_rename(label: str = typer.Argument(..., help="The label: name or id."),
                 name: Optional[str] = typer.Option(None, "--name", help="Its new name."),
                 color: Optional[str] = typer.Option(None, "--color", help="Its new colour, as #RRGGBB."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Rename a label or change its colour."""
    if name is None and color is None:
        raise DtZohoError("pass --name, --color or both.", exit_code=2)
    if color is not None:
        _check_color(color)
    client = _client()
    acct = mail.pick_account(client, account)
    found = mail.resolve_label(client, acct["account_id"], label)
    new_name, new_color = name or found["name"], color or found["color"]
    write.update_label(client, acct["account_id"], found["id"], new_name, new_color)
    _report({"id": found["id"], "name": new_name, "color": new_color}, json_output)


@label_app.command("delete")
@_command
def label_delete(label: str = typer.Argument(..., help="The label: name or id."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Delete a label. The messages it was on stay where they are."""
    client = _client()
    acct = mail.pick_account(client, account)
    found = mail.resolve_label(client, acct["account_id"], label)
    write.delete_label(client, acct["account_id"], found["id"])
    _report({"deleted": True, "id": found["id"], "name": found["name"]}, json_output)


def _label_messages(message_ids: List[str], label: str, mode: str, account: Optional[str]) -> tuple:
    ids = write.split_ids(message_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    found = mail.resolve_label(client, acct["account_id"], label)
    write.update_messages(client, acct["account_id"], ids, {"mode": mode, "labelId": [found["id"]]})
    return ids, found


@label_app.command("apply")
@_command
def label_apply(message_ids: List[str] = IdsArg, label: str = LabelOpt, account: Optional[str] = AccountOpt,
                json_output: bool = JsonOpt) -> None:
    """Put a label on messages."""
    ids, found = _label_messages(message_ids, label, "applyLabel", account)
    _report(_ids_record("labelled", ids, label=found["name"]), json_output)


@label_app.command("remove")
@_command
def label_remove(message_ids: List[str] = IdsArg, label: str = LabelOpt, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """Take a label off messages."""
    ids, found = _label_messages(message_ids, label, "removeLabel", account)
    _report(_ids_record("unlabelled", ids, label=found["name"]), json_output)


@label_app.command("clear")
@_command
def label_clear(message_ids: List[str] = IdsArg, account: Optional[str] = AccountOpt,
                json_output: bool = JsonOpt) -> None:
    """Take every label off messages."""
    ids, _, _ = _update(message_ids, {"mode": "removeAllLabels"}, account)
    _report(_ids_record("cleared", ids), json_output)


# --------------------------------------------------------------------------------------------- folders


FolderArg = typer.Argument(..., help="The folder: name, path such as /Inbox/Sub, or id (dt-zoho folders).")


def _folder(folder: str, account: Optional[str]) -> tuple:
    client = _client()
    acct = mail.pick_account(client, account)
    return client, acct, mail.resolve_folder(client, acct["account_id"], folder)


@folder_app.command("create")
@_command
def folder_create(name: str = typer.Argument(..., help="The new folder's name."),
                  parent: Optional[str] = typer.Option(None, "--parent", help="Create it inside this folder."),
                  account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Create a folder, at the top or inside another."""
    if "/" in name:
        raise DtZohoError("a folder name cannot hold '/'. To create one inside another, pass --parent.", exit_code=2)
    client = _client()
    acct = mail.pick_account(client, account)
    parent_id = mail.resolve_folder(client, acct["account_id"], parent)["id"] if parent is not None else None
    created = write.create_folder(client, acct["account_id"], name, parent_id)
    _report({"created": True, "id": str(created.get("folderId")), "name": name,
             "path": created.get("path"), "parent_id": parent_id}, json_output)


@folder_app.command("rename")
@_command
def folder_rename(folder: str = FolderArg, name: str = typer.Option(..., "--name", help="Its new name."),
                  account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Rename a folder."""
    client, acct, chosen = _folder(folder, account)
    write.update_folder(client, acct["account_id"], chosen["id"], {"mode": "rename", "folderName": name})
    _report({"id": chosen["id"], "was": chosen["path"], "name": name}, json_output)


@folder_app.command("move")
@_command
def folder_move(folder: str = FolderArg,
                to_top: bool = typer.Option(False, "--to-top", help="Move it out of its parent to the top level."),
                account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Move a nested folder to the top level. (Moving it under another folder was not proven, so is not offered.)"""
    if not to_top:
        raise DtZohoError("pass --to-top: moving a folder to the top level is the move dt-zoho offers.", exit_code=2)
    client, acct, chosen = _folder(folder, account)
    write.update_folder(client, acct["account_id"], chosen["id"], {"mode": "move"})
    _report({"id": chosen["id"], "was": chosen["path"], "moved_to": "top"}, json_output)


@folder_app.command("mark-read")
@_command
def folder_mark_read(folder: str = FolderArg, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Mark every message in a folder read."""
    client, acct, chosen = _folder(folder, account)
    write.update_folder(client, acct["account_id"], chosen["id"], {"mode": "markAsRead"})
    _report({"id": chosen["id"], "path": chosen["path"], "marked": "read"}, json_output)


@folder_app.command("empty")
@_command
def folder_empty(folder: str = FolderArg, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Delete every message in a folder. The folder stays."""
    client, acct, chosen = _folder(folder, account)
    write.update_folder(client, acct["account_id"], chosen["id"], {"mode": "emptyFolder"})
    _report({"id": chosen["id"], "path": chosen["path"], "emptied": True}, json_output)


@folder_app.command("imap")
@_command
def folder_imap(folder: str = FolderArg,
                show: bool = typer.Option(False, "--show", help="Show the folder to IMAP programs."),
                hide: bool = typer.Option(False, "--hide", help="Hide the folder from IMAP programs."),
                account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Show a folder to IMAP programs (dt-email, a mail app) or hide it from them."""
    if show == hide:
        raise DtZohoError("pass --show or --hide.", exit_code=2)
    client, acct, chosen = _folder(folder, account)
    write.update_folder(client, acct["account_id"], chosen["id"],
                        {"mode": "setViewableInIMAP" if show else "removeViewableInIMAP"})
    _report({"id": chosen["id"], "path": chosen["path"], "imap_view": show}, json_output)


@folder_app.command("delete")
@_command
def folder_delete(folder: str = FolderArg, account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Delete a folder."""
    client, acct, chosen = _folder(folder, account)
    write.delete_folder(client, acct["account_id"], chosen["id"])
    _report({"deleted": True, "id": chosen["id"], "path": chosen["path"]}, json_output)


# --------------------------------------------------------------------------------------------- threads


ThreadIdsArg = typer.Argument(..., help="Thread ids (the thread_id column of dt-zoho list --fields), space or comma "
                                        "separated.")


@thread_app.command("mark")
@_command
def thread_mark(thread_ids: List[str] = ThreadIdsArg, as_: str = MarkOpt, account: Optional[str] = AccountOpt,
                json_output: bool = JsonOpt) -> None:
    """Mark every message in threads read or unread, or set or clear their flag."""
    body = _mark_body(as_)
    ids = write.split_ids(thread_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    write.update_threads(client, acct["account_id"], ids, body)
    _report(_ids_record("threads", ids, marked=as_), json_output)


@thread_app.command("label")
@_command
def thread_label(thread_ids: List[str] = ThreadIdsArg,
                 apply: Optional[str] = typer.Option(None, "--apply", help="Put this label (name or id) on them."),
                 remove: Optional[str] = typer.Option(None, "--remove", help="Take this label (name or id) off."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Put a label on every message in threads, or take it off."""
    if (apply is None) == (remove is None):
        raise DtZohoError("pass --apply <label> or --remove <label>.", exit_code=2)
    ids = write.split_ids(thread_ids)
    client = _client()
    acct = mail.pick_account(client, account)
    found = mail.resolve_label(client, acct["account_id"], apply or remove)
    write.update_threads(client, acct["account_id"], ids,
                         {"mode": "applyLabel" if apply else "removeLabel", "labelId": [found["id"]]})
    _report(_ids_record("threads", ids, label=found["name"], applied=apply is not None), json_output)
