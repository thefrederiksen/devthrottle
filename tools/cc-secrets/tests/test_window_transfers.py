"""The Secret Handoff mission, phase 5: what the cc-secrets window does to send, get and answer a transfer; and the
Send dialog, the approval pop-up and their worker themselves, drawn on a stand-in for tkinter (tests/fake_tk.py) so
they need no screen, no clock and no sleep on any machine."""

import sys

import fake_tk
import pytest

from src import gateway_link, known_machines, machine_key, machines, transfers
from src.window_transfers import Destination, Waiting, WindowTransfers, ended_sentence
from test_transfer import TID, _dto, two  # noqa: F401  (the two-machine fixture)


@pytest.fixture(autouse=True)
def own_secrets_folder(home):
    """Every test here, the window tests included, writes any log line into its own folder, never the owner's."""
    yield home


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

    assert [w.transfer.transfer_id for w in waiting] == [TID]
    assert waiting[0].receiver.name == "devthrottle-mac-mini" and not waiting[0].receiver.key_changed


def test_WaitingHere_SaysWhenTheReceivingMachinesKeyChanged(gw, two):
    state, actions = gw
    known_machines.pin("devthrottle-mac-mini", "d" * 64)

    waiting = actions.waiting_here()

    assert waiting[0].receiver.key_changed
    assert waiting[0].receiver.fingerprint == two["mac_key"].fingerprint


def test_Answer_AcceptingTheNewKey_SendsThatExactKeysFingerprint(gw, two):
    state, actions = gw

    actions.answer(TID, approve=True, accept_fingerprint=two["mac_key"].fingerprint)

    body = state["posted"][0][1]
    assert body["acceptReceiverFingerprint"] == two["mac_key"].fingerprint and body["where"] == "window"


def test_Answer_IsGivenFromTheWindow(gw):
    state, actions = gw

    actions.answer(TID, approve=False)

    path, body = state["posted"][0]
    assert path == f"gateway/secrets/transfers/{TID}/answer"
    assert body == {"approve": False, "deny": True, "where": "window"}


def _machine_config(tmp_path, monkeypatch):
    import json

    from cc_shared import tool_errors

    (tmp_path / "config").mkdir()
    (tmp_path / "config" / "config.json").write_text(
        json.dumps({"gateway": {"url": "https://hosted.example", "token": "machine-token"}}), encoding="utf-8")
    monkeypatch.setattr(tool_errors, "machine_root", lambda: tmp_path)
    monkeypatch.setenv("CC_GATEWAY_URL", "https://session.example")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key")


def test_ResolveMachine_IgnoresASessionsKey_WithoutTouchingTheEnvironment(monkeypatch, tmp_path):
    import os

    _machine_config(tmp_path, monkeypatch)
    before = dict(os.environ)

    link = gateway_link.resolve_machine()

    assert (link.kind, link.url, link.bearer) == ("machine", "https://hosted.example", "machine-token")
    assert dict(os.environ) == before


