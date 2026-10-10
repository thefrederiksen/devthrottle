"""The cc-secrets windows, drawn with tkinter: the list (`cc-secrets ui`), the add / edit form opened from it, and
the pop-up an agent opens with `cc-secrets ask`. The two forms are one form in different modes.

This module only draws. What a button does - save, reveal, delete, the rows of the list, sorting and filtering -
is in window_actions; where a window goes and what the list remembers is in window_layout; how it looks is in
window_style. All three are tested without a screen. Nothing here stores, logs or prints a secret; a password's
box always starts empty, because no request carries one.

ASCII only: a password in the list is a row of asterisks with a Show link beside it.
"""

from __future__ import annotations

import os
import platform
import sys
from pathlib import Path
from typing import Callable, Dict, List, Optional, Tuple

from . import paths, window_layout, window_style
from .errors import CcSecretsError, describe
from .store import USES
from .window_actions import (MODE_ADD, MODE_ASK, MODE_EDIT, SHOW_ALL, SHOW_PASSWORDS, SHOW_SETTINGS, FormInput,
                             FormRequest, Row, WindowActions, filter_rows, log_failure, replace_warning, sort_rows)


class NoDisplayError(CcSecretsError):
    """There is no screen to show the window on."""


NO_DISPLAY_MESSAGE = ("cc-secrets needs a screen to show its window on, and this process has none ({why}). "
                      "Run it on the owner's desktop (a session on that machine, not an SSH shell), or have the "
                      "owner run 'cc-secrets add NAME' in a terminal there.")

MASK = "**********"
WRAP = 470
PREFS_FILE = "window.json"
LIST_MIN_WIDTH = 760
LIST_MIN_HEIGHT = 420


def _windows_desktop_visible() -> bool:
    """True when this process runs on the interactive window station, the one a person sees. An SSH shell into
    Windows, a service, or a task set to run whether the user is logged on or not gets an invisible one: Tk
    opens a window there without complaint, nobody can see it, and the command would wait forever."""
    import ctypes
    from ctypes import wintypes

    class USEROBJECTFLAGS(ctypes.Structure):
        _fields_ = [("fInherit", wintypes.BOOL), ("fReserved", wintypes.BOOL), ("dwFlags", wintypes.DWORD)]

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetProcessWindowStation.restype = wintypes.HANDLE
    user32.GetUserObjectInformationW.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD,
                                                 ctypes.POINTER(wintypes.DWORD)]
    user32.GetUserObjectInformationW.restype = wintypes.BOOL
    UOI_FLAGS = 1
    WSF_VISIBLE = 1
    flags = USEROBJECTFLAGS()
    needed = wintypes.DWORD(0)
    station = user32.GetProcessWindowStation()
    if not station or not user32.GetUserObjectInformationW(station, UOI_FLAGS, ctypes.byref(flags),
                                                           ctypes.sizeof(flags), ctypes.byref(needed)):
        raise NoDisplayError(f"Windows could not say whether this process has a visible desktop "
                             f"(error {ctypes.get_last_error()}).")
    return bool(flags.dwFlags & WSF_VISIBLE)


def display_problem() -> Optional[str]:
    """Why there is no display to open a window on, or None when there may be one. On Windows the process must be
    on the visible window station; on Linux and other X11 systems it needs DISPLAY or WAYLAND_DISPLAY. On macOS
    Tk itself refuses when there is no window server, which open_root reports."""
    if sys.platform == "win32":
        return None if _windows_desktop_visible() else "this process is not on the desktop a person sees"
    if sys.platform == "darwin":
        return None
    if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"):
        return None
    return "neither DISPLAY nor WAYLAND_DISPLAY is set"


def open_root(title: str):
    """A new, styled top-level window, still hidden: the caller places it and then shows it, so it never flashes
    up in a corner first. NoDisplayError says why there is no screen."""
    problem = display_problem()
    if problem is not None:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=problem))
    import tkinter as tk

    window_style.make_dpi_aware()
    window_style.set_taskbar_identity()
    try:
        root = tk.Tk()
    except tk.TclError as exc:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=f"the window system refused: {exc}")) from exc
    root.withdraw()
    root.title(title)
    window_style.apply(root)
    window_style.set_icon(root)
    return root


