"""The cc-secrets window's part in moving an entry between machines (the Secret Handoff mission, phase 5): the Send
and Get dialogs opened from the list, and the pop-up that asks the owner about a transfer waiting on this machine.

This module only draws. What a button does is in window_transfers, tested without a screen. Every Gateway call runs
off the window's thread and comes back through one queue that the window drains, so a click answers at once and the
window never stalls on the network. Nothing here shows, logs or prints a secret: a transfer is a name, two machines,
a reason and a status.
"""

from __future__ import annotations

import queue
import threading
from datetime import datetime
from typing import Callable, Dict, List, Optional

from . import window_style
from .errors import describe
from .gateway_link import GatewayRefusal, NotSignedIn
from .window_actions import log_failure
from .window_transfers import Destination, Waiting, WindowTransfers, ended_sentence

POPUP_POLL_MS = 5000
QUEUE_DRAIN_MS = 200


class Worker:
    """Run a call off the window's thread and hand its result back on the window's thread."""

    def __init__(self, root) -> None:
        self._root = root
        self._done: "queue.Queue[Callable[[], None]]" = queue.Queue()
        self._drain()

    def _drain(self) -> None:
        # One callback that fails must never end the loop: every later result, and every pop-up, comes through it.
        try:
            while True:
                try:
                    callback = self._done.get_nowait()
                except queue.Empty:
                    break
                try:
                    callback()
                except Exception as exc:  # the window's own loop: logged, and the next callback still runs
                    log_failure("show a secret transfer's result", exc)
        finally:
            if self._root.winfo_exists():
                self._root.after(QUEUE_DRAIN_MS, self._drain)

    def run(self, work: Callable[[], object], done: Callable[[object], None], failed: Callable[[BaseException], None]):
        def body() -> None:
            try:
                result = work()
            except BaseException as exc:  # the worker thread's entry point: handed back to the window to show
                error = exc  # bound here: Python unbinds `exc` when this block ends, before the window runs the call
                self._done.put(lambda: failed(error))
                return
            self._done.put(lambda: done(result))

        threading.Thread(target=body, daemon=True).start()


def refusal_text(exc: BaseException) -> str:
    """A Gateway refusal in its own words; anything else described without its message (it might quote a value)."""
    return str(exc) if isinstance(exc, (GatewayRefusal, NotSignedIn)) else describe(exc)


def local_time(utc_text: str) -> str:
    """'2026-10-10T12:15:00Z' as this machine's clock shows it, for example '08:15 (your time)'."""
    try:
        moment = datetime.fromisoformat(utc_text.replace("Z", "+00:00"))
    except ValueError:
        return utc_text
    return moment.astimezone().strftime("%H:%M") + " (your time)"


