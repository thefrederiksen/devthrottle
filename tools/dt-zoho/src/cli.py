"""Command line for dt-zoho: Zoho's own Mail, Calendar and admin API.

Every command that reaches Zoho reads the Zoho key from three environment variables supplied by cc-secrets:

    cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho list
"""

from __future__ import annotations

import functools
import shlex
import sys
import time
from pathlib import Path
from typing import Callable, List, Optional

import typer
from cc_shared.axi_output import format_help

from . import __version__
from . import auth, config, mail, output
from .client import ZohoClient
from .errors import DtZohoError

TOOL = "dt-zoho"

app = typer.Typer(name=TOOL, add_completion=False, no_args_is_help=True,
                  help="Zoho Mail, Calendar and admin through Zoho's own API. The key comes from cc-secrets: "
                       f"{config.SECRETS_COMMAND} <command>.")
settings_app = typer.Typer(no_args_is_help=True, help="The Zoho data centre dt-zoho talks to (no secret is stored).")
signin_app = typer.Typer(no_args_is_help=True, help="Check the sign-in, or forget the kept access token.")
app.add_typer(settings_app, name="settings")
app.add_typer(signin_app, name="signin")

AccountOpt = typer.Option(None, "--account", "-a", help="The mail account's address. Default: the key's default "
                                                        "account (dt-zoho accounts).")
JsonOpt = typer.Option(False, "--json", help="Print JSON with every field.")
FolderOpt = typer.Option("Inbox", "--folder", "-f", help="The folder the message is in: a name, a path such as "
                                                         "/Inbox/Sub, or a folder id (dt-zoho folders).")
CountOpt = typer.Option(20, "--count", "-n", min=1, max=200, help="How many messages to ask Zoho for (1 to 200).")
StartOpt = typer.Option(1, "--start", min=1, help="The position of the first message to show, for the next page.")
FieldsOpt = typer.Option(None, "--fields", help="Fields to show: " + ",".join(mail.SUMMARY_FIELDS) + ".")
FlaggedOpt = typer.Option(False, "--flagged", help="Only flagged messages, of the ones fetched.")


# --------------------------------------------------------------------------------------------- plumbing


def _fail(message: str, code: int = 1) -> None:
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
        except DtZohoError as exc:
            _fail(exc.message, exc.exit_code)
    return wrapper


def _client() -> ZohoClient:
    return ZohoClient.from_environment()


def _report(data: dict, json_output: bool) -> None:
    if json_output:
        output.emit_json(data)
    else:
        output.emit([output.kv_block(
            (k, ", ".join(map(str, v)) if isinstance(v, list) else v) for k, v in data.items())])


# The same refusal, in the same words, as cc-devthrottle gives (tools/cc-devthrottle/src/axi_cli.py).
FIELDS_WITH_JSON = ("--fields does not apply to --json, which always carries every field. "
                    "Use --fields to show a few fields, or --json to get every field.")


def _message_fields(fields: Optional[str], across_folders: bool, json_output: bool) -> List[str]:
    """The message fields to show, checked before anything reaches Zoho so a typo costs no token."""
    from cc_shared.axi_output import FieldsError, parse_fields

    if fields is not None and json_output:
        raise DtZohoError(FIELDS_WITH_JSON, 2)
    default = mail.SUMMARY_DEFAULT_ACROSS_FOLDERS if across_folders else mail.SUMMARY_DEFAULT
    try:
        return parse_fields(fields, mail.SUMMARY_FIELDS, default)
    except FieldsError as exc:
        raise DtZohoError(str(exc), 2) from exc


def _quote(text: str) -> str:
    """Quote a value for a command the user can paste back."""
    return f'"{text}"' if '"' not in text else shlex.quote(text)


