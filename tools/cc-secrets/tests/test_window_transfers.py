"""The Secret Handoff mission, phase 5: what the cc-secrets window does to send, get and answer a transfer, without a
screen; and the Send dialog and approval pop-up themselves on a real screen (skipped where there is none)."""

import time

import pytest

from src import entry_window, gateway_link, known_machines, machine_key, machines, transfers
from src.window_transfers import Destination, WindowTransfers, ended_sentence
from test_transfer import TID, _dto, two  # noqa: F401  (the two-machine fixture)


@pytest.fixture
def gw(two, monkeypatch):
    """NORTH's window: the Gateway lists both machines; records what was posted."""
    state = {"posted": [], "rows": [_dto("waiting")], "states": ["approved", "delivered"]}

    def rows():
        return [{"machine": "SOREN_NORTH", "publicKey": two["north_key"].public_b64,
                 "fingerprint": two["north_key"].fingerprint, "lastSeenUtc": "", "directors": 1},
                {"machine": "devthrottle-mac-mini", "publicKey": two["mac_key"].public_b64,
                 "fingerprint": two["mac_key"].fingerprint, "lastSeenUtc": "", "directors": 1}]

    def fake_get(path, link=None):
        if path == machines.ROUTE:
            return {"machines": rows()}
        if path == transfers.ROUTE:
            return {"transfers": state["rows"]}
        current = state["states"].pop(0) if len(state["states"]) > 1 else state["states"][0]
        return {"transfer": _dto(current)}

    def fake_post(path, body, link=None):
        state["posted"].append((path, body))
        return {"transfer": _dto("approved", answeredWhere="window"), "note": ""}

    monkeypatch.setattr(gateway_link, "get", fake_get)
    monkeypatch.setattr(gateway_link, "post", fake_post)
    monkeypatch.setenv("CC_SECRETS_HOME", str(two["north"]))
    from src import paths
    monkeypatch.setattr(paths, "_checked_home", None)
    from src.store import SecretStore
    from src.storefile import UserOnlyFile
    store = SecretStore(UserOnlyFile(paths.store_path()))
    actions = WindowTransfers(store, link_factory=lambda: gateway_link.Link("machine", "https://g.example", "k"),
                              sleep=lambda s: None)
    return state, actions


def test_Others_ListsTheOtherMachines_WithWhatTheOwnerNeedsToKnowAboutEachKey(gw):
    state, actions = gw

    others = actions.others()

    assert [d.name for d in others] == ["devthrottle-mac-mini"]
    assert others[0].key_note == "Not sent to from here before." and not others[0].key_changed


def test_Others_AKeyThatChanged_IsSaidPlainly(gw, two):
    state, actions = gw
    known_machines.pin("devthrottle-mac-mini", "d" * 64)

    others = actions.others()

    assert others[0].key_changed and "KEY HAS CHANGED" in others[0].key_note


def test_Send_IsApprovedInTheWindow_OnThisMachine(gw):
    state, actions = gw

    actions.send("qa-handoff-one", "devthrottle-mac-mini", "qa-handoff-one", False, False)

    path, body = state["posted"][0]
    assert path == transfers.ROUTE
    assert (body["fromMachine"], body["toMachine"], body["approvedHere"], body["askedOn"]) == \
        ("SOREN_NORTH", "devthrottle-mac-mini", "window", "SOREN_NORTH")
    assert "acceptReceiverFingerprint" not in body


def test_Send_AcceptingANewKey_SendsThatKeysFingerprint(gw, two):
    state, actions = gw

    actions.send("qa-handoff-one", "devthrottle-mac-mini", "qa-handoff-one", False, True)

    assert state["posted"][0][1]["acceptReceiverFingerprint"] == two["mac_key"].fingerprint


def test_Send_AnEntryNotHere_IsRefusedBeforeAsking(gw):
    state, actions = gw

    with pytest.raises(gateway_link.GatewayRefusal, match="No entry named"):
        actions.send("qa-handoff-missing", "devthrottle-mac-mini", "", False, False)
    assert state["posted"] == []


def test_Request_BringsItHere_ApprovedInTheWindow(gw):
    state, actions = gw

    actions.request("qa-handoff-one", "devthrottle-mac-mini", "qa-handoff-one", True)

    body = state["posted"][0][1]
    assert (body["fromMachine"], body["toMachine"], body["replace"], body["approvedHere"]) == \
        ("devthrottle-mac-mini", "SOREN_NORTH", True, "window")


def test_Wait_ReadsUntilItEnds(gw):
    state, actions = gw

    ended = actions.wait(transfers.Transfer.from_dto(_dto("approved")))

    assert ended.state == "delivered"
    assert ended_sentence(ended) == "Stored qa-handoff-one on devthrottle-mac-mini."


def test_WaitingHere_IsOnlyWhatThisMachineWouldSend_AndCanStillBeAnswered(gw):
    state, actions = gw
    state["rows"] = [_dto("waiting"), _dto("waiting", transferId="f" * 32, fromMachine="devthrottle-mac-mini",
                                          toMachine="SOREN_NORTH"),
                     _dto("denied", transferId="e" * 32)]

    waiting = actions.waiting_here()

    assert [t.transfer_id for t in waiting] == [TID]


