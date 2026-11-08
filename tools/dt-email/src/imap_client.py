"""The IMAP side of dt-email: folders, listing, reading, searching, flags, moving, deleting, drafts.

`MailStore` wraps an `imaplib.IMAP4` connection (or a fake with the same methods, in tests) and turns
its raw answers into plain records. It uses only standard IMAP4rev1 plus three common extensions:
MOVE (RFC 6851), UIDPLUS (RFC 4315) and SPECIAL-USE (RFC 6154). A server without MOVE or UIDPLUS gets a
clear error for the commands that need them; nothing is silently done another way.

Things learned on a real server (Zoho) that this code is shaped around:
- SEARCH works from an index that lags a write by up to about a minute; FETCH by UID does not. So every
  write reports the UID the server returned (APPENDUID), and nothing searches right after a write.
- `UID STORE` needs its flags in parentheses, or the server answers BAD.
- FETCH items arrive in any order (FLAGS before UID), and may follow the literal.
- An Archive folder may have no \\Archive flag; it is then found by its name.
"""

from __future__ import annotations

import base64
import imaplib
import re
import time
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

from .config import Account
from .errors import DtEmailError

TIMEOUT_SECONDS = 60

SPECIAL_FLAGS = {
    "drafts": "\\Drafts",
    "sent": "\\Sent",
    "trash": "\\Trash",
    "junk": "\\Junk",
    "archive": "\\Archive",
    "all": "\\All",
    "flagged": "\\Flagged",
}
# A folder with no special-use flag is found by these exact names, in this order.
SPECIAL_NAMES = {
    "drafts": ("Drafts",),
    "sent": ("Sent",),
    "trash": ("Trash",),
    "junk": ("Junk", "Spam"),
    "archive": ("Archive",),
}

SUMMARY_HEADERS = "FROM TO CC SUBJECT DATE MESSAGE-ID"


# --------------------------------------------------------------------------------------------- names


def encode_mailbox(name: str) -> str:
    """A folder name in IMAP modified UTF-7 (RFC 3501 5.1.3)."""
    out: List[str] = []
    buffer: List[str] = []

    def flush() -> None:
        if buffer:
            raw = "".join(buffer).encode("utf-16-be")
            out.append("&" + base64.b64encode(raw).decode("ascii").rstrip("=").replace("/", ",") + "-")
            buffer.clear()

    for ch in name:
        if 0x20 <= ord(ch) <= 0x7E:
            flush()
            out.append("&-" if ch == "&" else ch)
        else:
            buffer.append(ch)
    flush()
    return "".join(out)


def decode_mailbox(name: str) -> str:
    """The reverse of `encode_mailbox`."""
    def replace(match: "re.Match[str]") -> str:
        body = match.group(1)
        if body == "":
            return "&"
        data = body.replace(",", "/")
        data += "=" * (-len(data) % 4)
        return base64.b64decode(data).decode("utf-16-be")

    return re.sub(r"&([A-Za-z0-9+,]*)-", replace, name)


def quote(text: str) -> str:
    """An IMAP quoted string."""
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def mailbox_arg(name: str) -> str:
    return quote(encode_mailbox(name))


def uid_set(uids: Sequence[int]) -> str:
    if not uids:
        raise DtEmailError("no message UIDs given.", exit_code=2)
    return ",".join(str(u) for u in uids)


# --------------------------------------------------------------------------------------------- records


@dataclass
class Folder:
    name: str
    delimiter: Optional[str]
    flags: List[str]
    special: Optional[str]
    messages: Optional[int] = None
    unseen: Optional[int] = None

    @property
    def selectable(self) -> bool:
        return not any(f.lower() == "\\noselect" for f in self.flags) and \
            not any(f.lower() == "\\nonexistent" for f in self.flags)


@dataclass
class FetchItem:
    uid: int
    text: str
    literal: Optional[bytes]

    def flags(self) -> List[str]:
        m = re.search(r"FLAGS \(([^)]*)\)", self.text)
        return m.group(1).split() if m and m.group(1).strip() else []

    def internal_date(self) -> Optional[str]:
        m = re.search(r'INTERNALDATE "([^"]+)"', self.text)
        return m.group(1) if m else None

    def size(self) -> Optional[int]:
        m = re.search(r"RFC822\.SIZE (\d+)", self.text)
        return int(m.group(1)) if m else None


_LIST_LINE = re.compile(r'^\((?P<flags>[^)]*)\) (?P<delim>"(?:[^"\\]|\\.)*"|NIL) ?(?P<name>.*)$')