def _print_messages(records: List[dict], fetched: int, start: int, count: int, chosen: List[str],
                    json_output: bool, extra: dict, next_command: str, read_folder: Optional[str]) -> None:
    more = fetched == count
    if json_output:
        output.emit_json({"count": len(records), "fetched": fetched, "start": start,
                          "next_start": start + fetched if more else None, **extra, "messages": records})
        return
    help_lines = []
    if records:
        folder_part = f" --folder {_quote(read_folder)}" if read_folder else " --folder <folder_id>"
        help_lines.append(f"dt-zoho read <id>{folder_part}")
    if len(records) != fetched:
        help_lines.append(f"{fetched - len(records)} of the {fetched} messages fetched were left out by the filter")
    if more:
        help_lines.append(f"more may exist: {next_command} --start {start + fetched}")
    output.emit_list("messages", chosen, records, None, help_lines)


# --------------------------------------------------------------------------------------------- top level


def _version(value: bool) -> None:
    if value:
        typer.echo(f"{TOOL} {__version__}")
        raise typer.Exit()


@app.callback()
def main(version: bool = typer.Option(False, "--version", callback=_version, is_eager=True,
                                      help="Print the version and exit.")) -> None:
    """Zoho Mail, Calendar and admin through Zoho's own API."""


# ----- settings


@settings_app.command("show")
@_command
def settings_show(json_output: bool = JsonOpt) -> None:
    """Show the data centre and where the settings are kept."""
    settings = config.load()
    data = {"data_centre": settings.data_centre or "not set", "settings_file": str(config.config_path())}
    if settings.data_centre in config.DATA_CENTRES:
        centre = config.DATA_CENTRES[settings.data_centre]
        data.update({"sign_in": centre.accounts, "mail": centre.mail, "calendar": centre.calendar})
    _report(data, json_output)


@settings_app.command("set")
@_command
def settings_set(data_centre: str = typer.Option(..., "--data-centre",
                                                 help="Zoho's data centre: " + ", ".join(config.DATA_CENTRES) +
                                                      ". Canada is ca."),
                 json_output: bool = JsonOpt) -> None:
    """Set the Zoho data centre. A key made in one data centre is refused by the others."""
    code = data_centre.strip().lower()
    if code not in config.DATA_CENTRES:
        raise DtZohoError(f"--data-centre must be one of {', '.join(config.DATA_CENTRES)}, not '{data_centre}'.", 2)
    settings = config.load()
    changed = settings.data_centre != code
    settings.data_centre = code
    path = config.save(settings)
    if changed:
        auth.forget()       # a token from another data centre is useless here
    _report({"data_centre": code, "settings_file": str(path)}, json_output)


# ----- sign-in