class TransferDialog:
    """Send the selected entry to another machine, or get an entry from one. The click on Send or Get is the owner's
    approval; the dialog then waits for the delivery and says how it ended."""

    def __init__(self, parent, worker: Worker, actions: WindowTransfers, mode: str, entry: str = "",
                 on_done: Optional[Callable[[str], None]] = None) -> None:
        import tkinter as tk
        from tkinter import ttk

        self.mode = mode
        self._worker = worker
        self._actions = actions
        self._on_done = on_done
        self._destinations: List[Destination] = []
        self.win = win = tk.Toplevel(parent)
        win.title("Send to another machine" if mode == "send" else "Get from another machine")
        win.resizable(False, False)
        win.transient(parent)
        frame = ttk.Frame(win, padding=(20, 16, 20, 14))
        frame.grid(row=0, column=0, sticky="nsew")
        frame.columnconfigure(1, weight=1)

        heading = "Send an entry to another machine" if mode == "send" else "Get an entry from another machine"
        ttk.Label(frame, text=heading, style="Title.TLabel").grid(row=0, column=0, columnspan=2, sticky="w")
        ttk.Label(frame, text="It travels sealed to that machine's key. Clicking the button below is your approval.",
                  style="Muted.TLabel").grid(row=1, column=0, columnspan=2, sticky="w", pady=(2, 12))

        self.entry = tk.StringVar(value=entry)
        self.machine = tk.StringVar(value="")
        self.target = tk.StringVar(value=entry)
        self.replace = tk.BooleanVar(value=False)
        self.accept_key = tk.BooleanVar(value=False)

        ttk.Label(frame, text="Entry" if mode == "send" else "Entry name there").grid(row=2, column=0, sticky="w", pady=3)
        self.entry_box = ttk.Entry(frame, textvariable=self.entry, width=34,
                                   state="readonly" if mode == "send" else "normal")
        self.entry_box.grid(row=2, column=1, sticky="we", pady=3)
        ttk.Label(frame, text="To" if mode == "send" else "From").grid(row=3, column=0, sticky="w", pady=3)
        self.machine_box = ttk.Combobox(frame, textvariable=self.machine, state="readonly", width=32)
        self.machine_box.grid(row=3, column=1, sticky="we", pady=3)
        self.machine_box.bind("<<ComboboxSelected>>", lambda _e: self._machine_chosen())
        ttk.Label(frame, text="Store as").grid(row=4, column=0, sticky="w", pady=3)
        ttk.Entry(frame, textvariable=self.target, width=34).grid(row=4, column=1, sticky="we", pady=3)
        ttk.Checkbutton(frame, text="Replace an entry of that name already there", variable=self.replace).grid(
            row=5, column=1, sticky="w", pady=(6, 0))
        self.key_note = tk.StringVar(value="")
        ttk.Label(frame, textvariable=self.key_note, style="Muted.TLabel", wraplength=380).grid(
            row=6, column=0, columnspan=2, sticky="w", pady=(8, 0))
        self.accept_box = ttk.Checkbutton(frame, text="I know why its key changed: send to the new key",
                                          variable=self.accept_key)
        self.status = tk.StringVar(value="Finding the machines that can take part...")
        ttk.Label(frame, textvariable=self.status, wraplength=380).grid(row=8, column=0, columnspan=2, sticky="w",
                                                                        pady=(10, 0))
        buttons = ttk.Frame(frame)
        buttons.grid(row=9, column=0, columnspan=2, sticky="e", pady=(14, 0))
        self.go = ttk.Button(buttons, text="Send" if mode == "send" else "Get", style="Accent.TButton",
                             command=self.start, state="disabled")
        self.go.grid(row=0, column=0)
        self.close_button = ttk.Button(buttons, text="Cancel", command=self.close)
        self.close_button.grid(row=0, column=1, padx=(8, 0))
        win.bind("<Escape>", lambda _e: self.close())
        worker.run(actions.others, self._listed, self._failed)

    def _open(self) -> bool:
        """False once the owner closed the dialog: a late result then changes nothing."""
        return bool(self.win.winfo_exists())

    def _listed(self, destinations) -> None:
        if not self._open():
            return
        self._destinations = list(destinations)
        names = [d.name for d in self._destinations]
        self.machine_box.configure(values=names)
        if not names:
            self.status.set("No other machine can take part right now. A machine is listed while a Director that "
                            "can take part is running on it.")
            return
        self.machine.set(names[0])
        self._machine_chosen()
        self.status.set("")
        self.go.configure(state="normal")

    def _machine_chosen(self) -> None:
        chosen = next((d for d in self._destinations if d.name == self.machine.get()), None)
        self.key_note.set(chosen.key_note if chosen else "")
        if chosen is not None and chosen.key_changed and self.mode == "send":
            self.accept_box.grid(row=7, column=0, columnspan=2, sticky="w", pady=(4, 0))
        else:
            self.accept_key.set(False)
            self.accept_box.grid_remove()

    def start(self) -> None:
        entry, machine = self.entry.get().strip(), self.machine.get().strip()
        if not entry or not machine:
            self.status.set("Choose the entry and the machine first.")
            return
        self.go.configure(state="disabled")
        self.status.set("Approved here. Moving it now...")
        target, replace, accept = self.target.get().strip() or entry, self.replace.get(), self.accept_key.get()

        def work():
            t = (self._actions.send(entry, machine, target, replace, accept) if self.mode == "send"
                 else self._actions.request(entry, machine, target, replace))
            return self._actions.wait(t)

        self._worker.run(work, self._ended, self._failed)

    def _ended(self, t) -> None:
        sentence = ended_sentence(t)
        if self._on_done is not None:
            self._on_done(sentence)
        if not self._open():
            return
        self.status.set(sentence)
        self.close_button.configure(text="Close")

    def _failed(self, exc: BaseException) -> None:
        if not isinstance(exc, (GatewayRefusal, NotSignedIn)):
            log_failure(f"{self.mode} a secret transfer", exc)
        if not self._open():
            return
        self.status.set(f"Not done: {refusal_text(exc)}")
        self.close_button.configure(text="Close")

    def close(self) -> None:
        self.win.destroy()