def parse_list_response(data: Sequence) -> List[Folder]:
    """Folders from the answer to LIST. A name sent as a literal arrives as a (line, name) tuple."""
    folders: List[Folder] = []
    for item in data:
        if item is None:
            continue
        if isinstance(item, tuple):
            line = item[0].decode("ascii", "replace")
            literal_name: Optional[str] = item[1].decode("utf-8", "replace")
        else:
            line = item.decode("ascii", "replace")
            literal_name = None
        match = _LIST_LINE.match(line)
        if not match:
            raise DtEmailError(f"the server's folder list has a line dt-email cannot read: {line!r}")
        delim_raw = match.group("delim")
        delimiter = None if delim_raw == "NIL" else delim_raw[1:-1].replace('\\"', '"').replace("\\\\", "\\")
        if literal_name is not None:
            raw_name = literal_name
        else:
            raw_name = match.group("name").strip()
            if raw_name.startswith('"') and raw_name.endswith('"'):
                raw_name = raw_name[1:-1].replace('\\"', '"').replace("\\\\", "\\")
        flags = match.group("flags").split()
        special = None
        for key, flag in SPECIAL_FLAGS.items():
            if any(f.lower() == flag.lower() for f in flags):
                special = key
                break
        folders.append(Folder(name=decode_mailbox(raw_name), delimiter=delimiter, flags=flags, special=special))
    return folders


def parse_fetch_response(data: Sequence) -> List[FetchItem]:
    """Records from the answer to UID FETCH. Each message is a (prefix, literal) tuple followed by
    bytes that close it; a message with no literal is a bytes line alone. Items may come in any order."""
    records: List[Tuple[List[str], Optional[bytes]]] = []
    for item in data:
        if item is None:
            continue
        if isinstance(item, tuple):
            records.append(([item[0].decode("ascii", "replace")], item[1]))
        else:
            text = item.decode("ascii", "replace")
            if re.match(r"^\d+ \(", text):
                records.append(([text], None))
            elif records:
                records[-1][0].append(text)
    items: List[FetchItem] = []
    for parts, literal in records:
        text = " ".join(parts)
        m = re.search(r"\bUID (\d+)", text)
        if not m:
            raise DtEmailError(f"the server's FETCH answer carries no UID: {text[:200]!r}")
        items.append(FetchItem(uid=int(m.group(1)), text=text, literal=literal))
    return items


def _text(data: Sequence) -> str:
    return " ".join(d.decode("utf-8", "replace") if isinstance(d, bytes) else str(d)
                    for d in data if d is not None)


# --------------------------------------------------------------------------------------------- store


def connect(account: Account) -> imaplib.IMAP4:
    """Open the raw connection. Tests replace this function with one returning a fake."""
    if account.imap_security == "ssl":
        return imaplib.IMAP4_SSL(account.imap_host, account.imap_port, timeout=TIMEOUT_SECONDS)
    conn = imaplib.IMAP4(account.imap_host, account.imap_port, timeout=TIMEOUT_SECONDS)
    conn.starttls()
    return conn