def monitors(win) -> List[window_layout.Rect]:
    """Every monitor's work area. Tk alone knows only the main screen on Windows, which is why a form used to
    open on the main screen while the list was on another. On macOS and Linux Tk reports one rectangle for the
    whole screen area, so there a window is kept on that area, not on one monitor of it."""
    if sys.platform == "win32":
        return window_layout.windows_monitors()
    return [(0, 0, win.winfo_screenwidth(), win.winfo_screenheight())]


def bring_to_front(win) -> None:
    """Show a placed window in front: the owner is usually looking at something else when it opens."""
    win.deiconify()
    win.lift()
    win.attributes("-topmost", True)
    win.after(500, lambda: win.attributes("-topmost", False))
    win.focus_force()


def _client_rect(win) -> window_layout.Rect:
    x, y = win.winfo_rootx(), win.winfo_rooty()
    return x, y, x + win.winfo_width(), y + win.winfo_height()


class EntryForm:
    """The add / edit / ask form, built inside `win`. `on_save` returns None when saved, else a message to show.
    `on_reveal_current` (edit only) returns the stored password for the Show button. `on_close(saved)` closes
    the window."""

    def __init__(self, win, request: FormRequest, on_save: Callable[[FormInput], Optional[str]],
                 on_close: Callable[[bool], None], on_reveal_current: Optional[Callable[[], str]] = None) -> None:
        import tkinter as tk
        from tkinter import ttk

        self._win = win
        self._request = request
        self._on_save = on_save
        self._on_close = on_close
        self._on_reveal_current = on_reveal_current
        self._shown = False
        self._shown_current = ""  # the stored password, once Show has fetched it
        win.configure(background=window_style.BACKGROUND)
        frame = ttk.Frame(win, padding=(24, 20, 24, 18))
        frame.grid(row=0, column=0, sticky="nsew")
        frame.columnconfigure(1, weight=1)
        row = 0

        if request.mode == MODE_ASK:
            card = ttk.Frame(frame, style="Ask.TFrame", padding=(14, 12))
            card.grid(row=row, column=0, columnspan=3, sticky="we", pady=(0, 16))
            ttk.Label(card, text=f"{request.asked_by} asks you for {'a setting' if request.kind_setting else 'a password'}",
                      style="AskTitle.TLabel", wraplength=WRAP).grid(row=0, column=0, sticky="w")
            if request.reason:
                ttk.Label(card, text=request.reason, style="Ask.TLabel", wraplength=WRAP, justify="left").grid(
                    row=1, column=0, sticky="w", pady=(6, 0))
            ttk.Label(card, text="Everything is filled in except the secret. Type it and press Save; the session "
                                 "only learns whether you saved.", style="Ask.TLabel", foreground=window_style.MUTED,
                      wraplength=WRAP, justify="left").grid(row=2, column=0, sticky="w", pady=(6, 0))
            if request.exists:
                ttk.Label(card, text=replace_warning(request.name), style="AskWarning.TLabel", wraplength=WRAP).grid(
                    row=3, column=0, sticky="w", pady=(8, 0))
        else:
            heading = f"Edit {request.name}" if request.mode == MODE_EDIT else "Add a password or setting"
            ttk.Label(frame, text=heading, style="Title.TLabel").grid(row=row, column=0, columnspan=3, sticky="w",
                                                                    pady=(0, 16))
        row += 1

        def caption(text: str):
            label = ttk.Label(frame, text=text, style="Field.TLabel")
            label.grid(row=row, column=0, sticky="w", padx=(0, 16), pady=5)
            return label

        def text_box(value: str, width: int = 44):
            nonlocal row
            var = tk.StringVar(value=value)
            box = ttk.Entry(frame, textvariable=var, width=width)
            box.grid(row=row, column=1, columnspan=2, sticky="we", pady=5)
            row += 1
            return var, box

        def hint(text: str):
            nonlocal row
            label = ttk.Label(frame, text=text, style="Faint.TLabel", wraplength=WRAP)
            label.grid(row=row, column=1, columnspan=2, sticky="w", pady=(0, 4))
            row += 1
            return label

        caption("Name")
        self._name, self._name_box = text_box(request.name)
        if request.mode == MODE_EDIT:
            self._name_box.state(["disabled"])

        caption("Kind")
        self._setting = tk.BooleanVar(value=request.kind_setting)
        kinds = ttk.Frame(frame)
        kinds.grid(row=row, column=1, columnspan=2, sticky="w", pady=5)
        ttk.Radiobutton(kinds, text="Password - kept hidden", variable=self._setting, value=False,
                        style="Choice.Toolbutton", command=self._kind_changed).grid(row=0, column=0)
        ttk.Radiobutton(kinds, text="Setting - not secret", variable=self._setting, value=True,
                        style="Choice.Toolbutton", command=self._kind_changed).grid(row=0, column=1, padx=(4, 0))
        row += 1
        self._kind_hint = hint("")

        caption("User name")
        self._username, self._user_box = text_box(request.username)

        self._secret_caption = caption("Password")
        self._secret = tk.StringVar(value=request.setting_value)
        self._secret_box = ttk.Entry(frame, textvariable=self._secret, width=36, show="*")
        self._secret_box.grid(row=row, column=1, sticky="we", pady=5)
        self._eye = ttk.Button(frame, text="Show", width=6, command=self._toggle_eye)
        self._eye.grid(row=row, column=2, sticky="e", padx=(6, 0), pady=5)
        row += 1
        self._secret_hint = hint("")

        caption("Notes")
        self._notes, self._notes_box = text_box(request.notes)

        ttk.Separator(frame).grid(row=row, column=0, columnspan=3, sticky="we", pady=(12, 10))
        row += 1

        caption("Agents")
        self._agents = tk.BooleanVar(value=request.agents_may_use)
        self._uses = {use: tk.BooleanVar(value=use in request.uses) for use in USES}
        access = ttk.Frame(frame)
        access.grid(row=row, column=1, columnspan=2, sticky="w", pady=5)
        ttk.Checkbutton(access, text="Agents may use it", variable=self._agents,
                        command=self._access_changed).grid(row=0, column=0, sticky="w")
        self._run_check = ttk.Checkbutton(access, text="to run commands (cc-secrets run)", variable=self._uses["run"],
                                          command=self._access_changed)
        self._run_check.grid(row=1, column=0, sticky="w", padx=(24, 0), pady=(4, 0))
        self._login_check = ttk.Checkbutton(access, text="to log in to websites (cc-secrets login)",
                                            variable=self._uses["login"], command=self._access_changed)
        self._login_check.grid(row=2, column=0, sticky="w", padx=(24, 0), pady=(4, 0))
        row += 1

        caption("Login sites")
        self._domains, self._domains_box = text_box(request.domains)
        hint("The addresses a browser login may type it into, for example https://example.com")

        self._message = tk.StringVar(value="")
        self._message_label = ttk.Label(frame, textvariable=self._message, wraplength=WRAP + 120, justify="left")
        self._message_label.grid(row=row, column=0, columnspan=3, sticky="we", pady=(12, 0))
        self._message_label.grid_remove()
        row += 1

        buttons = ttk.Frame(frame)
        buttons.grid(row=row, column=0, columnspan=3, sticky="e", pady=(18, 0))
        self._cancel_button = ttk.Button(buttons, text="Cancel", command=self.cancel)
        self._cancel_button.grid(row=0, column=0, padx=(0, 8))
        self._save_button = ttk.Button(buttons, text="Save", style="Accent.TButton", command=self.save)
        self._save_button.grid(row=0, column=1)

        # Return saves from a text box, and presses whichever button has the focus - never Save from Cancel.
        for box in (self._name_box, self._user_box, self._secret_box, self._notes_box, self._domains_box):
            box.bind("<Return>", self.save)
        self._save_button.bind("<Return>", self.save)
        self._cancel_button.bind("<Return>", self.cancel)
        win.bind("<Escape>", self.cancel)
        win.protocol("WM_DELETE_WINDOW", self.cancel)
        self._kind_changed()
        self._access_changed()
        self._opened_with = self._snapshot()

    def focus_first(self) -> None:
        """The box the owner types into first: the secret when everything else is filled in, else the name."""
        if self._request.mode == MODE_EDIT or self._request.name:
            self._secret_box.focus_set()
            self._secret_box.icursor("end")
        else:
            self._name_box.focus_set()

    def _snapshot(self) -> Tuple:
        return (self._name.get(), bool(self._setting.get()), self._username.get(), self._secret.get(),
                self._notes.get(), bool(self._agents.get()), tuple(u for u in USES if self._uses[u].get()),
                self._domains.get())

    def _changed(self) -> bool:
        now = self._snapshot()
        if self._request.mode == MODE_EDIT and not self._opened_with[1] and self._shown_current:
            # Showing the stored password filled the box; that alone is not a change.
            now = now[:3] + (self._opened_with[3] if now[3] == self._shown_current else now[3],) + now[4:]
        return now != self._opened_with

    def _kind_changed(self) -> None:
        setting = bool(self._setting.get())
        self._secret_caption.configure(text="Value" if setting else "Password")
        if setting:
            self._secret_box.configure(show="")
            self._eye.grid_remove()
            self._kind_hint.configure(text="Not secret, like an email address or a host. Shown in full.")
        else:
            self._secret_box.configure(show="" if self._shown else "*")
            self._eye.grid()
            self._kind_hint.configure(text="Hidden everywhere. Agents can use it but never see it.")
        editing_password = self._request.mode == MODE_EDIT and not setting
        self._secret_hint.configure(text="Leave it empty to keep the current password." if editing_password else "")
        if editing_password:
            self._secret_hint.grid()
        else:
            self._secret_hint.grid_remove()

    def _access_changed(self) -> None:
        allowed = bool(self._agents.get())
        for check in (self._run_check, self._login_check):
            check.state(["!disabled"] if allowed else ["disabled"])
        login = allowed and bool(self._uses["login"].get())
        self._domains_box.state(["!disabled"] if login else ["disabled"])

    def _toggle_eye(self) -> None:
        if self._shown:
            self._shown = False
            self._secret_box.configure(show="*")
            self._eye.configure(text="Show")
            return
        if not self._secret.get() and self._on_reveal_current is not None:
            try:
                self._shown_current = self._on_reveal_current()
                self._secret.set(self._shown_current)
            except Exception as exc:  # the Show button's click handler: say why in the form
                log_failure("reveal", exc)
                self._say(f"Could not show it: {describe(exc)}")
                return
        self._shown = True
        self._secret_box.configure(show="")
        self._eye.configure(text="Hide")

    def _say(self, text: str) -> None:
        warning = text.startswith("An entry called")
        self._message_label.configure(style="Warning.TLabel" if warning else "Error.TLabel")
        self._message.set(text)
        self._message_label.grid()

    def save(self, _event=None) -> str:
        result = self._on_save(FormInput(
            name=self._name.get().strip(), kind_setting=bool(self._setting.get()),
            username=self._username.get().strip(), secret=self._secret.get(), notes=self._notes.get().strip(),
            agents_may_use=bool(self._agents.get()), uses=[u for u in USES if self._uses[u].get()],
            domains=self._domains.get()))
        if result is None:
            self._secret.set("")
            self._on_close(True)
        else:
            self._say(result)
        return "break"

    def cancel(self, _event=None) -> str:
        from tkinter import messagebox

        if self._changed() and not messagebox.askyesno(
                "Discard changes?", "You have changes that are not saved. Close without saving them?",
                icon="warning", default="no", parent=self._win):
            return "break"
        self._secret.set("")
        self._on_close(False)
        return "break"


