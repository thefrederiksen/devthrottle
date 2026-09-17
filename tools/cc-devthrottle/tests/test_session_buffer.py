"""Tests for `cc-devthrottle session buffer`: the terminal text comes back exactly as the terminal held it.

The buffer verb's whole job is to hand back another session's raw terminal content. That content is
arbitrary text nobody controls - file paths, prompts, code, log lines - so anything that INTERPRETS it on
the way out is a defect, not a feature. Printing it through Rich did two things:

  (a) a token shaped like a closing tag - `[/tmp/x]`, `[/INST]` - raised rich.errors.MarkupError as an
      uncaught traceback, so the verb crashed on perfectly ordinary output;
  (b) with stdout not a terminal - exactly how an agent or a pipe calls it - Rich rewrapped every line at
      80 columns and silently ate style-shaped tokens like `[bold]`.

Both are asserted here with stdout captured as a pipe (capsys), which is the non-TTY path (a). and (b).
both live on. The same hazard was already fixed in this file for the JSON output of list_sessions, with a
comment explaining why; this is the same fix for the same reason.
"""

import sys
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402

SESSION_ID = "11111111-2222-3333-4444-555555555555"


@pytest.fixture
def buffer_text(monkeypatch):
    """Serve a chosen buffer text from the Director, without any real HTTP."""

    def serve(text: str) -> str:
        monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
        monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: {"text": text})
        return text

    return serve


def test_a_bracketed_path_does_not_crash_the_verb(buffer_text, capsys):
    # A real terminal shows paths like this constantly. Rich reads [/tmp/x] as a CLOSING TAG for a style
    # that was never opened and raises MarkupError - an uncaught traceback out of a read-only verb.
    text = "checked [/tmp/x] and it was fine"
    buffer_text(text)

    session_ops.read_session_buffer(None)

    assert capsys.readouterr().out == text + "\n"


def test_a_long_line_is_not_rewrapped(buffer_text, capsys):
    # Piped stdout is not a terminal, so Rich falls back to 80 columns and injects newlines. The caller
    # asked what the terminal is showing; a line broken in a place the terminal never broke it is a lie
    # about the session's output, and it is exactly what an agent reads.
    text = "x" * 200
    buffer_text(text)

    session_ops.read_session_buffer(None)

    out = capsys.readouterr().out
    assert out == text + "\n"
    assert "\n" not in out[:-1], "the 200-character line was rewrapped"


def test_a_style_shaped_token_is_not_eaten(buffer_text, capsys):
    # [bold] is valid Rich markup, so Rich consumes it and emits nothing - the text silently loses
    # characters that were genuinely on the terminal.
    text = "the literal token [bold] was on screen"
    buffer_text(text)

    session_ops.read_session_buffer(None)

    assert capsys.readouterr().out == text + "\n"


def test_the_error_branch_does_not_crash_on_the_servers_error_text(buffer_text, monkeypatch, capsys, plain):
    # The failure branch had the SAME defect as the success branch it reports on: the error text comes
    # from the server and can quote a path or a fragment of the session's own output, and it was
    # interpolated straight into Rich markup. A closing-tag-shaped token in it raised MarkupError - a
    # traceback out of the code whose only job is to explain a failure.
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)

    def boom(path):
        raise session_ops.gateway.GatewayError("no session at [/tmp/x] on that Director")

    monkeypatch.setattr(session_ops.gateway, "get_json", boom)

    with pytest.raises(typer.Exit):
        session_ops.read_session_buffer(None)

    captured = capsys.readouterr()
    assert captured.out == ""                                  # the answer stream stays clean
    out = plain(captured.err)
    assert "no session at [/tmp/x] on that Director" in out   # reported literally, not swallowed
    assert "Error:" in out                                     # and still human-readable
    assert "cc-devthrottle session list" in out                # with what to do next


def test_terminal_content_survives_uninterpreted(buffer_text, capsys):
    # The whole contract in one: markup-shaped tokens, a closing-tag shape, a long line, and blank lines
    # all come back as the terminal held them - unparsed, unwrapped, nothing added or removed in the
    # middle. Not a byte-for-byte claim: print adds the trailing newline, and the platform translates it.
    text = "\n".join(
        [
            "$ ls [/tmp/x]",
            "[bold]not a style, just text[/bold]",
            "y" * 200,
            "",
            "  indented  and   spaced  ",
        ]
    )
    buffer_text(text)

    session_ops.read_session_buffer(None)

    assert capsys.readouterr().out == text + "\n"


def test_the_buffer_shape_from_the_other_path_is_still_read(buffer_text, monkeypatch, capsys):
    # The verb accepts the text under "text" or "buffer" depending on the path it came back through.
    # Keep that working - this fix is about HOW the text is printed, not which shape carries it.
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: {"buffer": "from the other shape"})

    session_ops.read_session_buffer(None)

    assert capsys.readouterr().out == "from the other shape\n"
