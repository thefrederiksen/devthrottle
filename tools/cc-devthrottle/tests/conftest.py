"""Shared helpers for the cc-devthrottle tests.

WHY THIS EXISTS. These tests drive the command-line surface through Typer's CliRunner and then
assert on what the user would read: that `--subject` is offered, that a summary says "900
directories", that an error message survives intact. Rich renders that output with STYLE, so the
captured text carries ANSI escape sequences threaded through it, and a plain `in` check against a
styled string can miss a substring that is plainly on the screen:

    assert '--subject' in '\x1b[1m ... \x1b[0m\x1b[1;36m--subject\x1b[0m ...'   # False

Whether styling is on depends on the environment, not on the code under test. Locally, with output
captured to a buffer, Rich usually renders plain and the assertions pass; on a continuous
integration runner colour is on and they fail. Five of these tests had never been run anywhere but
a developer's console, so nobody had seen it (issue #1082, found by the job added in #1077).

The fix is to assert against the TEXT rather than the rendering. `plain` strips the escape
sequences, so the assertion means the same thing wherever it runs. It deliberately does not turn
colour off: forcing a plain console would make these particular tests pass while leaving the next
one written the same way just as fragile, and it would depend on knowing exactly which environment
variable a given runner uses to enable colour.
"""

import re

import pytest

# Control Sequence Introducer colour/style codes: ESC [ ... m. That is the whole of what Rich emits
# for styling here; cursor movement and the like do not appear in captured CliRunner output.
_ANSI_STYLE = re.compile(r"\x1b\[[0-9;]*m")


def strip_ansi(text: str) -> str:
    """Return `text` with ANSI style sequences removed, leaving what a reader actually sees."""
    return _ANSI_STYLE.sub("", text)


@pytest.fixture
def plain():
    """Strip ANSI styling from captured command output before asserting on it."""
    return strip_ansi


@pytest.fixture(params=["colour terminal", "not a terminal", "narrow colour terminal"])
def either_console(request, monkeypatch):
    """Run the test on a colour terminal, on a console that is not a terminal, and on a narrow colour terminal.

    For the sentences the Gateway writes and this tool only QUOTES - a refusal, a note - `plain` is the
    wrong tool: those must reach the reader verbatim, so the test asserts on the raw output, and it has to
    hold whether or not the console styles. The continuous integration job forces colour, a developer's
    captured run usually does not, and a test that ran only one way hid exactly this: the console coloured
    the "6" in "the limit is 6".

    It has to hold whatever the width, too (inspection 6, ruling 3). The console wraps a long line by
    inserting line breaks, and a sentence with a break inside it is no longer the sentence the Gateway sent.
    That was hidden while every run was wide: a terminal named "dumb" makes the console ignore the width it
    was given and wrap at 80, and the broadcast refusal row - a full session id, a label and the sentence -
    was split. The narrow run proves it on any machine. The height is set beside the width because the
    console honours a fixed size only when both are given; without it, TERM=dumb wins.
    """
    from rich.console import Console
    from src import session_ops

    terminal = request.param != "not a terminal"
    width = 40 if request.param == "narrow colour terminal" else 500
    console = Console(force_terminal=terminal, color_system="standard" if terminal else None, width=width, height=25)
    monkeypatch.setattr(session_ops, "console", console)
    return request.param


@pytest.fixture(autouse=True)
def inside_a_session(monkeypatch):
    """Run every test as if it were inside a DevThrottle session with a Gateway.

    Remove-the-network-port mission, phase 2. The cc-* commands reach the fleet through the Gateway
    and present THIS SESSION's own key, and both are read from the environment a session is launched
    with. Without them every command in this suite would fail before reaching the code it is about,
    with the same "there is no Gateway here" message - which would say nothing about what these tests
    are for.

    THIS IS NOT A HOLE IN THE NO-GATEWAY PROOF. That the commands fail loudly, and what they say when
    they do, is asserted directly in cc_shared/tests/test_gateway.py against the real resolution -
    each half separately, and the unreachable case too. This fixture only stops every OTHER test from
    re-proving it by accident. The values are deliberately not a real address: any test that reaches
    the network with them fails to connect rather than talking to something.
    """
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gateway.invalid")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "test-session-key")