def show_ask(request: FormRequest, actions: WindowActions) -> bool:
    """The agent's pop-up, in its own window on the monitor under the mouse, until the owner saves or cancels.
    True when saved."""
    root = open_root("cc-secrets - a session is asking for a password")
    root.resizable(False, False)
    outcome = {"saved": False}

    def close(saved: bool) -> None:
        outcome["saved"] = saved
        root.destroy()

    form = EntryForm(root, request, lambda typed: actions.submit(typed, MODE_ASK), close)
    root.update_idletasks()
    x, y = window_layout.centre_on_monitor(root.winfo_pointerxy(), root.winfo_reqwidth(), root.winfo_reqheight(),
                                           monitors(root))
    root.geometry(f"+{x}+{y}")
    bring_to_front(root)
    form.focus_first()
    root.mainloop()
    return outcome["saved"]


class ListWindow:
    """Every entry, searchable, sortable and filtered by kind. Settings show in full; a password shows as
    asterisks until its Show is clicked, and stays shown until Hide is clicked (owner ruling 2026-10-09). No Copy
    button (owner ruling)."""

    # (key, heading, width, stretches)
    COLUMNS = (("name", "Name", 230, True), ("kind", "Kind", 80, False), ("username", "User name", 190, True),
               ("value", "Value", 200, True), ("eye", "", 56, False), ("access", "Agents may", 96, False),
               ("used", "Last used", 104, False))
    VALUE_COLUMN = "#4"
    EYE_COLUMN = "#5"
    PLACEHOLDER = "Search names, user names, notes and settings"
    HINT = ("Double-click or press Enter to edit. Show reveals a password until you hide it, and every Show is "
            "written to the audit log.")

    def __init__(self, root, actions: WindowActions, prefs: window_layout.ListPrefs) -> None:
        import tkinter as tk
        from tkinter import ttk

        self._root = root
        self._actions = actions
        self._prefs = prefs
        self._rows: List[Row] = []
        self._revealed: Dict[str, str] = {}
        self.form: Optional[EntryForm] = None
        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)
        frame = ttk.Frame(root, padding=(20, 16, 20, 10))
        frame.grid(row=0, column=0, sticky="nsew")
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(2, weight=1)

        header = ttk.Frame(frame)
        header.grid(row=0, column=0, sticky="we")
        header.columnconfigure(0, weight=1)
        ttk.Label(header, text="Passwords and settings", style="Title.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(header, text=f"Stored on {platform.node()}, for you and the agents you allow.",
                  style="Muted.TLabel").grid(row=1, column=0, sticky="w", pady=(2, 0))
        ttk.Button(header, text="+ Add", style="Accent.TButton", command=self._add).grid(
            row=0, column=1, rowspan=2, sticky="e")

        bar = ttk.Frame(frame)
        bar.grid(row=1, column=0, sticky="we", pady=(16, 10))
        bar.columnconfigure(0, weight=1)
        self._query = tk.StringVar(value="")
        self._search = ttk.Entry(bar, textvariable=self._query)
        self._search.grid(row=0, column=0, sticky="we")
        self._placeholder_on = False
        self._query.trace_add("write", lambda *_: self._render())
        self._show_placeholder()
        self._search.bind("<FocusIn>", lambda _e: self._hide_placeholder())
        self._search.bind("<FocusOut>", lambda _e: self._show_placeholder())
        self._search.bind("<Escape>", self._clear_search)
        self._search.bind("<Down>", self._into_list)
        self._search.bind("<Return>", self._into_list)

        self._show = tk.StringVar(value=prefs.show)
        choices = ttk.Frame(bar)
        choices.grid(row=0, column=1, padx=(12, 0))
        for column, (value, text) in enumerate(((SHOW_ALL, "All"), (SHOW_PASSWORDS, "Passwords"),
                                                (SHOW_SETTINGS, "Settings"))):
            ttk.Radiobutton(choices, text=text, value=value, variable=self._show, style="Choice.Toolbutton",
                            command=self._render).grid(row=0, column=column, padx=(0 if column == 0 else 2, 0))

        actions_bar = ttk.Frame(bar)
        actions_bar.grid(row=0, column=2, padx=(12, 0))
        self._edit_button = ttk.Button(actions_bar, text="Edit", command=self._edit)
        self._edit_button.grid(row=0, column=0)
        self._eye_button = ttk.Button(actions_bar, text="Show", width=6, command=self._toggle_selected)
        self._eye_button.grid(row=0, column=1, padx=(6, 0))
        self._delete_button = ttk.Button(actions_bar, text="Delete", style="Danger.TButton", command=self._delete)
        self._delete_button.grid(row=0, column=2, padx=(6, 0))

        card = tk.Frame(frame, background=window_style.SURFACE, highlightthickness=1,
                        highlightbackground=window_style.BORDER, highlightcolor=window_style.BORDER)
        card.grid(row=2, column=0, sticky="nsew")
        card.columnconfigure(0, weight=1)
        card.rowconfigure(0, weight=1)
        self._tree = ttk.Treeview(card, columns=[c[0] for c in self.COLUMNS], show="headings", selectmode="browse")
        for key, title, width, stretch in self.COLUMNS:
            self._tree.heading(key, text=title, anchor="w",
                               command=(lambda k=key: self._sort_by(k)) if key != "eye" else "")
            self._tree.column(key, width=width, minwidth=50, anchor="w", stretch=stretch)
        self._tree.tag_configure("stripe", background=window_style.STRIPE)
        self._tree.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(card, orient="vertical", command=self._tree.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self._tree.configure(yscrollcommand=scroll.set)
        self._empty = ttk.Label(card, text="", style="Muted.TLabel", background=window_style.SURFACE)

        self._tree.bind("<ButtonRelease-1>", self._clicked)
        self._tree.bind("<Double-1>", self._double_clicked)
        self._tree.bind("<<TreeviewSelect>>", lambda _e: self._selection_changed())
        self._tree.bind("<Return>", lambda _e: self._edit())
        self._tree.bind("<Delete>", lambda _e: self._delete())
        self._tree.bind("<space>", lambda _e: self._toggle_selected())
        self._tree.bind("<Escape>", lambda _e: self._search.focus_set())
        right_click = ("<Button-3>",)
        if sys.platform == "darwin":
            # Tk before 8.7 numbered a Mac's right button 2; Tk 8.7 and 9 number it 3 like everywhere else.
            right_click = ("<Button-3>" if tk.TkVersion >= 8.7 else "<Button-2>", "<Control-Button-1>")
        for sequence in right_click:
            self._tree.bind(sequence, self._context_menu)
        self._menu = tk.Menu(root, tearoff=0)

        status = ttk.Frame(frame)
        status.grid(row=3, column=0, sticky="we", pady=(8, 0))
        status.columnconfigure(0, weight=1)
        self._status = tk.StringVar(value=self.HINT)
        self._status_label = ttk.Label(status, textvariable=self._status, style="Faint.TLabel")
        self._status_label.grid(row=0, column=0, sticky="w")
        self._counts = tk.StringVar(value="")
        ttk.Label(status, textvariable=self._counts, style="Faint.TLabel").grid(row=0, column=1, sticky="e")

        command_key = "Command" if sys.platform == "darwin" else "Control"
        root.bind(f"<{command_key}-f>", lambda _e: self._search.focus_set())
        root.bind(f"<{command_key}-n>", lambda _e: self._add())
        self.reload()
        self._tree.focus_set()

    # --- Data ---------------------------------------------------------------------------------------------------

    def reload(self, select: Optional[str] = None) -> None:
        try:
            self._rows = self._actions.rows()
        except Exception as exc:  # a window event: say why rather than leave an empty list that looks true
            self._rows = []
            self._render()
            self._fail("read the store", exc)
            return
        self._render(select)

    def _query_text(self) -> str:
        return "" if self._placeholder_on else self._query.get()

    def _shown_rows(self) -> List[Row]:
        rows = filter_rows(self._rows, self._query_text(), self._show.get())
        return sort_rows(rows, self._prefs.sort_column, self._prefs.sort_descending)

    def _render(self, select: Optional[str] = None) -> None:
        if not hasattr(self, "_tree"):
            return  # the search box's placeholder is set before the table exists
        selected = select or self._selected_name()
        shown = self._shown_rows()
        self._tree.delete(*self._tree.get_children())
        for index, row in enumerate(shown):
            if row.is_setting:
                value, eye = row.setting_value, ""
            elif row.name in self._revealed:
                value, eye = self._revealed[row.name], "Hide"
            else:
                value, eye = MASK, "Show"
            self._tree.insert("", "end", iid=row.name, tags=("stripe",) if index % 2 else (),
                              values=(row.name, row.kind_label, row.username, value, eye, row.access, row.last_used))
        if selected and self._tree.exists(selected):
            self._tree.selection_set(selected)
            self._tree.focus(selected)
            self._tree.see(selected)
        self._headings()
        self._empty_state(shown)
        passwords = sum(1 for r in self._rows if not r.is_setting)
        total = len(self._rows)
        summary = f"{total} entries: {passwords} passwords, {total - passwords} settings"
        self._counts.set(summary if len(shown) == total else f"{len(shown)} shown of {summary}")
        self._selection_changed()

    def _headings(self) -> None:
        for key, title, _width, _stretch in self.COLUMNS:
            if key == self._prefs.sort_column:
                title = f"{title}  {'v' if self._prefs.sort_descending else '^'}"
            self._tree.heading(key, text=title)

    def _empty_state(self, shown: List[Row]) -> None:
        if shown:
            self._empty.place_forget()
            return
        if not self._rows:
            text = "Nothing stored yet. Press + Add to store a password or a setting."
        elif self._query_text().strip():
            text = f'Nothing matches "{self._query_text().strip()}".'
        else:
            text = "Nothing of this kind is stored."
        self._empty.configure(text=text)
        self._empty.place(relx=0.5, rely=0.4, anchor="center")

    def _sort_by(self, column: str) -> None:
        if self._prefs.sort_column == column:
            self._prefs.sort_descending = not self._prefs.sort_descending
        else:
            self._prefs.sort_column, self._prefs.sort_descending = column, column == "used"
        self._render()

    # --- Selection, search and keys -----------------------------------------------------------------------------

    def _selected_name(self) -> Optional[str]:
        selection = self._tree.selection()
        return selection[0] if selection else None

    def _row(self, name: Optional[str]) -> Optional[Row]:
        return next((r for r in self._rows if r.name == name), None)

    def _selection_changed(self) -> None:
        row = self._row(self._selected_name())
        for button in (self._edit_button, self._delete_button):
            button.state(["!disabled"] if row else ["disabled"])
        if row is None or row.is_setting:
            self._eye_button.configure(text="Show")
            self._eye_button.state(["disabled"])
        else:
            self._eye_button.configure(text="Hide" if row.name in self._revealed else "Show")
            self._eye_button.state(["!disabled"])

    def _show_placeholder(self) -> None:
        if self._query.get() or self._root.focus_get() == self._search:
            return
        self._placeholder_on = True
        self._search.configure(style="Placeholder.TEntry")
        self._query.set(self.PLACEHOLDER)

    def _hide_placeholder(self) -> None:
        if self._placeholder_on:
            self._placeholder_on = False
            self._search.configure(style="TEntry")
            self._query.set("")

    def _clear_search(self, _event=None) -> str:
        self._query.set("")
        return "break"

    def _into_list(self, _event=None) -> str:
        children = self._tree.get_children()
        if children:
            target = self._selected_name() if self._selected_name() in children else children[0]
            self._tree.selection_set(target)
            self._tree.focus(target)
            self._tree.see(target)
            self._tree.focus_set()
        return "break"

    def _clicked(self, event) -> None:
        if self._tree.identify_region(event.x, event.y) != "cell":
            return
        if self._tree.identify_column(event.x) in (self.EYE_COLUMN, self.VALUE_COLUMN):
            self._toggle(self._tree.identify_row(event.y))

    def _double_clicked(self, event) -> None:
        if self._tree.identify_region(event.x, event.y) == "cell" and \
                self._tree.identify_column(event.x) not in (self.EYE_COLUMN, self.VALUE_COLUMN):
            self._edit()

    def _context_menu(self, event) -> None:
        name = self._tree.identify_row(event.y)
        row = self._row(name)
        if row is None:
            return
        self._tree.selection_set(name)
        self._tree.focus(name)
        self._menu.delete(0, "end")
        self._menu.add_command(label="Edit", command=self._edit)
        if not row.is_setting:
            self._menu.add_command(label="Hide" if name in self._revealed else "Show", command=self._toggle_selected)
        self._menu.add_separator()
        self._menu.add_command(label="Delete", foreground=window_style.RED, command=self._delete)
        self._menu.tk_popup(event.x_root, event.y_root)
        self._menu.grab_release()

    # --- Actions ------------------------------------------------------------------------------------------------

    def _toggle_selected(self) -> None:
        self._toggle(self._selected_name())

    def _toggle(self, name: Optional[str]) -> None:
        row = self._row(name)
        if row is None or row.is_setting:
            return
        if row.name in self._revealed:
            del self._revealed[row.name]
        else:
            try:
                self._revealed[row.name] = self._actions.reveal(row.name)
            except Exception as exc:  # the Show click handler
                self._fail(f"show '{row.name}'", exc)
                return
        self._render(row.name)

    def _open_form(self, request: FormRequest, edit_name: str = "") -> None:
        import tkinter as tk

        win = tk.Toplevel(self._root)
        win.withdraw()
        win.title("Add an entry" if request.mode == MODE_ADD else f"Edit - {edit_name}")
        win.resizable(False, False)
        win.transient(self._root)

        def close(saved: bool) -> None:
            win.grab_release()
            win.destroy()
            self._tree.focus_set()
            if saved:
                name = edit_name or self._actions.saved_name or ""
                # A revealed value may now be the old one: hide it again rather than show it stale.
                self._revealed.pop(name, None)
                self.reload(select=name)
                self._status.set(f"Saved {name}.")

        reveal = (lambda: self._actions.reveal(edit_name)) if request.mode == MODE_EDIT else None
        form = EntryForm(win, request, lambda typed: self._actions.submit(typed, request.mode, edit_name), close,
                         reveal)
        self.form = form  # the open form, for the tests that drive this window
        win.update_idletasks()
        # The size the window takes on screen includes its title bar, which the list's own shows.
        title_bar = max(0, self._root.winfo_rooty() - self._root.winfo_y())
        x, y = window_layout.centre_over(_client_rect(self._root), win.winfo_reqwidth(),
                                         win.winfo_reqheight() + title_bar, monitors(self._root))
        win.geometry(f"+{x}+{y}")
        win.deiconify()
        win.lift()
        win.grab_set()
        form.focus_first()

    def _add(self) -> None:
        self._open_form(FormRequest(mode=MODE_ADD))

    def _edit(self) -> None:
        name = self._selected_name()
        if name is None:
            return
        try:
            request = self._actions.edit_request(name)
        except Exception as exc:  # a button handler
            self._fail(f"open '{name}'", exc)
            return
        self._open_form(request, name)

    def _delete(self) -> None:
        from tkinter import messagebox

        name = self._selected_name()
        if name is None:
            return
        if not messagebox.askyesno("Delete entry", f"Delete {name}?",
                                   detail="It is removed from this machine and cannot be undone. Anything that "
                                          "uses it will stop working.",
                                   icon="warning", default="no", parent=self._root):
            return
        try:
            self._actions.delete(name)
        except Exception as exc:  # a button handler
            self._fail(f"delete '{name}'", exc)
            return
        self._revealed.pop(name, None)
        self.reload()
        self._status.set(f"Deleted {name}.")

    def _fail(self, what: str, exc: BaseException) -> None:
        from tkinter import messagebox

        log_failure(what, exc)
        messagebox.showerror("cc-secrets", f"Could not {what}.", detail=describe(exc), parent=self._root)

    def remember(self) -> window_layout.ListPrefs:
        """Where the window is and how it is sorted and filtered, for the next opening."""
        self._prefs.x, self._prefs.y = self._root.winfo_x(), self._root.winfo_y()
        self._prefs.width, self._prefs.height = self._root.winfo_width(), self._root.winfo_height()
        self._prefs.show = self._show.get()
        return self._prefs


def _prefs_path() -> Path:
    """Beside the store, in the private folder - made now if nothing has been stored yet."""
    return paths.ensure_home() / PREFS_FILE


def show_list(actions: WindowActions) -> None:
    """The list window, until the owner closes it. It opens where it was last closed, or on the monitor under the
    mouse the first time."""
    prefs_path = _prefs_path()
    prefs = window_layout.load_prefs(prefs_path)
    root = open_root(f"cc-secrets - passwords and settings ({platform.node()})")
    root.minsize(LIST_MIN_WIDTH, LIST_MIN_HEIGHT)
    window = ListWindow(root, actions, prefs)
    x, y, width, height = window_layout.list_geometry(prefs, monitors(root), root.winfo_pointerxy())
    root.geometry(f"{width}x{height}+{x}+{y}")

    def close() -> None:
        try:
            window_layout.save_prefs(prefs_path, window.remember())
        except Exception as exc:  # closing must not fail because the window's place could not be remembered
            log_failure("remember the window", exc)
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", close)
    bring_to_front(root)
    root.update()
    title_bar = max(0, root.winfo_rooty() - root.winfo_y())
    monitor = window_layout.monitor_at(x + width // 2, y + title_bar, monitors(root))
    fitted = window_layout.fit_height(y, title_bar, height, monitor, LIST_MIN_HEIGHT)
    if fitted != height:
        root.geometry(f"{width}x{fitted}")
    root.mainloop()
