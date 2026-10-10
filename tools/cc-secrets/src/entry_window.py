"""The cc-secrets windows, drawn with tkinter: the list (`cc-secrets ui`), the add / edit form opened from it, and
the pop-up an agent opens with `cc-secrets ask`. The two forms are one form in different modes.

This module only draws. What a button does - save, reveal, delete, the rows of the list - is in window_actions,
which is tested without a screen. Nothing here stores, logs or prints a secret; the secret box always starts empty,
because no request carries a secret.

ASCII only: the eye is a button labelled Show / Hide, and a secret in the list is a row of asterisks with [show].
"""

from __future__ import annotations

import os
import platform
import sys
from typing import Callable, Dict, List, Optional

from .errors import CcSecretsError, describe
from .store import USES
from .window_actions import (MODE_ADD, MODE_ASK, MODE_EDIT, FormInput, FormRequest, Row, WindowActions,
                             filter_rows, log_failure, replace_warning)


class NoDisplayError(CcSecretsError):
    """There is no screen to show the window on."""


NO_DISPLAY_MESSAGE = ("cc-secrets needs a screen to show its window on, and this process has none ({why}). "
                      "Run it on the owner's desktop (a session on that machine, not an SSH shell), or have the "
                      "owner run 'cc-secrets add NAME' in a terminal there.")

MASK = "**********"
AMBER = "#9a5b00"
RED = "#b00020"
GREY = "#555555"
WRAP = 460


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
    """A new top-level window on the owner's screen, or NoDisplayError saying why there is none."""
    problem = display_problem()
    if problem is not None:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=problem))
    import tkinter as tk

    try:
        root = tk.Tk()
    except tk.TclError as exc:
        raise NoDisplayError(NO_DISPLAY_MESSAGE.format(why=f"the window system refused: {exc}")) from exc
    root.title(title)
    return root


def bring_to_front(win) -> None:
    """Centre the window and put it in front: the owner is usually looking at something else when it opens."""
    win.update_idletasks()
    x = (win.winfo_screenwidth() - win.winfo_reqwidth()) // 2
    y = (win.winfo_screenheight() - win.winfo_reqheight()) // 3
    win.geometry(f"+{max(x, 0)}+{max(y, 0)}")
    win.lift()
    win.attributes("-topmost", True)
    win.after(500, lambda: win.attributes("-topmost", False))
    win.focus_force()


