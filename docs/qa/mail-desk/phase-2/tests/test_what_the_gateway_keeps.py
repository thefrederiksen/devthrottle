"""Review finding 1: the fix reaches the owner. Asserted on what the Gateway KEEPS, never on raw standard error.

The Gateway stores a failed check's reason as "exit code 1: " plus the first 200 characters of ALL of standard error,
flattened onto one line (TriggerCheckContract.Read and OneLine on origin/main), and the Director's process is uv, whose
own line can come first. That reason is what phase 1's signed-in label, the Open problems card and the 08:00 email show.
So for every kind of failure, with one channel and with two, in observe and in live mode, the check's whole first
sentence must be inside that window after a stand-in uv line, and the memory must be left exactly as it was.
"""

import pytest
import requests

from conftest import MAILBOX, SECOND, UV_LINE, channel, gateway_reason, msg

CLIENT = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}
# The longest name Windows gives a computer (15 characters), with a domain the check must not spend the window on.
HOSTNAME = "DESKTOP-ABCD123.corp.example.com"
MACHINE = "DESKTOP-ABCD123"
NICKNAMES = {MAILBOX: "devthrottle", SECOND: "example"}


def register_all(world, *addresses):
    world.register({a: {"client_id": CLIENT, "nickname": NICKNAMES[a]} for a in addresses})


# Each failure kind: (name, set it up for the failing address, the fix or cause its sentence must hold).
def missing(world, failing):
    register_all(world, MAILBOX, SECOND)
    return f"{failing} is not signed in on {MACHINE}: run cc-outlook -a {NICKNAMES[failing]} auth"


def not_in_cc_outlook(world, failing):
    register_all(world, *[a for a in (MAILBOX, SECOND) if a != failing])
    return f"{failing} is not signed in on {MACHINE}: first run cc-outlook accounts add {failing} -c {CLIENT}"


def more_than_one(world, failing):
    world.plant(failing, instance="default")
    world.plant(failing, instance="slot5")
    return f"{failing} is signed in more than once on {MACHINE}: keep one and sign out of the others with cc-outlook"


def expired(world, failing):
    register_all(world, MAILBOX, SECOND)
    world.plant(failing)
    world.sign_in.renew_fails.add(failing)
    return f"{failing} is not signed in on {MACHINE}: run cc-outlook -a {NICKNAMES[failing]} auth"


def sign_in_service_unreachable(world, failing):
    world.plant(failing)
    world.sign_in.renew_raises[failing] = requests.exceptions.ProxyError(
        "HTTPSConnectionPool(host='login.microsoftonline.com', port=443): Max retries exceeded with url: "
        "/common/oauth2/v2.0/token (Caused by ProxyError('Unable to connect to proxy'))")
    return f"could not reach the Microsoft sign-in service from {MACHINE} for {failing}: ProxyError"


def graph_error(world, failing):
    world.plant(failing)
    world.graph.fail_for[failing] = 500
    return f"Microsoft Graph answered HTTP 500 for {failing} while "


def graph_unreachable(world, failing):
    world.plant(failing)
    world.graph.fail_for[failing] = "unreachable"
    return f"Microsoft Graph could not be reached from {MACHINE} for {failing} while "


def unparseable_page(world, failing):
    world.plant(failing)
    world.graph.fail_for[failing] = "no-value"
    return f"Microsoft Graph answered a page without a 'value' list for {failing} while "


def not_json(world, failing):
    world.plant(failing)
    world.graph.fail_for[failing] = "not-json"
    return f"Microsoft Graph answered something that is not JSON for {failing} while "


def unusable_message(world, failing):
    world.plant(failing)
    world.graph.box(failing)["inbox"].append({"receivedDateTime": "2026-10-18T09:00:00Z", "subject": "no id"})
    return f"Microsoft Graph answered a message without a usable id or receive time for {failing} while reading the Inbox"


def unknown_provider(world, failing):
    return f"provider gmail has no reader in this version of screen.py (channel {failing})"


def memory_too_big(world, failing):
    world.plant(failing)
    return "over the Director's 65536, so nothing was written: give this factory fewer channels"


# Every channel signs in before any channel files or reads, so only these fail after the healthy channel's move.
AFTER_SIGN_IN = {graph_error, graph_unreachable, unparseable_page, not_json, unusable_message, memory_too_big}