class ApprovalPopup:
    """One transfer waiting on this machine for the owner's answer: what, from where to where, who asked and why."""

    def __init__(self, parent, worker: Worker, actions: WindowTransfers, waiting: Waiting,
                 on_closed: Callable[[str, str], None]) -> None:
        import tkinter as tk
        from tkinter import ttk

        transfer = waiting.transfer
        self.transfer = transfer
        self.receiver = waiting.receiver
        self._worker = worker
        self._actions = actions
        self._on_closed = on_closed
        self.win = win = tk.Toplevel(parent)
        win.title("A secret transfer needs your answer")
        win.resizable(False, False)
        win.attributes("-topmost", True)
        frame = ttk.Frame(win, padding=(20, 16, 20, 14), style="Ask.TFrame")
        frame.grid(row=0, column=0, sticky="nsew")
        ttk.Label(frame, text="A secret transfer needs your answer", style="AskTitle.TLabel").grid(
            row=0, column=0, sticky="w")
        lines = [transfer.summary, f"Asked by {transfer.asked_by}."]
        if transfer.reason:
            lines.append(f"Why: {transfer.reason}")
        if transfer.replace_note:
            lines.append(transfer.replace_note)
        self.key_changed = bool(self.receiver is not None and self.receiver.key_changed)
        if self.key_changed:
            lines.append(f"{transfer.to_machine}'s KEY HAS CHANGED since this machine pinned it. Approve only if you "
                         "know why (it was reinstalled, for example).")
        lines.append(f"Answer by {local_time(transfer.expires_utc)}. You can also answer on the phone or in the "
                     "Cockpit.")
        for row, line in enumerate(lines, start=1):
            ttk.Label(frame, text=line, style="Ask.TLabel", wraplength=420).grid(row=row, column=0, sticky="w",
                                                                                 pady=(6 if row == 1 else 2, 0))
        self.accept_key = tk.BooleanVar(value=False)
        self.accept_box = None
        if self.key_changed:
            self.accept_box = ttk.Checkbutton(frame, text="I know why its key changed: send to the new key",
                                              variable=self.accept_key, command=self._accept_toggled)
            self.accept_box.grid(row=len(lines) + 1, column=0, sticky="w", pady=(8, 0))
        self.status = tk.StringVar(value="")
        ttk.Label(frame, textvariable=self.status, style="Ask.TLabel", wraplength=420).grid(
            row=len(lines) + 2, column=0, sticky="w", pady=(8, 0))
        buttons = ttk.Frame(frame, style="Ask.TFrame")
        buttons.grid(row=len(lines) + 3, column=0, sticky="e", pady=(12, 0))
        self.approve_button = ttk.Button(buttons, text="Approve", style="Accent.TButton", command=lambda: self.answer(True),
                                         state="disabled" if self.key_changed else "normal")
        self.approve_button.grid(row=0, column=0)
        self.deny_button = ttk.Button(buttons, text="Deny", style="Danger.TButton", command=lambda: self.answer(False))
        self.deny_button.grid(row=0, column=1, padx=(8, 0))
        win.protocol("WM_DELETE_WINDOW", self.dismiss)

    def _accept_toggled(self) -> None:
        self.approve_button.configure(state="normal" if self.accept_key.get() else "disabled")

    def answer(self, approve: bool) -> None:
        if approve and self.key_changed and not self.accept_key.get():
            return
        accept = self.receiver.fingerprint if (approve and self.key_changed) else None
        self.approve_button.configure(state="disabled")
        self.deny_button.configure(state="disabled")
        self.status.set("Sending your answer...")
        self._worker.run(lambda: self._actions.answer(self.transfer.transfer_id, approve, accept),
                         lambda t: self._close(f"{'Approved' if approve else 'Denied'}: {self.transfer.summary}."),
                         self._failed)

    def answered_elsewhere(self, status_text: str) -> None:
        self._close(f"Answered elsewhere: {status_text}")

    def _failed(self, exc: BaseException) -> None:
        if not isinstance(exc, GatewayRefusal):
            log_failure("answer a secret transfer", exc)
        self._close(f"Not answered here: {refusal_text(exc)}")

    def dismiss(self) -> None:
        """Closed without an answer: the transfer keeps waiting for one elsewhere."""
        self._close("")

    def _close(self, sentence: str) -> None:
        if self.win.winfo_exists():
            self.win.destroy()
        self._on_closed(self.transfer.transfer_id, sentence)