class EntryForm:
    """The add / edit / ask form, built inside `win`. `on_save` returns None when saved, else a message to show.
    `on_reveal_current` (edit only) returns the stored secret for the eye. `on_close(saved)` closes the window."""

    def __init__(self, win, request: FormRequest, on_save: Callable[[FormInput], Optional[str]],
                 on_close: Callable[[bool], None], on_reveal_current: Optional[Callable[[], str]] = None) -> None:
        import tkinter as tk
        from tkinter import ttk

        self._on_save = on_save
        self._on_close = on_close
        self._on_reveal_current = on_reveal_current
        self._shown = False
        frame = ttk.Frame(win, padding=16)
        frame.grid(row=0, column=0, sticky="nsew")
        row = 0

        def line(text: str, **style) -> None:
            nonlocal row
            ttk.Label(frame, text=text, wraplength=WRAP, justify="left", **style).grid(
                row=row, column=0, columnspan=3, sticky="w", pady=(0, 8))
            row += 1

        if request.mode == MODE_ASK:
            line(f"{request.asked_by} asks:", font=("TkDefaultFont", 10, "bold"))
            if request.reason:
                line(f'"{request.reason}"')
            if request.exists:
                line(replace_warning(request.name), foreground=AMBER)

        def caption(text: str):
            label = ttk.Label(frame, text=text)
            label.grid(row=row, column=0, sticky="w", padx=(0, 10), pady=3)
            return label

        def text_box(value: str):
            nonlocal row
            var = tk.StringVar(value=value)
            box = ttk.Entry(frame, textvariable=var, width=46)
            box.grid(row=row, column=1, columnspan=2, sticky="we", pady=3)
            row += 1
            return var, box

        def hint(text: str) -> None:
            nonlocal row
            ttk.Label(frame, text=text, foreground=GREY).grid(row=row, column=1, columnspan=2, sticky="w")
            row += 1

        caption("Name")
        self._name, name_box = text_box(request.name)
        if request.mode == MODE_EDIT:
            name_box.state(["disabled"])

        caption("Kind")
        self._setting = tk.BooleanVar(value=request.kind_setting)
        kinds = ttk.Frame(frame)
        kinds.grid(row=row, column=1, columnspan=2, sticky="w", pady=3)
        ttk.Radiobutton(kinds, text="Secret - hidden", variable=self._setting, value=False,
                        command=self._kind_changed).grid(row=0, column=0, padx=(0, 12))
        ttk.Radiobutton(kinds, text="Setting - not secret", variable=self._setting, value=True,
                        command=self._kind_changed).grid(row=0, column=1)
        row += 1

        caption("User name")
        self._username, user_box = text_box(request.username)

        self._secret_caption = caption("Secret")
        self._secret = tk.StringVar(value="")
        self._secret_box = ttk.Entry(frame, textvariable=self._secret, width=38, show="*")
        self._secret_box.grid(row=row, column=1, sticky="we", pady=3)
        self._eye = ttk.Button(frame, text="Show", width=6, command=self._toggle_eye)
        self._eye.grid(row=row, column=2, sticky="e", padx=(6, 0), pady=3)
        row += 1
        if request.mode == MODE_EDIT:
            hint("Leave it empty to keep the current one.")

        caption("Notes")
        self._notes, notes_box = text_box(request.notes)

        caption("Agents may use")
        self._agents = tk.BooleanVar(value=request.agents_may_use)
        self._uses = {use: tk.BooleanVar(value=use in request.uses) for use in USES}
        access = ttk.Frame(frame)
        access.grid(row=row, column=1, columnspan=2, sticky="w", pady=3)
        ttk.Checkbutton(access, text="yes", variable=self._agents).grid(row=0, column=0, padx=(0, 16))
        ttk.Label(access, text="Uses:").grid(row=0, column=1, padx=(0, 4))
        ttk.Checkbutton(access, text="run", variable=self._uses["run"]).grid(row=0, column=2, padx=(0, 8))
        ttk.Checkbutton(access, text="login (browser)", variable=self._uses["login"]).grid(row=0, column=3)
        row += 1

        caption("Login sites")
        self._domains, domains_box = text_box(request.domains)
        hint("Only for browser logins, for example https://example.com")

        self._message = tk.StringVar(value="")
        self._message_label = ttk.Label(frame, textvariable=self._message, wraplength=WRAP, justify="left")
        self._message_label.grid(row=row, column=0, columnspan=3, sticky="w", pady=(8, 8))
        row += 1

        buttons = ttk.Frame(frame)
        buttons.grid(row=row, column=0, columnspan=3, sticky="e")
        cancel_button = ttk.Button(buttons, text="Cancel", command=self.cancel)
        cancel_button.grid(row=0, column=0, padx=(0, 8))
        save_button = ttk.Button(buttons, text="Save", command=self.save)
        save_button.grid(row=0, column=1)

        # Return saves from a text box, and presses whichever button has the focus - never Save from Cancel.
        for box in (name_box, user_box, self._secret_box, notes_box, domains_box):
            box.bind("<Return>", self.save)
        save_button.bind("<Return>", self.save)
        cancel_button.bind("<Return>", self.cancel)
        win.bind("<Escape>", self.cancel)
        win.protocol("WM_DELETE_WINDOW", self.cancel)
        self._kind_changed()
        (self._secret_box if request.name else name_box).focus_set()

    def _kind_changed(self) -> None:
        setting = self._setting.get()
        self._secret_caption.configure(text="Value" if setting else "Secret")
        if setting:
            self._secret_box.configure(show="")
            self._eye.grid_remove()
        else:
            self._secret_box.configure(show="" if self._shown else "*")
            self._eye.grid()

    def _toggle_eye(self) -> None:
        if self._shown:
            self._shown = False
            self._secret_box.configure(show="*")
            self._eye.configure(text="Show")
            return
        if not self._secret.get() and self._on_reveal_current is not None:
            try:
                self._secret.set(self._on_reveal_current())
            except Exception as exc:  # the eye's click handler: say why in the form
                log_failure("reveal", exc)
                self._say(f"Could not show it: {describe(exc)}")
                return
        self._shown = True
        self._secret_box.configure(show="")
        self._eye.configure(text="Hide")

    def _say(self, text: str) -> None:
        self._message_label.configure(foreground=AMBER if text.startswith("An entry called") else RED)
        self._message.set(text)

    def save(self, _event=None) -> None:
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

    def cancel(self, _event=None) -> None:
        self._secret.set("")
        self._on_close(False)


