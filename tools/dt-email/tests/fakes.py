"""In-memory IMAP and SMTP servers for the dt-email tests. No network.

FakeImapConn answers with the same shapes `imaplib` returns, modelled on what Zoho answered in the
proof (proof\\prover-a\\evidence): FLAGS before UID in a FETCH, APPENDUID on append, `BAD [CLIENTBUG]`
for flags without parentheses, a deleted folder kept in Trash, and Archive with no special-use flag.

FakeSmtp is a real `smtplib.SMTP` with only the wire replaced (`sendmail`), so the real
`send_message` decides what is transmitted - including dropping the Bcc header.
"""

from __future__ import annotations

import email
import email.policy
import imaplib
import re
import smtplib
from dataclasses import dataclass, field
from typing import Dict, List, Optional


@dataclass
class Stored:
    raw: bytes
    flags: List[str]
    internal_date: str = "07-Nov-2026 17:31:20 -0500"


@dataclass
class FakeFolder:
    flags: List[str]
    messages: Dict[int, Stored] = field(default_factory=dict)
    uidnext: int = 1
    uidvalidity: int = 1


def _unquote(arg: str) -> str:
    if arg.startswith('"') and arg.endswith('"'):
        return arg[1:-1].replace('\\"', '"').replace("\\\\", "\\")
    return arg


class FakeImapServer:
    def __init__(self, user="me@example.com", password="pw", capabilities=None, delete_folder_to_trash=True,
                 pre_login_capabilities=None):
        self.pre_login_capabilities = pre_login_capabilities
        self.user = user
        self.password = password
        self.capabilities = tuple(capabilities or ("IMAP4REV1", "MOVE", "UIDPLUS", "IDLE", "NAMESPACE"))
        self.delete_folder_to_trash = delete_folder_to_trash
        self.folders: Dict[str, FakeFolder] = {
            "INBOX": FakeFolder(["\\HasNoChildren"]),
            "Drafts": FakeFolder(["\\Noinferiors", "\\Drafts"]),
            "Sent": FakeFolder(["\\Noinferiors", "\\Sent"]),
            "Spam": FakeFolder(["\\Noinferiors", "\\Junk"]),
            "Trash": FakeFolder(["\\HasNoChildren", "\\Trash"]),
            "Archive": FakeFolder(["\\HasNoChildren"]),
        }
        self.connections = 0
        self.commands: List[tuple] = []

    def add(self, folder: str, raw: bytes, flags=(), internal_date: str = "07-Nov-2026 17:31:20 -0500") -> int:
        f = self.folders[folder]
        uid = f.uidnext
        f.uidnext += 1
        f.messages[uid] = Stored(raw=raw, flags=list(flags), internal_date=internal_date)
        return uid

    def connect(self, account=None):
        self.connections += 1
        return FakeImapConn(self)


