"""Regression tests for cc-gmail ImapClient.

Two areas:

1. UID namespace bug. Gmail gives every label its own IMAP UID space, so a UID
   from INBOX (where list/search ids come from) does NOT address the same
   message in [Gmail]/All Mail. get_message_details/mark_as_read/mark_as_unread/
   delete_message must resolve the stable X-GM-MSGID to the UID within the
   mailbox they operate on, never reuse the caller-supplied number as a UID in a
   different mailbox. Getting this wrong can read, mark, or DELETE the wrong
   message.

2. Reply-all recipient construction must include Cc and exclude the user's own
   address.
"""

from unittest.mock import MagicMock

import pytest

from src.imap_client import (
    ImapClient,
    build_reply_all_recipients,
    _encode_address_header,
    _imap_quote,
)


def _client_with_conn(monkeypatch):
    """Build an ImapClient with a mocked IMAP connection and no real folder select."""
    client = ImapClient("me@gmail.com", "app-password")
    conn = MagicMock()
    conn.noop.return_value = ("OK", [b""])
    client._conn = conn
    # Do not touch a real server when selecting mailboxes; record selections.
    selected = []
    monkeypatch.setattr(
        client, "_select_folder",
        lambda folder="INBOX", readonly=True: selected.append((folder, readonly)),
    )
    return client, conn, selected