def show_ask(request: FormRequest, actions: WindowActions) -> bool:
    """The agent's pop-up, in its own window, until the owner saves or cancels. True when saved."""
    root = open_root("cc-secrets - a session is asking for a password")
    root.resizable(False, False)
    outcome = {"saved": False}

    def close(saved: bool) -> None:
        outcome["saved"] = saved
        root.destroy()

    EntryForm(root, request, lambda typed: actions.submit(typed, MODE_ASK), close)
    bring_to_front(root)
    root.mainloop()
    return outcome["saved"]


class ListWindow:
    """Every entry, searchable. Settings show in full; a secret shows as asterisks until its eye is clicked, and
    stays shown until it is clicked again (owner ruling 2026-10-09). No Copy button (owner ruling)."""

    COLUMNS = (("name", "Name", 220), ("kind", "Kind", 70), ("username", "User name", 170),
               ("secret", "Secret", 240), ("agents", "Agents", 60), ("used", "Last used", 100))
    SECRET_COLUMN = "#4"

    def __init__(self, root, actions: WindowActions) -> None:
        import tkinter as tk
        from tkinter import ttk

        self._root = root
        self._actions = actions
        self._rows: List[Row] = []
        self._revealed: Dict[str, str] = {}
        frame = ttk.Frame(root, padding=12)
        frame.grid(row=0, column=0, sticky="nsew")
        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)
        frame.columnconfigure(1, weight=1)
        frame.rowconfigure(1, weight=1)

        ttk.Label(frame, text="Search").grid(row=0, column=0, sticky="w", padx=(0, 6))
        self._query = tk.StringVar(value="")
        self._query.trace_add("write", lambda *_: self._render())
        search = ttk.Entry(frame, textvariable=self._query)
        search.grid(row=0, column=1, sticky="we")
        bar = ttk.Frame(frame)
        bar.grid(row=0, column=2, sticky="e", padx=(8, 0))
        for column, (text, command) in enumerate((("+ Add", self._add), ("Edit", self._edit),
                                                  ("Show / hide", self._toggle_selected), ("Delete", self._delete))):
            ttk.Button(bar, text=text, command=command).grid(row=0, column=column, padx=(4, 0))

        self._tree = ttk.Treeview(frame, columns=[c[0] for c in self.COLUMNS], show="headings", height=16,
                                  selectmode="browse")
        for key, title, width in self.COLUMNS:
            self._tree.heading(key, text=title)
            self._tree.column(key, width=width, anchor="w", stretch=key in ("name", "secret"))
        self._tree.grid(row=1, column=0, columnspan=3, sticky="nsew", pady=(10, 6))
        scroll = ttk.Scrollbar(frame, orient="vertical", command=self._tree.yview)
        scroll.grid(row=1, column=3, sticky="ns", pady=(10, 6))
        self._tree.configure(yscrollcommand=scroll.set)
        self._tree.bind("<ButtonRelease-1>", self._clicked)
        self._tree.bind("<Double-1>", self._double_clicked)

        self._footer = tk.StringVar(value="")
        ttk.Label(frame, textvariable=self._footer, wraplength=860, justify="left", foreground=GREY).grid(
            row=2, column=0, columnspan=3, sticky="w")
        search.focus_set()
        self.reload()

    def reload(self) -> None:
        try:
            self._rows = self._actions.rows()
        except Exception as exc:  # a window event: say why rather than leave an empty list that looks true
            self._rows = []
            self._render()
            self._fail("read the store", exc)
            return
        self._render()

    def _render(self) -> None:
        selected = self._selected_name()
        shown = filter_rows(self._rows, self._query.get())
        self._tree.delete(*self._tree.get_children())
        for row in shown:
            if row.is_setting:
                secret_cell = row.setting_value
            elif row.name in self._revealed:
                secret_cell = f"{self._revealed[row.name]}   [hide]"
            else:
                secret_cell = f"{MASK}   [show]"
            self._tree.insert("", "end", iid=row.name, values=(row.name, row.kind, row.username, secret_cell,
                                                                 row.agents, row.last_used))
        if selected and self._tree.exists(selected):
            self._tree.selection_set(selected)
        total = len(self._rows)
        count = f"{len(shown)} of {total} entries" if len(shown) != total else f"{total} entries"
        self._footer.set(f"{count}. Settings (hosts, email addresses) are not secret and show in full. Click [show] "
                         f"to see a secret; it stays shown until you click [hide]. Every reveal is written to the "
                         f"audit log (cc-secrets log) - never the secret itself.")

    def _selected_name(self) -> Optional[str]:
        selection = self._tree.selection()
        return selection[0] if selection else None

    def _row(self, name: Optional[str]) -> Optional[Row]:
        return next((r for r in self._rows if r.name == name), None)

    def _clicked(self, event) -> None:
        if self._tree.identify_region(event.x, event.y) != "cell":
            return
        if self._tree.identify_column(event.x) == self.SECRET_COLUMN:
            self._toggle(self._tree.identify_row(event.y))

    def _double_clicked(self, event) -> None:
        if self._tree.identify_region(event.x, event.y) == "cell" and \
                self._tree.identify_column(event.x) != self.SECRET_COLUMN:
            self._edit()

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
            except Exception as exc:  # the eye's click handler
                self._fail(f"show '{row.name}'", exc)
                return
        self._render()

    def _open_form(self, request: FormRequest, edit_name: str = "") -> None:
        import tkinter as tk

        win = tk.Toplevel(self._root)
        win.title("Add an entry" if request.mode == MODE_ADD else f"Edit entry - {edit_name}")
        win.resizable(False, False)
        win.transient(self._root)

        def close(saved: bool) -> None:
            win.grab_release()
            win.destroy()
            if saved:
                # A revealed value may now be the old one: hide it again rather than show it stale.
                self._revealed.pop(edit_name or self._actions.saved_name or "", None)
                self.reload()

        reveal = (lambda: self._actions.reveal(edit_name)) if request.mode == MODE_EDIT else None
        EntryForm(win, request, lambda typed: self._actions.submit(typed, request.mode, edit_name), close, reveal)
        bring_to_front(win)
        win.grab_set()

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
        if not messagebox.askyesno("Delete entry", f"Delete '{name}'? This cannot be undone.", parent=self._root):
            return
        try:
            self._actions.delete(name)
        except Exception as exc:  # a button handler
            self._fail(f"delete '{name}'", exc)
            return
        self._revealed.pop(name, None)
        self.reload()

    def _fail(self, what: str, exc: BaseException) -> None:
        from tkinter import messagebox

        log_failure(what, exc)
        messagebox.showerror("cc-secrets", f"Could not {what}: {describe(exc)}", parent=self._root)


def show_list(actions: WindowActions) -> None:
    """The list window, until the owner closes it."""
    root = open_root(f"cc-secrets - your passwords and settings ({platform.node()})")
    root.minsize(780, 400)
    ListWindow(root, actions)
    bring_to_front(root)
    root.mainloop()