class FakeImapConn:
    def __init__(self, server: FakeImapServer):
        self.server = server
        # Before login a server may advertise less (Dovecot adds MOVE and UIDPLUS only after it).
        self.capabilities = server.pre_login_capabilities or server.capabilities
        self.selected: Optional[str] = None
        self.readonly = True

    # ----- session
    def login(self, user, password):
        if user != self.server.user or password != self.server.password:
            raise imaplib.IMAP4.error(b"[AUTHENTICATIONFAILED] Invalid credentials(Failure)")
        return "OK", [b"Success"]

    def capability(self):
        # Zoho answered with the list twice (one from the sign-in, one from CAPABILITY), as imaplib gathers both.
        line = " ".join(self.server.capabilities).encode()
        return "OK", [line, line]

    def logout(self):
        return "BYE", [b"logging out"]

    def shutdown(self):
        pass

    # ----- folders
    def list(self):
        lines = [f'({" ".join(f.flags)}) "/" "{name}"'.encode() for name, f in self.server.folders.items()]
        return "OK", lines

    def _folder(self, arg: str) -> Optional[FakeFolder]:
        return self.server.folders.get(_unquote(arg))

    def status(self, mailbox, items):
        f = self._folder(mailbox)
        if f is None:
            return "NO", [b"Folder not exist"]
        unseen = sum(1 for m in f.messages.values() if "\\Seen" not in m.flags)
        return "OK", [f'{mailbox} (MESSAGES {len(f.messages)} UNSEEN {unseen})'.encode()]

    def select(self, mailbox, readonly=False):
        name = _unquote(mailbox)
        if name not in self.server.folders:
            return "NO", [b"Folder not exist"]
        self.selected = name
        self.readonly = readonly
        return "OK", [str(len(self.server.folders[name].messages)).encode()]

    def response(self, code):
        # What imaplib keeps from the last SELECT: the folder's UIDVALIDITY, as Zoho sends it.
        if code == "UIDVALIDITY" and self.selected is not None:
            return code, [str(self.server.folders[self.selected].uidvalidity).encode()]
        return code, [None]

    def create(self, mailbox):
        name = _unquote(mailbox)
        if name in self.server.folders:
            return "NO", [b"Folder already exists"]
        self.server.folders[name] = FakeFolder(["\\HasNoChildren"])
        return "OK", [b"Success"]

    def rename(self, old, new):
        o, n = _unquote(old), _unquote(new)
        if o not in self.server.folders:
            return "NO", [b"Folder not exist"]
        self.server.folders[n] = self.server.folders.pop(o)
        return "OK", [b"Success"]

    def delete(self, mailbox):
        name = _unquote(mailbox)
        if name not in self.server.folders:
            return "NO", [b"Folder not exist"]
        folder = self.server.folders.pop(name)
        if self.server.delete_folder_to_trash and not name.startswith("Trash/"):
            # Zoho keeps a deleted folder in Trash, joining a nested name's parts with "_" (seen live).
            self.server.folders["Trash/" + name.replace("/", "_")] = folder
        return "OK", [b"Success"]

    def append(self, mailbox, flags, date_time, message):
        f = self._folder(mailbox)
        if f is None:
            return "NO", [b"Folder not exist"]
        parsed = flags.strip("()").split() if flags else []
        uid = self.server.add(_unquote(mailbox), message, parsed)
        return "OK", [f"[APPENDUID 1 {uid}] APPEND completed".encode()]

    # ----- messages
    def _uids(self, spec: str) -> List[int]:
        return [int(x) for x in spec.split(",")]

    def uid(self, command, *args):
        command = command.upper()
        self.server.commands.append((command,) + args)
        folder = self.server.folders[self.selected]
        if command == "SEARCH":
            return "OK", [" ".join(str(u) for u in self._search(folder, list(args[1:]))).encode()]
        if command == "FETCH":
            return "OK", self._fetch(folder, self._uids(args[0]), args[1])
        if command == "STORE":
            if self.readonly:
                return "NO", [b"Folder is read-only"]
            spec, op, flags = args
            if not flags.startswith("("):
                raise imaplib.IMAP4.error("UID command error: BAD [b'[CLIENTBUG] syntax']")
            names = flags.strip("()").split()
            for u in self._uids(spec):
                if u in folder.messages:
                    m = folder.messages[u]
                    if op.startswith("+"):
                        m.flags += [n for n in names if n not in m.flags]
                    else:
                        m.flags = [f for f in m.flags if f not in names]
            return "OK", [None]
        if command == "MOVE":
            if "MOVE" not in self.server.capabilities:
                raise imaplib.IMAP4.error("UID command error: BAD [b'Unknown command']")
            dest = self.server.folders.get(_unquote(args[1]))
            if dest is None:
                return "NO", [b"[TRYCREATE] Folder not exist"]
            for u in self._uids(args[0]):
                if u in folder.messages:
                    stored = folder.messages.pop(u)
                    self.server.add(_unquote(args[1]), stored.raw, stored.flags)
            return "OK", [None]
        if command == "EXPUNGE":
            for u in self._uids(args[0]):
                if u in folder.messages and "\\Deleted" in folder.messages[u].flags:
                    del folder.messages[u]
            return "OK", [None]
        raise AssertionError(f"fake IMAP does not know UID {command}")

    def _search(self, folder: FakeFolder, criteria: List[str]) -> List[int]:
        result = []
        for uid, m in sorted(folder.messages.items()):
            msg = email.message_from_bytes(m.raw, policy=email.policy.default)
            ok = True
            i = 0
            while i < len(criteria):
                key = criteria[i].upper()
                if key == "ALL":
                    i += 1
                elif key == "UNSEEN":
                    ok &= "\\Seen" not in m.flags
                    i += 1
                elif key == "FLAGGED":
                    ok &= "\\Flagged" in m.flags
                    i += 1
                elif key in ("FROM", "TO", "SUBJECT"):
                    ok &= _unquote(criteria[i + 1]).lower() in str(msg.get(key.title(), "")).lower()
                    i += 2
                elif key in ("TEXT", "BODY"):
                    ok &= _unquote(criteria[i + 1]).lower().encode() in m.raw.lower()
                    i += 2
                elif key in ("SINCE", "BEFORE"):
                    i += 2
                elif key == "UID":
                    low, high = criteria[i + 1].split(":")
                    top = max(folder.messages) if folder.messages else 0
                    low_n = int(low)
                    high_n = top if high == "*" else int(high)
                    # As a real server does: "n:*" with n above the newest UID still matches the newest message.
                    ok &= (min(low_n, high_n) <= uid <= max(low_n, high_n))
                    i += 2
                else:
                    raise AssertionError(f"fake IMAP does not know search key {key}")
            if ok:
                result.append(uid)
        return result

    def _fetch(self, folder: FakeFolder, uids: List[int], items: str):
        out = []
        seq = 0
        for u in uids:
            if u not in folder.messages:
                continue
            seq += 1
            m = folder.messages[u]
            flags = " ".join(m.flags)
            partial = re.search(r"BODY\.PEEK\[\]<0\.(\d+)>", items)
            if partial:
                data = m.raw[:int(partial.group(1))]
                out.append((f"{seq} (FLAGS ({flags}) UID {u} BODY[]<0> {{{len(data)}}}".encode(), data))
                out.append(b")")
            elif "BODY.PEEK[]" in items:
                out.append((f"{seq} (FLAGS ({flags}) UID {u} BODY[] {{{len(m.raw)}}}".encode(), m.raw))
                out.append(b")")
            elif "HEADER.FIELDS" in items:
                wanted = re.search(r"HEADER\.FIELDS \(([^)]*)\)", items).group(1).split()
                msg = email.message_from_bytes(m.raw, policy=email.policy.compat32)
                head = "".join(f"{k}: {v}\r\n" for k, v in msg.items() if k.upper() in wanted) + "\r\n"
                data = head.encode()
                fields = " ".join(wanted)
                prefix = (f'{seq} (FLAGS ({flags}) UID {u} INTERNALDATE "{m.internal_date}" '
                          f'RFC822.SIZE {len(m.raw)} BODY[HEADER.FIELDS ({fields})] {{{len(data)}}}')
                out.append((prefix.encode(), data))
                out.append(b")")
            else:
                out.append(f"{seq} (FLAGS ({flags}) UID {u})".encode())
        return out