class TestUidNamespace:
    def test_delete_resolves_msgid_and_uses_resolved_uid(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        calls = []

        def uid_side(cmd, *args):
            calls.append((cmd.lower(), args))
            if cmd.lower() == "search":
                # The search must be by stable X-GM-MSGID, in All Mail.
                assert args[1] == "X-GM-MSGID 18000000000000001"
                return ("OK", [b"42"])
            return ("OK", [b""])

        conn.uid.side_effect = uid_side

        client.delete_message("18000000000000001")

        # It must have selected All Mail to resolve, not addressed the raw id.
        assert ("ALL", False) in selected
        store_args = [a for c, a in calls if c == "store"]
        copy_args = [a for c, a in calls if c == "copy"]
        # The resolved UID "42" is used for store/copy, NOT the raw message id.
        assert all(a[0] == "42" for a in store_args)
        assert all(a[0] == "42" for a in copy_args)
        assert not any("18000000000000001" in str(a) for a in store_args + copy_args)

    def test_mark_as_read_uses_resolved_uid(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        calls = []

        def uid_side(cmd, *args):
            calls.append((cmd.lower(), args))
            if cmd.lower() == "search":
                return ("OK", [b"77"])
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        client.mark_as_read("18000000000000002")

        store_args = [a for c, a in calls if c == "store"]
        assert store_args
        assert store_args[0][0] == "77"
        assert store_args[0][1] == "+FLAGS"

    def test_get_message_details_fetches_resolved_uid(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        raw_email = (
            b"From: sender@example.com\r\n"
            b"To: me@gmail.com\r\n"
            b"Subject: Hello\r\n\r\n"
            b"Body content here."
        )
        envelope = b"1 (X-GM-THRID 999 X-GM-LABELS (\\Inbox) UID 55"
        fetched = {}

        def uid_side(cmd, *args):
            if cmd.lower() == "search":
                return ("OK", [b"55"])
            if cmd.lower() == "fetch":
                fetched["uid"] = args[0]
                return ("OK", [(envelope, raw_email)])
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        result = client.get_message_details("18000000000000003")

        # Fetch must target the resolved All Mail UID, not the raw id.
        assert fetched["uid"] == "55"
        # The returned id stays the stable message id passed in.
        assert result["id"] == "18000000000000003"
        assert result["headers"]["subject"] == "Hello"

    def test_resolve_raises_when_not_found(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        conn.uid.return_value = ("OK", [b""])  # empty search result
        with pytest.raises(ValueError):
            client.mark_as_read("18000000000000099")

    def test_list_messages_returns_stable_msgids(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)

        def uid_side(cmd, *args):
            if cmd.lower() == "search":
                return ("OK", [b"10 11"])
            if cmd.lower() == "fetch":
                return (
                    "OK",
                    [
                        b"1 (UID 10 X-GM-MSGID 18000000000000010)",
                        b"2 (UID 11 X-GM-MSGID 18000000000000011)",
                    ],
                )
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        messages = client.list_messages(max_results=10)
        ids = {m["id"] for m in messages}
        # Stable message ids, not the per-mailbox UIDs 10/11.
        assert ids == {"18000000000000010", "18000000000000011"}


class TestReplyAllRecipients:
    def test_includes_cc_and_excludes_self(self):
        result = build_reply_all_recipients(
            own_address="me@gmail.com",
            original_from="Sender <sender@example.com>",
            original_to="me@gmail.com, Bob <bob@example.com>",
            original_cc="carol@example.com",
        )
        assert "sender@example.com" in result
        assert "bob@example.com" in result
        assert "carol@example.com" in result
        # The user's own address must be excluded from reply-all.
        assert "me@gmail.com" not in result

    def test_dedupes_addresses(self):
        result = build_reply_all_recipients(
            own_address="me@gmail.com",
            original_from="Sender <sender@example.com>",
            original_to="sender@example.com",
            original_cc="",
        )
        assert result.count("sender@example.com") == 1

    def test_excludes_self_case_insensitive(self):
        result = build_reply_all_recipients(
            own_address="Me@Gmail.com",
            original_from="sender@example.com",
            original_to="ME@GMAIL.COM",
            original_cc="",
        )
        assert "gmail.com" not in result.lower()
        assert "sender@example.com" in result


class TestUidExpunge:
    """Archive/delete must UID EXPUNGE only the targeted message, never call the
    mailbox-wide expunge() that removes every \\Deleted message present."""

    def test_archive_uses_uid_expunge_not_mailbox_expunge(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        calls = []

        def uid_side(cmd, *args):
            calls.append((cmd.upper(), args))
            if cmd.lower() == "search":
                return ("OK", [b"42"])
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        client.archive_message("18000000000000001")

        expunge_calls = [a for c, a in calls if c == "EXPUNGE"]
        assert expunge_calls, "archive must issue UID EXPUNGE"
        # Scoped to the resolved UID, not the whole mailbox.
        assert expunge_calls[0][0] == "42"
        conn.expunge.assert_not_called()

    def test_delete_permanent_uses_uid_expunge(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        calls = []

        def uid_side(cmd, *args):
            calls.append((cmd.upper(), args))
            if cmd.lower() == "search":
                return ("OK", [b"77"])
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        client.delete_message("18000000000000002", permanent=True)

        expunge_calls = [a for c, a in calls if c == "EXPUNGE"]
        assert expunge_calls
        assert expunge_calls[0][0] == "77"
        conn.expunge.assert_not_called()


class TestXGmRawEscaping:
    """X-GM-RAW search queries must escape embedded double-quotes so a quote in
    the user query cannot terminate the IMAP string early or inject tokens."""

    def test_imap_quote_escapes_quotes_and_backslashes(self):
        assert _imap_quote('a"b') == '"a\\"b"'
        assert _imap_quote("a\\b") == '"a\\\\b"'

    def test_list_messages_escapes_query_quote(self, monkeypatch):
        client, conn, selected = _client_with_conn(monkeypatch)
        captured = {}

        def uid_side(cmd, *args):
            if cmd.lower() == "search":
                captured["criteria"] = args[-1]
                return ("OK", [b""])  # no uids -> empty list, no fetch
            return ("OK", [b""])

        conn.uid.side_effect = uid_side
        client.list_messages(query='subject:"hi there"', max_results=5)

        crit = captured["criteria"]
        assert crit.startswith('X-GM-RAW "')
        # The embedded quotes are backslash-escaped, not left raw.
        assert 'subject:\\"hi there\\"' in crit


class TestEncodeAddressHeaderImap:
    def test_non_ascii_name_keeps_addr_spec(self):
        encoded = _encode_address_header("Jos\xe9 <jose@example.com>")
        encoded.encode("ascii")
        from email.utils import getaddresses
        addrs = [addr for _, addr in getaddresses([encoded])]
        assert "jose@example.com" in addrs