@signin_app.command("check")
@_command
def signin_check(json_output: bool = JsonOpt) -> None:
    """Sign in (reusing the kept access token when it has time left) and show which account the key reaches."""
    client = _client()
    token = client.token()
    records = [mail.account_record(a) for a in mail.accounts(client)]
    default = next((r for r in records if r["default"]), records[0] if records else None)
    data = {"signed_in": True, "data_centre": client.centre.code, "token": token.source,
            "token_minutes_left": int((token.expires_at - time.time()) // 60),
            "scopes": token.scope.split() if token.scope else [], "accounts": len(records),
            "default_account": default["email"] if default else None,
            "token_file": str(auth.cache_path())}
    if not token.scope:
        data.pop("scopes")
    _report(data, json_output)


@signin_app.command("forget")
@_command
def signin_forget(json_output: bool = JsonOpt) -> None:
    """Delete the kept access token. The next command asks Zoho for a new one."""
    _report({"forgotten": auth.forget(), "token_file": str(auth.cache_path())}, json_output)


# ----- reading


@app.command("accounts")
@_command
def accounts_command(json_output: bool = JsonOpt) -> None:
    """List the mail accounts the key reaches, with every address each one receives."""
    records = [mail.account_record(a) for a in mail.accounts(_client())]
    if json_output:
        output.emit_json({"count": len(records), "accounts": records})
        return
    rows = [dict(r, addresses=" ".join(r["addresses"])) for r in records]
    output.emit_list("accounts", ["account_id", "email", "default", "imap_enabled", "addresses"], rows, None)


@app.command("folders")
@_command
def folders_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List the folders, nested ones by their path."""
    client = _client()
    acct = mail.pick_account(client, account)
    records = [mail.folder_record(f) for f in mail.folders(client, acct["account_id"])]
    if json_output:
        output.emit_json({"count": len(records), "account": acct["email"], "folders": records})
        return
    output.emit_list("folders", ["id", "path", "type", "imap_view"], records, None,
                     ['dt-zoho list --folder "<path>"'] if records else [])


@app.command("labels")
@_command
def labels_command(account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """List the labels (Zoho's tags)."""
    client = _client()
    acct = mail.pick_account(client, account)
    records = [mail.label_record(l) for l in mail.labels(client, acct["account_id"])]
    if json_output:
        output.emit_json({"count": len(records), "account": acct["email"], "labels": records})
        return
    output.emit_list("labels", ["id", "name", "color"], records, None,
                     ['dt-zoho list --label "<name>"'] if records else [])


@app.command("list")
@_command
def list_command(folder: Optional[str] = typer.Option(None, "--folder", "-f",
                                                      help="Folder name, path or id. Default: Inbox, or every folder "
                                                           "when --label or --thread is given."),
                 count: int = CountOpt, start: int = StartOpt,
                 unread: bool = typer.Option(False, "--unread", help="Only unread messages."),
                 read: bool = typer.Option(False, "--read", help="Only read messages."),
                 flagged: bool = FlaggedOpt,
                 label: Optional[str] = typer.Option(None, "--label", help="Only messages with this label (name or id)."),
                 thread: Optional[str] = typer.Option(None, "--thread", help="Only messages in this thread (a "
                                                                             "thread_id from --fields)."),
                 include_archive: bool = typer.Option(False, "--include-archive",
                                                      help="Include archived messages."),
                 fields: Optional[str] = FieldsOpt, account: Optional[str] = AccountOpt,
                 json_output: bool = JsonOpt) -> None:
    """List messages, newest first."""
    if unread and read:
        raise DtZohoError("pass --unread or --read, not both.", 2)
    if folder is None and label is None and thread is None:
        folder = "Inbox"
    chosen_fields = _message_fields(fields, folder is None, json_output)
    client = _client()
    acct = mail.pick_account(client, account)
    aid = acct["account_id"]
    chosen = mail.resolve_folder(client, aid, folder) if folder is not None else None
    label_id = mail.resolve_label(client, aid, label)["id"] if label is not None else None
    status = "unread" if unread else "read" if read else "all"
    fetched = mail.list_messages(client, aid, chosen["id"] if chosen else None, start, count, status, label_id,
                                 thread, include_archive)
    records = [r for r in fetched if mail.flag_matches(r, flagged)]
    extra = {"account": acct["email"], "folder": chosen["path"] if chosen else None,
             "folder_id": chosen["id"] if chosen else None, "label_id": label_id, "thread_id": thread}
    _print_messages(records, len(fetched), start, count, chosen_fields, json_output, extra,
                    _again("dt-zoho list", folder=chosen["path"] if chosen else None, label=label, thread=thread,
                           count=count, switches=(("--unread", unread), ("--read", read), ("--flagged", flagged),
                                                  ("--include-archive", include_archive))),
                    chosen["path"] if chosen else None)


def _again(command: str, count: int, switches, folder: Optional[str] = None, label: Optional[str] = None,
           thread: Optional[str] = None) -> str:
    """The same command again with every filter kept, for the next-page hint."""
    parts = [command]
    if folder:
        parts.append(f"--folder {_quote(folder)}")
    if label:
        parts.append(f"--label {_quote(label)}")
    if thread:
        parts.append(f"--thread {thread}")
    parts += [flag for flag, on in switches if on]
    parts.append(f"--count {count}")
    return " ".join(parts)


@app.command("search")
@_command
def search_command(query: str = typer.Argument(..., help="Zoho search words: sender:x, subject:x, to:x, cc:x, "
                                                         "entire:x, has:attachment, in:Sent; join with :: for AND "
                                                         "or ::or: for OR. Example: subject:invoice::has:attachment"),
                   count: int = CountOpt, start: int = StartOpt,
                   unread: bool = typer.Option(False, "--unread", help="Only unread messages, of the ones found."),
                   read: bool = typer.Option(False, "--read", help="Only read messages, of the ones found."),
                   flagged: bool = typer.Option(False, "--flagged", help="Only flagged messages, of the ones found."),
                   fields: Optional[str] = FieldsOpt, account: Optional[str] = AccountOpt,
                   json_output: bool = JsonOpt) -> None:
    """Search every folder with Zoho's search words, newest first."""
    if unread and read:
        raise DtZohoError("pass --unread or --read, not both.", 2)
    mail.check_search_key(query)
    chosen_fields = _message_fields(fields, True, json_output)
    client = _client()
    acct = mail.pick_account(client, account)
    fetched = mail.search(client, acct["account_id"], query, start, count)
    records = [r for r in fetched if mail.flag_matches(r, flagged) and mail.read_matches(r, unread, read)]
    _print_messages(records, len(fetched), start, count, chosen_fields, json_output,
                    {"account": acct["email"], "query": query},
                    _again(f"dt-zoho search {_quote(query)}", count=count,
                           switches=(("--unread", unread), ("--read", read), ("--flagged", flagged))), None)


@app.command("read")
@_command
def read_command(message_id: str = typer.Argument(..., help="The message id (dt-zoho list)."),
                 folder: str = FolderOpt,
                 html_body: bool = typer.Option(False, "--html", help="Show the body as Zoho's HTML, not as text."),
                 headers: bool = typer.Option(False, "--headers", help="Add every header line."),
                 raw: bool = typer.Option(False, "--raw", help="Print only the whole original message (MIME)."),
                 save: Optional[Path] = typer.Option(None, "--save", help="With --raw: write it to this file "
                                                                          "instead (an .eml file)."),
                 account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Read one message: who, when, subject, attachments and the body. Zoho does not mark it read."""
    if save is not None and not raw:
        raise DtZohoError("--save goes with --raw.", 2)
    client = _client()
    acct = mail.pick_account(client, account)
    aid = acct["account_id"]
    if raw:
        mime = mail.original(client, aid, message_id)
        if save is not None:
            if save.exists():
                raise DtZohoError(f"{save} already exists; dt-zoho does not overwrite a file.", 2)
            save.write_bytes(mime.encode("utf-8"))
            _report({"saved": str(save), "bytes": save.stat().st_size, "id": message_id}, json_output)
        elif json_output:
            output.emit_json({"id": message_id, "original": mime})
        else:
            sys.stdout.write(output.ascii_text(mime) + "\n")
        return
    chosen = mail.resolve_folder(client, aid, folder)
    fid = chosen["id"]
    meta = mail.summary_record(dict(mail.details(client, aid, fid, message_id), folderId=fid))
    markup = mail.content(client, aid, fid, message_id)
    files = mail.attachment_info(client, aid, fid, message_id) if meta["attachments"] else []
    data = dict(meta, id=message_id, folder=chosen["path"], attachments=files,
                body=markup if html_body else mail.html_to_text(markup))
    data.pop("summary", None)
    if headers:
        data["headers"] = mail.header(client, aid, fid, message_id)
    if json_output:
        data["body_html"] = markup
        output.emit_json(data)
        return
    pairs = [(k, data[k] if data[k] != "" else None)
             for k in ("id", "folder", "thread_id", "received", "from", "to", "cc", "subject", "read", "flag", "size")]
    pairs.append(("attachments", ", ".join(f"{a['name']} ({a['size']} bytes, id {a['id']})" for a in files) or None))
    blocks = [output.kv_block(pairs)]
    if headers:
        blocks.append("headers:\n" + output.ascii_text(data["headers"]).rstrip())
    blocks.append("body:\n" + output.ascii_text(data["body"]))
    if files:
        blocks.append(format_help([f'dt-zoho download {message_id} --folder "{chosen["path"]}"']))
    output.emit(blocks)


@app.command("attachments")
@_command
def attachments_command(message_id: str = typer.Argument(..., help="The message id (dt-zoho list)."),
                        folder: str = FolderOpt, account: Optional[str] = AccountOpt,
                        json_output: bool = JsonOpt) -> None:
    """List a message's attachments."""
    client = _client()
    acct = mail.pick_account(client, account)
    chosen = mail.resolve_folder(client, acct["account_id"], folder)
    files = mail.attachment_info(client, acct["account_id"], chosen["id"], message_id)
    if json_output:
        output.emit_json({"count": len(files), "id": message_id, "folder": chosen["path"], "attachments": files})
        return
    output.emit_list("attachments", ["id", "name", "size"], files, None,
                     [f'dt-zoho download {message_id} --folder "{chosen["path"]}"'] if files else [])


@app.command("download")
@_command
def download_command(message_id: str = typer.Argument(..., help="The message id (dt-zoho list)."),
                     folder: str = FolderOpt,
                     attachment: Optional[List[str]] = typer.Option(None, "--attachment", help="Only this attachment, "
                                                                    "by id or file name. Repeat for several. "
                                                                    "Default: all of them."),
                     out_dir: Path = typer.Option(Path("."), "--out-dir", "-o", help="The folder to write into."),
                     account: Optional[str] = AccountOpt, json_output: bool = JsonOpt) -> None:
    """Download a message's attachments into a folder. Never overwrites a file."""
    if not out_dir.is_dir():
        raise DtZohoError(f"--out-dir {out_dir} is not a folder.", 2)
    client = _client()
    acct = mail.pick_account(client, account)
    aid = acct["account_id"]
    chosen = mail.resolve_folder(client, aid, folder)
    files = mail.attachment_info(client, aid, chosen["id"], message_id)
    if attachment:
        wanted = set(attachment)
        picked = [f for f in files if f["id"] in wanted or f["name"] in wanted]
        missing = wanted - {f["id"] for f in picked} - {f["name"] for f in picked}
        if missing:
            raise DtZohoError(f"message {message_id} has no attachment {', '.join(sorted(missing))}. It has: "
                              f"{', '.join(f['name'] for f in files) or 'none'}.", 2)
        files = picked
    if not files:
        raise DtZohoError(f"message {message_id} has no attachments.", 2)
    names = [Path(f["name"]).name for f in files]
    if len(set(names)) != len(names):
        raise DtZohoError("two attachments have the same file name; download them one at a time with "
                          "--attachment <id> --out-dir <a different folder>.", 2)
    targets = [out_dir / n for n in names]
    taken = [str(t) for t in targets if t.exists()]
    if taken:
        raise DtZohoError(f"{', '.join(taken)} already exist; dt-zoho does not overwrite a file. Pick another "
                          "--out-dir.", 2)
    saved = []
    for item, target in zip(files, targets):
        data = mail.attachment_bytes(client, aid, chosen["id"], message_id, item["id"])
        target.write_bytes(data)
        saved.append({"id": item["id"], "name": item["name"], "bytes": len(data), "path": str(target),
                      "crlf_text": b"\r\n" in data and b"\0" not in data})
    help_lines = []
    if any(s["crlf_text"] for s in saved):
        help_lines.append("a text file came back with CRLF line endings: Zoho rewrites LF to CRLF in text "
                          "attachments, so it can differ from what was sent; binary files come back byte for byte")
    if json_output:
        output.emit_json({"count": len(saved), "id": message_id, "folder": chosen["path"], "saved": saved})
        return
    output.emit_list("saved", ["name", "bytes", "path"], saved, None, help_lines)


# The writing commands register on `app` when their module loads.
from . import cli_write  # noqa: E402,F401


def tool_main() -> None:
    """The console-script entry point, through the shared failure reporter."""
    from cc_shared.tool_errors import run_tool

    run_tool(lambda: app(prog_name=TOOL), TOOL, app=app)
