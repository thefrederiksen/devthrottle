"""The small window in which the owner types a credential that an agent asked for (`cc-secrets ask`).

The agent fills in everything it knows - the name, the user name, a note, why it needs it - and the owner
types only the secret. This module draws the window and nothing else: it never stores, logs or prints
anything. When the owner presses Save it hands the fields to the caller's `on_save`, which validates and
stores them through the same code path as `cc-secrets add`. `on_save` returns None when the entry was
saved (the window closes) or a message to show in the window (it stays open, so a short secret or a name
that already exists can be corrected without losing what was typed).

The request carries no secret field, by design: the secret can only come from a person typing it here.
"""

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from typing import Callable, Optional

from .errors import CcSecretsError


class NoDisplayError(CcSecretsError):
    """There is no screen to show the window on."""


NO_DISPLAY_MESSAGE = ("cc-secrets ask needs a screen to show its window on, and this process has none ({why}). "
                      "Run it on the owner's desktop (a session on that machine, not an SSH shell), or have the "
                      "owner run 'cc-secrets add NAME' in a terminal there.")


@dataclass(frozen=True)
class WindowRequest:
    """What the agent pre-fills. Deliberately without a secret."""
    name: str
    username: str
    notes: str
    reason: str
    asked_by: str
    is_setting: bool
    agents_may_use: bool
    summary: str
    exists: bool


@dataclass(frozen=True)
class WindowInput:
    """What the owner submitted. `secret` is the typed value; it is passed straight to the store."""
    name: str
    username: str
    notes: str
    secret: str
    agents_may_use: bool


def display_problem() -> Optional[str]:
    """Why there is no display to open a window on, or None when there may be one. Windows and macOS always
    have one for a desktop user; on Linux and other X11 systems a window needs DISPLAY or WAYLAND_DISPLAY."""
    if sys.platform in ("win32", "darwin"):
        return None
    if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"):
        return None
    return "neither DISPLAY nor WAYLAND_DISPLAY is set"


def show(request: WindowRequest, on_save: Callable[[WindowInput], Optional[str]]) -> bool:
    """Show the window until the owner saves or cancels. True when on_save accepted an entry."""
    problem = display_problem()
    if problem is not None:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=problem))
    import tkinter as tk
    from tkinter import ttk

    try:
        root = tk.Tk()
    except tk.TclError as exc:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=f"the window system refused: {exc}")) from exc

    kind_word = "setting" if request.is_setting else "secret"
    root.title(f"cc-secrets - enter a {kind_word}")
    root.resizable(False, False)
    saved = {"value": False}

    frame = ttk.Frame(root, padding=16)
    frame.grid(row=0, column=0, sticky="nsew")
    wrap = 440
    row = 0

    def label(text: str, **style) -> None:
        nonlocal row
        ttk.Label(frame, text=text, wraplength=wrap, justify="left", **style).grid(
            row=row, column=0, columnspan=2, sticky="w", pady=(0, 8))
        row += 1

    label(f"{request.asked_by} asks you to enter a {kind_word}.", font=("TkDefaultFont", 10, "bold"))
    if request.reason:
        label(f"Why: {request.reason}")
    if request.summary:
        label(request.summary)

    def field(caption: str, value: str):
        nonlocal row
        ttk.Label(frame, text=caption).grid(row=row, column=0, sticky="w", padx=(0, 8), pady=3)
        var = tk.StringVar(value=value)
        box = ttk.Entry(frame, textvariable=var, width=44)
        box.grid(row=row, column=1, sticky="we", pady=3)
        row += 1
        return var, box

    name_var, name_box = field("Name", request.name)
    user_var, _ = field("Username", request.username)
    notes_var, _ = field("Notes", request.notes)

    ttk.Label(frame, text="Value" if request.is_setting else "Secret").grid(row=row, column=0, sticky="w",
                                                                           padx=(0, 8), pady=3)
    secret_var = tk.StringVar(value="")
    secret_box = ttk.Entry(frame, textvariable=secret_var, width=44, show="" if request.is_setting else "*")
    secret_box.grid(row=row, column=1, sticky="we", pady=3)
    row += 1

    if not request.is_setting:
        shown = tk.BooleanVar(value=False)
        ttk.Checkbutton(frame, text="Show the secret", variable=shown,
                        command=lambda: secret_box.configure(show="" if shown.get() else "*")).grid(
            row=row, column=1, sticky="w")
        row += 1

    agents_var = tk.BooleanVar(value=request.agents_may_use)
    ttk.Checkbutton(frame, text="Sessions on this machine may use it", variable=agents_var).grid(
        row=row, column=1, sticky="w", pady=(0, 6))
    row += 1

    replace_text = (f"'{request.name}' already exists. Save REPLACES it." if request.exists else "")
    message_var = tk.StringVar(value=replace_text)
    ttk.Label(frame, textvariable=message_var, foreground="#b00020", wraplength=wrap, justify="left").grid(
        row=row, column=0, columnspan=2, sticky="w", pady=(4, 8))
    row += 1

    def save(_event=None) -> None:
        result = on_save(WindowInput(name=name_var.get().strip(), username=user_var.get().strip(),
                                     notes=notes_var.get().strip(), secret=secret_var.get(),
                                     agents_may_use=bool(agents_var.get())))
        if result is None:
            saved["value"] = True
            secret_var.set("")
            root.destroy()
        else:
            message_var.set(result)

    def cancel(_event=None) -> None:
        secret_var.set("")
        root.destroy()

    buttons = ttk.Frame(frame)
    buttons.grid(row=row, column=0, columnspan=2, sticky="e")
    ttk.Button(buttons, text="Cancel", command=cancel).grid(row=0, column=0, padx=(0, 8))
    ttk.Button(buttons, text="Save", command=save).grid(row=0, column=1)

    root.bind("<Return>", save)
    root.bind("<Escape>", cancel)
    root.protocol("WM_DELETE_WINDOW", cancel)

    # Bring it to the front: the owner is usually looking at another window when an agent asks.
    root.update_idletasks()
    x = (root.winfo_screenwidth() - root.winfo_reqwidth()) // 2
    y = (root.winfo_screenheight() - root.winfo_reqheight()) // 3
    root.geometry(f"+{max(x, 0)}+{max(y, 0)}")
    root.lift()
    root.attributes("-topmost", True)
    root.after(500, lambda: root.attributes("-topmost", False))
    root.focus_force()
    (secret_box if request.name else name_box).focus_set()
    root.mainloop()
    return saved["value"]