class FakeSmtp(smtplib.SMTP):
    """A real smtplib.SMTP that never opens a socket: only sendmail, login and quit are replaced."""

    def __init__(self, outbox: "SmtpOutbox"):
        super().__init__(local_hostname="fake.local")  # no host: no connection; no name lookup
        self.outbox = outbox
        self.esmtp_features = {"auth": "LOGIN PLAIN", "size": "53477376"}
        self.does_esmtp = True
        self.sock = object()  # "connected", as far as smtplib's own checks go

    def ehlo_or_helo_if_needed(self):
        pass

    def login(self, user, password, *, initial_response_ok=True):
        if (user, password) != (self.outbox.user, self.outbox.password):
            raise smtplib.SMTPAuthenticationError(535, b"Authentication Failed")
        return 235, b"ok"

    def sendmail(self, from_addr, to_addrs, msg, mail_options=(), rcpt_options=()):
        if from_addr not in self.outbox.allowed_senders:
            # Zoho accepts MAIL FROM, refuses at DATA, then closes the connection (seen in the live run).
            # smtplib has not noticed yet: sock is still set, and a later quit() fails.
            self.closed_by_server = True
            raise smtplib.SMTPDataError(550, b"5.7.1 Sender is not allowed to relay emails")
        data = msg if isinstance(msg, bytes) else msg.encode()
        self.outbox.sent.append({"from": from_addr, "to": list(to_addrs), "data": data,
                                 "message": email.message_from_bytes(data, policy=email.policy.default)})
        return {}

    def close(self):
        self.sock = None

    def quit(self):
        if self.sock is None or getattr(self, "closed_by_server", False):
            raise smtplib.SMTPServerDisconnected("please run connect() first")
        return 221, b"bye"


class SmtpOutbox:
    def __init__(self, user="me@example.com", password="pw", allowed_senders=("me@example.com",)):
        self.user = user
        self.password = password
        self.allowed_senders = set(allowed_senders)
        self.sent: List[dict] = []
        self.connections = 0

    def connect(self, account=None):
        self.connections += 1
        return FakeSmtp(self)