class MailStore:
    """One signed-in IMAP connection. Use as a context manager so it always logs out."""

    def __init__(self, conn, account: Account, capabilities: Optional[List[str]] = None):
        self._conn = conn
        self._capabilities = capabilities
        self.account = account
        self._selected: Optional[Tuple[str, bool]] = None
        self._folders: Optional[List[Folder]] = None

    @classmethod
    def open(cls, account: Account, password: str) -> "MailStore":
        conn = connect(account)
        try:
            conn.login(account.user, password)
        except imaplib.IMAP4.error as exc:
            conn.shutdown()
            raise DtEmailError(
                f"the IMAP server {account.imap_host} refused the sign-in for {account.user}: {exc}. "
                "Check the password held in cc-secrets entry "
                f"'{account.secret_entry}', and that IMAP is switched on for this mailbox at the provider.") from exc
        # Many servers (Dovecot, Gmail) advertise MOVE and UIDPLUS only after login, and imaplib keeps the
        # list it read before login. Ask again now.
        typ, data = conn.capability()
        if typ != "OK":
            raise DtEmailError(f"the server refused CAPABILITY after sign-in: {_text(data)}")
        return cls(conn, account, list(dict.fromkeys(_text(data).upper().split())))

    def __enter__(self) -> "MailStore":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def close(self) -> None:
        if self._conn is not None:
            self._conn.logout()
            self._conn = None

    # ----- helpers

    def _ok(self, result, what: str) -> Sequence:
        typ, data = result
        if typ != "OK":
            raise DtEmailError(f"the server refused {what}: {_text(data)}")
        return data

    def capabilities(self) -> List[str]:
        """What the server offers after sign-in."""
        if self._capabilities is None:
            raise DtEmailError("dt-email defect: the capability list was not read after sign-in.")
        return self._capabilities

    def _require(self, capability: str, what: str) -> None:
        if capability not in self.capabilities():
            raise DtEmailError(
                f"{what} needs the IMAP {capability} extension, and {self.account.imap_host} does not offer it. "
                "dt-email does not do it another way.")

    def select(self, folder: str, readonly: bool = True) -> int:
        """Open a folder. A read-only open never changes flags such as \\Seen."""
        data = self._ok(self._conn.select(mailbox_arg(folder), readonly=readonly), f"opening folder '{folder}'")
        self._selected = (folder, readonly)
        return int(data[0]) if data and data[0] and data[0].isdigit() else 0

    def uid_validity(self, folder: str) -> int:
        """The folder's UIDVALIDITY. A UID means the same message only while this stays the same, so a reader that
        keeps a UID between runs keeps this beside it."""
        self.select(folder, readonly=True)
        _, data = self._conn.response("UIDVALIDITY")
        values = [v for v in (data or []) if v is not None]
        if not values:
            raise DtEmailError(f"the server did not say the UIDVALIDITY of folder '{folder}' when it was opened.")
        return int(values[-1])

    # ----- folders

    def folders(self, with_counts: bool = False) -> List[Folder]:
        if self._folders is None:
            self._folders = parse_list_response(self._ok(self._conn.list(), "listing folders"))
        if with_counts:
            for f in self._folders:
                if f.selectable and f.messages is None:
                    data = self._ok(self._conn.status(mailbox_arg(f.name), "(MESSAGES UNSEEN)"),
                                    f"counting folder '{f.name}'")
                    text = _text(data)
                    m = re.search(r"MESSAGES (\d+)", text)
                    u = re.search(r"UNSEEN (\d+)", text)
                    f.messages = int(m.group(1)) if m else None
                    f.unseen = int(u.group(1)) if u else None
        return self._folders

    def _find_special(self, kind: str) -> Optional[str]:
        folders = self.folders()
        for f in folders:
            if f.special == kind:
                return f.name
        for name in SPECIAL_NAMES.get(kind, ()):
            for f in folders:
                if f.name.lower() == name.lower():
                    return f.name
        return None

    def special_folder(self, kind: str) -> str:
        """The folder for drafts, sent, trash, junk or archive: by its special-use flag, else by name."""
        found = self._find_special(kind)
        if found is not None:
            return found
        raise DtEmailError(
            f"this mailbox has no {kind} folder: none carries the {SPECIAL_FLAGS[kind]} flag and none is named "
            f"{' or '.join(SPECIAL_NAMES.get(kind, ()))}. Create one with: dt-email folder create "
            f"{SPECIAL_NAMES.get(kind, (kind.title(),))[0]}")

    def create_folder(self, name: str) -> None:
        self._ok(self._conn.create(mailbox_arg(name)), f"creating folder '{name}'")
        self._folders = None

    def rename_folder(self, old: str, new: str) -> None:
        self._ok(self._conn.rename(mailbox_arg(old), mailbox_arg(new)), f"renaming folder '{old}' to '{new}'")
        self._folders = None

    def delete_folder(self, name: str) -> Optional[str]:
        """Delete a folder. Some servers (Zoho) keep it in Trash instead, under a name of their choosing;
        the folder that newly appeared in Trash is returned in that case, so the caller can say so."""
        before = {f.name: f for f in self.folders()}
        if name not in before:
            raise DtEmailError(f"there is no folder named '{name}'. List folders with: dt-email folders")
        delimiter = before[name].delimiter
        trash = self._find_special("trash")
        self._ok(self._conn.delete(mailbox_arg(name)), f"deleting folder '{name}'")
        self._folders = None
        if trash is None or delimiter is None or name == trash:
            return None
        appeared = [f.name for f in self.folders()
                    if f.name.startswith(trash + delimiter) and f.name not in before]
        return appeared[0] if len(appeared) == 1 else None

    # ----- messages

    def search(self, folder: str, criteria: List[str]) -> List[int]:
        """UIDs matching the criteria, oldest first."""
        self.select(folder, readonly=True)
        data = self._ok(self._conn.uid("SEARCH", None, *(criteria or ["ALL"])), f"searching folder '{folder}'")
        return sorted(int(x) for x in _text(data).split())

    def fetch_summaries(self, folder: str, uids: Sequence[int]) -> List[FetchItem]:
        if not uids:
            return []
        self.select(folder, readonly=True)
        data = self._ok(self._conn.uid(
            "FETCH", uid_set(uids),
            f"(UID FLAGS INTERNALDATE RFC822.SIZE BODY.PEEK[HEADER.FIELDS ({SUMMARY_HEADERS})])"),
            f"reading message headers in '{folder}'")
        return parse_fetch_response(data)

    def fetch_message(self, folder: str, uid: int) -> FetchItem:
        """The whole message, without marking it read."""
        self.select(folder, readonly=True)
        data = self._ok(self._conn.uid("FETCH", str(uid), "(UID FLAGS BODY.PEEK[])"),
                        f"reading message {uid} in '{folder}'")
        items = [i for i in parse_fetch_response(data) if i.uid == uid and i.literal is not None]
        if not items:
            raise DtEmailError(
                f"there is no message with UID {uid} in folder '{folder}'. "
                f"List the folder with: dt-email list --folder \"{folder}\"")
        return items[0]

    def existing_uids(self, folder: str, uids: Sequence[int]) -> List[int]:
        self.select(folder, readonly=True)
        data = self._ok(self._conn.uid("FETCH", uid_set(uids), "(UID)"), f"checking messages in '{folder}'")
        return sorted(i.uid for i in parse_fetch_response(data))

    def require_present(self, folder: str, uids: Sequence[int]) -> None:
        present = set(self.existing_uids(folder, uids))
        missing = [u for u in uids if u not in present]
        if missing:
            raise DtEmailError(
                f"no message with UID {', '.join(str(u) for u in missing)} in folder '{folder}'. Nothing was changed. "
                f"List the folder with: dt-email list --folder \"{folder}\"")

    def store_flags(self, folder: str, uids: Sequence[int], add: bool, flags: Sequence[str]) -> Dict[int, List[str]]:
        """Add or remove system flags. Returns each message's flags as the server now reports them."""
        self.require_present(folder, uids)
        self.select(folder, readonly=False)
        self._ok(self._conn.uid("STORE", uid_set(uids), ("+" if add else "-") + "FLAGS",
                                "(" + " ".join(flags) + ")"), f"changing flags in '{folder}'")
        data = self._ok(self._conn.uid("FETCH", uid_set(uids), "(UID FLAGS)"), f"reading flags in '{folder}'")
        return {i.uid: i.flags() for i in parse_fetch_response(data)}

    def move(self, folder: str, uids: Sequence[int], destination: str) -> None:
        self._require("MOVE", "Moving a message")
        self.require_present(folder, uids)
        self.select(folder, readonly=False)
        self._ok(self._conn.uid("MOVE", uid_set(uids), mailbox_arg(destination)),
                 f"moving messages from '{folder}' to '{destination}'")

    def delete_permanently(self, folder: str, uids: Sequence[int]) -> None:
        """Mark the messages deleted and expunge exactly those UIDs - never other deleted messages."""
        self._require("UIDPLUS", "Deleting a message permanently")
        self.require_present(folder, uids)
        self.select(folder, readonly=False)
        self._ok(self._conn.uid("STORE", uid_set(uids), "+FLAGS", "(\\Deleted)"), f"marking messages deleted in '{folder}'")
        self._ok(self._conn.uid("EXPUNGE", uid_set(uids)), f"expunging messages in '{folder}'")
        left = self.existing_uids(folder, uids)
        if left:
            raise DtEmailError(f"the server kept UID {', '.join(str(u) for u in left)} in '{folder}' after the expunge.")

    def append(self, folder: str, raw: bytes, flags: Sequence[str]) -> Optional[int]:
        """Store a message in a folder. Returns the UID the server gave it (APPENDUID), or None when the
        server does not say."""
        data = self._ok(self._conn.append(mailbox_arg(folder), "(" + " ".join(flags) + ")",
                                          imaplib.Time2Internaldate(time.time()), raw),
                        f"saving a message to '{folder}'")
        m = re.search(r"APPENDUID \d+ (\d+)", _text(data))
        return int(m.group(1)) if m else None