def test_Answer_IsGivenFromTheWindow(gw):
    state, actions = gw

    actions.answer(TID, approve=False)

    path, body = state["posted"][0]
    assert path == f"gateway/secrets/transfers/{TID}/answer"
    assert body == {"approve": False, "deny": True, "where": "window"}


def test_ResolveMachine_IgnoresASessionsKey_AndPutsTheEnvironmentBack(monkeypatch):
    from cc_shared import tool_errors

    monkeypatch.setenv("CC_GATEWAY_URL", "https://session.example")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key")
    seen = {}

    def machine_only():
        import os
        seen["url"] = os.environ.get("CC_GATEWAY_URL")
        return tool_errors.Credential("machine", "https://hosted.example", "machine-token")

    monkeypatch.setattr(tool_errors, "resolve_credential", machine_only)

    link = gateway_link.resolve_machine()

    import os
    assert (link.kind, link.bearer) == ("machine", "machine-token")
    assert seen["url"] is None
    assert os.environ["CC_GATEWAY_SESSION_KEY"] == "session-key"


def test_ResolveMachine_NotSignedIn_IsARefusal(monkeypatch):
    from cc_shared import tool_errors

    monkeypatch.setattr(tool_errors, "resolve_credential",
                        lambda: tool_errors.Credential("before-sign-in", "https://hosted.example", None))

    with pytest.raises(gateway_link.NotSignedIn):
        gateway_link.resolve_machine()


# --- On a real screen ----------------------------------------------------------------------------------------------

def _screen_problem():
    try:
        return entry_window.display_problem()
    except Exception as exc:  # noqa: BLE001 - any failure to ask means no usable screen here
        return type(exc).__name__


screen = pytest.mark.skipif(_screen_problem() is not None, reason="needs a screen")


class FakeActions:
    """Stands in for WindowTransfers on screen: answers at once, records what was asked."""

    def __init__(self, waiting):
        self.waiting = list(waiting)
        self.answered = []
        self.sent = []

    def waiting_here(self):
        return list(self.waiting)

    def answer(self, transfer_id, approve):
        self.answered.append((transfer_id, approve))
        self.waiting = [t for t in self.waiting if t.transfer_id != transfer_id]
        return transfers.Transfer.from_dto(_dto("approved"))

    def others(self):
        return [Destination("devthrottle-mac-mini", "Not sent to from here before.", False)]

    def send(self, entry, to, target, replace, accept):
        self.sent.append((entry, to, target, replace, accept))
        return transfers.Transfer.from_dto(_dto("approved"))

    def wait(self, t):
        return transfers.Transfer.from_dto(_dto("delivered"))


def _pump(root, condition, seconds=5.0):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        root.update()
        if condition():
            return True
        time.sleep(0.02)
    return False


@pytest.fixture(scope="module")
def tk_root():
    """One Tk for the module: Tk on Windows sometimes cannot start again in one process."""
    root = entry_window.open_root("cc-secrets transfer screen test")
    yield root
    root.destroy()


@screen
def test_Popup_AppearsForAWaitingTransfer_AndApproveAnswersFromTheWindow(tk_root):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([transfers.Transfer.from_dto(_dto("waiting"))])
    said = []
    watcher = ApprovalWatcher(tk_root, Worker(tk_root), fake, said.append, poll_ms=100)

    assert _pump(tk_root, lambda: TID in watcher.open)
    popup = watcher.open[TID]
    assert popup.win.title() == "A secret transfer needs your answer"
    popup.approve_button.invoke()

    assert _pump(tk_root, lambda: TID not in watcher.open and said)
    assert fake.answered == [(TID, True)]
    assert said[-1].startswith("Approved: qa-handoff-one from SOREN_NORTH")


@screen
def test_Popup_AnsweredElsewhere_Closes_AndSaysSo(tk_root):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([transfers.Transfer.from_dto(_dto("waiting"))])
    said = []
    watcher = ApprovalWatcher(tk_root, Worker(tk_root), fake, said.append, poll_ms=100)
    assert _pump(tk_root, lambda: TID in watcher.open)

    fake.waiting = []

    assert _pump(tk_root, lambda: TID not in watcher.open and said)
    assert said[-1].startswith("Answered elsewhere")


@screen
def test_SendDialog_ListsTheMachines_AndSendSaysHowItEnded(tk_root):
    from src.transfer_window import TransferDialog, Worker

    fake = FakeActions([])
    ended = []
    dialog = TransferDialog(tk_root, Worker(tk_root), fake, "send", "qa-handoff-one", ended.append)

    assert _pump(tk_root, lambda: str(dialog.go.cget("state")) == "normal")
    assert dialog.machine.get() == "devthrottle-mac-mini"
    dialog.go.invoke()

    assert _pump(tk_root, lambda: ended)
    assert fake.sent == [("qa-handoff-one", "devthrottle-mac-mini", "qa-handoff-one", False, False)]
    assert dialog.status.get() == "Stored qa-handoff-one on devthrottle-mac-mini."
    dialog.close()