class ApprovalWatcher:
    """Asks the Gateway every few seconds for transfers waiting on this machine, and pops each one up once. A machine
    that is not signed in says why once, in the window's status line, and the watcher stops. Any other failure (a
    Gateway restarting, a laptop waking up) is said once and the watcher keeps asking."""

    def __init__(self, root, worker: Worker, actions: WindowTransfers, say: Callable[[str], None],
                 poll_ms: int = POPUP_POLL_MS) -> None:
        self._root = root
        self._worker = worker
        self._actions = actions
        self._say = say
        self._poll_ms = poll_ms
        self.open: Dict[str, ApprovalPopup] = {}
        self._dismissed: set = set()
        self._stopped = False
        self._busy = False
        self._failing = False
        self.poll()

    def poll(self) -> None:
        if self._stopped or not self._root.winfo_exists():
            return
        if not self._busy:
            self._busy = True
            self._worker.run(self._actions.waiting_here, self._arrived, self._failed)
        self._root.after(self._poll_ms, self.poll)

    def _arrived(self, waiting) -> None:
        self._busy = False
        if self._failing:
            self._failing = False
            self._say("Checking for secret transfers to answer again.")
        ids = {w.transfer.transfer_id for w in waiting}
        for transfer_id, popup in list(self.open.items()):
            if transfer_id not in ids:
                popup.answered_elsewhere("it was answered or expired.")
        for w in waiting:
            transfer_id = w.transfer.transfer_id
            if transfer_id not in self.open and transfer_id not in self._dismissed:
                self.open[transfer_id] = ApprovalPopup(self._root, self._worker, self._actions, w, self._closed)

    def _closed(self, transfer_id: str, sentence: str) -> None:
        self.open.pop(transfer_id, None)
        if not sentence:
            self._dismissed.add(transfer_id)
        else:
            self._say(sentence)

    def _failed(self, exc: BaseException) -> None:
        self._busy = False
        if isinstance(exc, NotSignedIn):
            self._stopped = True
            self._say(f"Moving secrets between machines is off here: {refusal_text(exc)}")
            return
        if self._failing:
            return
        self._failing = True
        if not isinstance(exc, GatewayRefusal):
            log_failure("look for secret transfers to answer", exc)
        self._say(f"Could not check for secret transfers to answer ({refusal_text(exc)}). Trying again.")