KINDS = [missing, not_in_cc_outlook, more_than_one, expired, sign_in_service_unreachable, graph_error,
         graph_unreachable, unparseable_page, not_json, unusable_message, unknown_provider, memory_too_big]


def build(world, kind, channels, mode):
    """One or two channels; the LAST one fails, so with two the healthy channel's whole run comes first. In live mode
    every channel has a queued move, so the filing log exists before the failure."""
    addresses = [MAILBOX, SECOND][:channels]
    failing = addresses[-1]
    for healthy in addresses[:-1]:
        world.plant(healthy)
    expected = kind(world, failing)
    entries = []
    for address in addresses:
        fields = {"cursor": CURSOR}
        if mode == "live":
            graph_id = f"AAMkOld{addresses.index(address)}="
            world.graph.box(address)["inbox"].append(msg(graph_id, "2026-10-18T06:00:00Z"))
            key = world.screen.short_key(address, graph_id)
            fields.update(keys={key: {"id": graph_id, "reportedUtc": "2026-10-18T06:05:00Z"}},
                          filingQueue=[{"messageKey": key, "decision": "archive"}])
        if address == failing and kind is unknown_provider:
            fields["provider"] = "gmail"
        if address == failing and kind is memory_too_big:
            fields["padding"] = "x" * (64 * 1024)
        entries.append(channel(address, mode=mode, **fields))
    world.memory(*entries)
    return expected


@pytest.mark.parametrize("mode", ["observe", "live"])
@pytest.mark.parametrize("channels", [1, 2])
@pytest.mark.parametrize("kind", KINDS, ids=[k.__name__ for k in KINDS])
def test_Failure_TheReasonTheGatewayKeeps_HoldsTheWholeSentenceAfterUvsLine(world, monkeypatch, kind, channels, mode):
    monkeypatch.setattr(world.screen.socket, "gethostname", lambda: HOSTNAME)
    expected = build(world, kind, channels, mode)
    before = world.state.read_bytes()

    code, _, _, err = world.run()

    assert code == 1
    assert world.state.read_bytes() == before
    sentence = err.splitlines()[0]
    assert expected in sentence
    assert len(sentence) <= world.screen.SENTENCE_LIMIT
    kept = gateway_reason(err)
    assert kept.startswith(f"exit code 1: {UV_LINE} {sentence}"), kept
    assert "Traceback" not in kept
    if mode == "live" and channels == 2 and kind in AFTER_SIGN_IN:
        assert world.graph.box(MAILBOX)["archive"], "the healthy channel's move ran before the failure"


def test_SentenceLimit_LeavesRoomForUvsLineInsideTheGatewaysWindow(world):
    assert world.screen.GATEWAY_REASON_WINDOW == 200
    assert len(UV_LINE) + 1 + world.screen.SENTENCE_LIMIT <= 200


def test_TheRuleOnTheWindow_IsTheGatewaysOwn(world):
    """The helper the tests above judge by: lines joined with one space, cut at 200 with "...", and uv first."""
    assert gateway_reason("a\n\n  b\tc\r\n") == f"exit code 1: {UV_LINE} a b c"
    assert gateway_reason("x" * 300, uv_line=None) == "exit code 1: " + "x" * 200 + "..."


def test_MoveFailing_WithARealLengthGraphId_KeepsTheWholeSentence(world, monkeypatch):
    """A real Graph id is about 152 characters (phase 0). The sentence names the mailbox and the step, never the
    address it called, so the id cannot push the sentence out of the window, and the id is on no line at all."""
    graph_id = "AAMk" + "A" * 147 + "="
    world.plant()
    world.graph.box(MAILBOX)["inbox"].append(msg(graph_id, "2026-10-18T06:00:00Z"))
    key = world.screen.short_key(MAILBOX, graph_id)
    world.memory(channel(mode="live", cursor=CURSOR, keys={key: {"id": graph_id, "reportedUtc": "2026-10-18T06:05:00Z"}},
                         filingQueue=[{"messageKey": key, "decision": "junk"}]))
    world.graph.fail_for[MAILBOX] = 503
    code, _, _, err = world.run()
    assert code == 1
    sentence = err.splitlines()[0]
    assert sentence == f"Microsoft Graph answered HTTP 503 for {MAILBOX} while moving a message to junkemail"
    assert gateway_reason(err).startswith(f"exit code 1: {UV_LINE} {sentence}")
    assert graph_id not in err
