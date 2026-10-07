"""Tests for cc-outlook OutlookClient._format_message.

Regression test for the search crash:
    AttributeError: 'MessageFlag' object has no attribute 'get'
The formatter was calling .get() on the typed O365 MessageFlag object as if
it were a dict. This crashed any search whose result set contained a flagged
message, because MessageFlag.__bool__ only returns True when the message is
actually flagged or completed.
"""

from types import SimpleNamespace
from unittest.mock import MagicMock

from src.outlook_api import OutlookClient


def _make_msg(flag=None):
    """Build a stub message with the attributes _format_message reads."""
    return SimpleNamespace(
        object_id="msg-1",
        subject="Hello",
        sender=None,
        to=[],
        received=None,
        has_attachments=False,
        is_read=True,
        is_draft=False,
        importance=None,
        categories=[],
        conversation_id=None,
        web_link=None,
        flag=flag,
        body_preview="snippet",
    )


def _client():
    """Build an OutlookClient without running its real __init__."""
    return OutlookClient(account=MagicMock())


class TestFormatMessageFlag:
    """Flag handling in _format_message - the regression area."""

    def test_no_flag_attribute_returns_not_flagged(self):
        msg = _make_msg(flag=None)
        result = _client()._format_message(msg)
        assert result["flag_status"] == "notFlagged"

    def test_falsy_flag_returns_not_flagged(self):
        # MessageFlag.__bool__ returns False when status is NotFlagged.
        # The formatter must treat that the same as a missing flag.
        # SimpleNamespace can't override __bool__ via attribute, so use a
        # subclass with explicit __bool__ semantics.
        class FalsyFlag:
            status = SimpleNamespace(value="not_flagged")
            def __bool__(self):
                return False

        msg = _make_msg(flag=FalsyFlag())
        result = _client()._format_message(msg)
        assert result["flag_status"] == "notFlagged"

    def test_flagged_message_does_not_crash(self):
        """The exact bug: MessageFlag is an object, not a dict. .get() crashed."""
        class TruthyFlag:
            status = SimpleNamespace(value="flagged")
            def __bool__(self):
                return True
            # Important: does NOT implement .get(). If the formatter still
            # calls .get(), this attribute-less object will raise the
            # original AttributeError.

        msg = _make_msg(flag=TruthyFlag())
        result = _client()._format_message(msg)
        assert result["flag_status"] == "flagged"

    def test_completed_flag(self):
        class CompleteFlag:
            status = SimpleNamespace(value="complete")
            def __bool__(self):
                return True

        msg = _make_msg(flag=CompleteFlag())
        result = _client()._format_message(msg)
        assert result["flag_status"] == "complete"

    def test_flag_with_missing_status(self):
        """Defensive: if the SDK ever returns a flag with status=None."""
        class WeirdFlag:
            status = None
            def __bool__(self):
                return True

        msg = _make_msg(flag=WeirdFlag())
        result = _client()._format_message(msg)
        assert result["flag_status"] == "notFlagged"