def test_ResolveMachine_FromManyThreadsAtOnce_AlwaysFindsTheMachine(monkeypatch, tmp_path):
    import threading

    _machine_config(tmp_path, monkeypatch)
    failures = []

    def resolve_many():
        for _ in range(50):
            try:
                gateway_link.resolve_machine()
            except Exception as exc:  # noqa: BLE001 - counted, the test asserts there were none
                failures.append(type(exc).__name__)

    threads = [threading.Thread(target=resolve_many) for _ in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()

    assert failures == []


def test_ResolveMachine_NotSignedIn_IsARefusal(monkeypatch):
    from cc_shared import tool_errors

    monkeypatch.setattr(tool_errors, "resolve_credential",
                        lambda: tool_errors.Credential("before-sign-in", "https://hosted.example", None))

    with pytest.raises(gateway_link.NotSignedIn):
        gateway_link.resolve_machine()


# --- The Send dialog, the approval pop-up and the worker, drawn on a stand-in for tkinter ---------------------------
# These once opened a real Tk window and waited on the clock, so they passed or failed on whether the machine had a
# working Tk install and a screen (issue #4442). They now draw on tests/fake_tk.py: the same calls into the product,
# with every `after` run by a tick of the test and every worker thread joined, so the code alone decides.


class FakeActions:
    """Stands in for WindowTransfers: answers at once, records what was asked."""

    def __init__(self, waiting):
        self.waiting = list(waiting)
        self.answered = []
        self.sent = []

    def waiting_here(self):
        return list(self.waiting)

    def answer(self, transfer_id, approve, accept=None):
        self.answered.append((transfer_id, approve, accept))
        self.waiting = [w for w in self.waiting if w.transfer.transfer_id != transfer_id]
        return transfers.Transfer.from_dto(_dto("approved"))

    def others(self):
        return [Destination("devthrottle-mac-mini", "Not sent to from here before.", False)]

    def send(self, entry, to, target, replace, accept):
        self.sent.append((entry, to, target, replace, accept))
        return transfers.Transfer.from_dto(_dto("approved"))

    def wait(self, t):
        return transfers.Transfer.from_dto(_dto("delivered"))


MAC = Destination("devthrottle-mac-mini", "Not sent to from here before.", False, "a" * 64)
MAC_CHANGED = Destination("devthrottle-mac-mini", "Its KEY HAS CHANGED since this machine pinned it.", True, "b" * 64)


def _waiting(receiver=MAC):
    return Waiting(transfers.Transfer.from_dto(_dto("waiting")), receiver)


class FakeWindow:
    def __init__(self, root, threads):
        self.root = root
        self.threads = threads


@pytest.fixture
def window(monkeypatch):
    """The window's root on the stand-in toolkit, with the worker's threads recorded so each tick can join them."""
    from src import transfer_window

    tk, ttk = fake_tk.modules()
    monkeypatch.setitem(sys.modules, "tkinter", tk)
    monkeypatch.setitem(sys.modules, "tkinter.ttk", ttk)
    threads = fake_tk.Threads()
    monkeypatch.setattr(transfer_window, "threading", threads)
    return FakeWindow(fake_tk.Root(), threads)


def _pump(window, condition, ticks=50):
    """Let the window run: each tick waits for every worker thread started so far, then runs every waiting `after`
    call. Counted in ticks, not seconds; True as soon as the condition holds."""
    for _ in range(ticks):
        window.threads.join_all()
        window.root.tick()
        if condition():
            return True
    return False


def test_Popup_AppearsForAWaitingTransfer_AndApproveAnswersFromTheWindow(window):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([_waiting()])
    said = []
    watcher = ApprovalWatcher(window.root, Worker(window.root), fake, said.append, poll_ms=100)

    assert _pump(window, lambda: TID in watcher.open)
    popup = watcher.open[TID]
    assert popup.win.title() == "A secret transfer needs your answer"
    popup.approve_button.invoke()

    assert _pump(window, lambda: TID not in watcher.open and said)
    assert fake.answered == [(TID, True, None)]
    assert said[-1].startswith("Approved: qa-handoff-one from SOREN_NORTH")
    assert not popup.win.winfo_exists()


def test_Popup_AnsweredElsewhere_Closes_AndSaysSo(window):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([_waiting()])
    said = []
    watcher = ApprovalWatcher(window.root, Worker(window.root), fake, said.append, poll_ms=100)
    assert _pump(window, lambda: TID in watcher.open)
    popup = watcher.open[TID]

    fake.waiting = []

    assert _pump(window, lambda: TID not in watcher.open and said)
    assert said[-1].startswith("Answered elsewhere")
    assert not popup.win.winfo_exists()


def test_SendDialog_ListsTheMachines_AndSendSaysHowItEnded(window):
    from src.transfer_window import TransferDialog, Worker

    fake = FakeActions([])
    ended = []
    dialog = TransferDialog(window.root, Worker(window.root), fake, "send", "qa-handoff-one", ended.append)
    assert str(dialog.go.cget("state")) == "disabled"

    assert _pump(window, lambda: str(dialog.go.cget("state")) == "normal")
    assert dialog.machine.get() == "devthrottle-mac-mini"
    dialog.go.invoke()

    assert _pump(window, lambda: ended)
    assert fake.sent == [("qa-handoff-one", "devthrottle-mac-mini", "qa-handoff-one", False, False)]
    assert dialog.status.get() == "Stored qa-handoff-one on devthrottle-mac-mini."
    dialog.close()


def test_Worker_AFailure_ReachesItsCallbackWithTheException(window):
    from src.transfer_window import Worker

    worker = Worker(window.root)
    got = []

    def boom():
        raise gateway_link.GatewayRefusal(409, "already_answered", "It was already answered on the phone.")

    worker.run(boom, lambda r: got.append(("done", r)), lambda e: got.append(("failed", str(e))))

    assert _pump(window, lambda: got)
    assert got == [("failed", "It was already answered on the phone.")]


def test_Worker_ACallbackThatRaises_DoesNotStopLaterResults(window):
    from src.transfer_window import Worker

    worker = Worker(window.root)
    got = []

    def raising(_):
        raise RuntimeError("a widget that was already destroyed")

    worker.run(lambda: 1, raising, lambda e: None)
    worker.run(lambda: 2, got.append, lambda e: None)

    assert _pump(window, lambda: got)
    assert got == [2]


def test_SendDialog_ClosedBeforeTheMachinesArrive_ChangesNothingAndLaterResultsStillArrive(window):
    import threading

    from src.transfer_window import TransferDialog, Worker

    release = threading.Event()
    fake = FakeActions([])
    real_others = fake.others

    def slow_others():
        release.wait()
        return real_others()

    fake.others = slow_others
    worker = Worker(window.root)
    dialog = TransferDialog(window.root, worker, fake, "send", "qa-handoff-one")
    dialog.close()
    release.set()
    _pump(window, lambda: False, ticks=5)
    assert dialog.machine.get() == "" and str(dialog.go.cget("state")) == "disabled"
    got = []
    worker.run(lambda: "later", got.append, lambda e: None)

    assert _pump(window, lambda: got)
    assert got == ["later"]


def test_Popup_ToAMachineWhoseKeyChanged_ApprovesOnlyWithTheBoxTicked_AndSendsThatKey(window):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([_waiting(MAC_CHANGED)])
    watcher = ApprovalWatcher(window.root, Worker(window.root), fake, lambda s: None, poll_ms=100)
    assert _pump(window, lambda: TID in watcher.open)
    popup = watcher.open[TID]

    assert str(popup.approve_button.cget("state")) == "disabled"
    popup.approve_button.invoke()
    assert fake.answered == []
    popup.accept_box.invoke()
    assert str(popup.approve_button.cget("state")) == "normal"
    popup.approve_button.invoke()

    assert _pump(window, lambda: fake.answered)
    assert fake.answered == [(TID, True, "b" * 64)]


def test_Watcher_AFailedPoll_IsSaidOnce_AndItKeepsAsking(window):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([])
    calls = {"n": 0}

    def fails_twice():
        calls["n"] += 1
        if calls["n"] <= 2:
            raise ConnectionError("the Gateway is restarting")
        return [_waiting()]

    fake.waiting_here = fails_twice
    said = []
    watcher = ApprovalWatcher(window.root, Worker(window.root), fake, said.append, poll_ms=100)

    assert _pump(window, lambda: TID in watcher.open)
    assert sum(1 for line in said if line.startswith("Could not check")) == 1
    watcher.open[TID].dismiss()


def test_Watcher_NotSignedIn_IsSaidOnce_AndItStops(window):
    from src.transfer_window import ApprovalWatcher, Worker

    fake = FakeActions([])
    calls = {"n": 0}

    def not_signed_in():
        calls["n"] += 1
        raise gateway_link.NotSignedIn("This machine is not signed in to a Gateway.")

    fake.waiting_here = not_signed_in
    said = []
    ApprovalWatcher(window.root, Worker(window.root), fake, said.append, poll_ms=100)

    assert _pump(window, lambda: said)
    _pump(window, lambda: False, ticks=10)
    assert said == ["Moving secrets between machines is off here: This machine is not signed in to a Gateway."]
    assert calls["n"] == 1


def test_LocalTime_ShowsTheOwnersClock_NotARawUtcStamp():
    from src.transfer_window import local_time

    shown = local_time("2026-10-10T12:15:00Z")

    assert shown.endswith("(your time)") and "T" not in shown
