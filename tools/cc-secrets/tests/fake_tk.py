"""A stand-in for the part of tkinter and ttk that src/transfer_window.py draws with, so its dialogs, its approval
pop-up and its worker run with no window system, no clock and no sleep (issue #4442).

What it keeps from the real toolkit is what the product relies on: a variable holds what it was set to, a button or
a box that is disabled does nothing when invoked, a destroyed window no longer exists, and an option the real widget
does not have is refused (as Tk refuses it) rather than quietly kept. Time does not pass on its own: every call
handed to `after` waits for the test to `tick`, and one tick runs every call that was waiting, whatever its delay.

Worker threads stay real threads. `Threads` records each one the worker starts so a test can join it, which waits
on the thread itself rather than on a timer.
"""

from __future__ import annotations

import threading
import types
from typing import Callable, List, Tuple


class TclError(Exception):
    """What the real toolkit raises for an option a widget does not have."""


_LAYOUT = {"padding", "style", "width", "wraplength"}


class _Variable:
    def __init__(self, master=None, value=None) -> None:
        self._value = value

    def get(self):
        return self._value

    def set(self, value) -> None:
        self._value = value


class StringVar(_Variable):
    def __init__(self, master=None, value: str = "") -> None:
        super().__init__(master, value)


class BooleanVar(_Variable):
    def __init__(self, master=None, value: bool = False) -> None:
        super().__init__(master, bool(value))

    def set(self, value) -> None:
        super().set(bool(value))


class _Widget:
    OPTIONS: set = set()

    def __init__(self, master=None, **options) -> None:
        self.master = master
        self._options = {}
        self.gridded = False
        self.bindings = {}
        self.configure(**options)

    def configure(self, **options) -> None:
        unknown = set(options) - self.OPTIONS
        if unknown:
            raise TclError(f'unknown option "-{sorted(unknown)[0]}" for {type(self).__name__}')
        self._options.update(options)

    config = configure

    def cget(self, name: str):
        if name not in self.OPTIONS:
            raise TclError(f'unknown option "-{name}" for {type(self).__name__}')
        value = self._options.get(name, "")
        return value.get() if isinstance(value, _Variable) else value

    def grid(self, **_where) -> None:
        self.gridded = True

    def grid_remove(self) -> None:
        self.gridded = False

    def columnconfigure(self, index, **_how) -> None:
        pass

    def bind(self, sequence: str, handler: Callable) -> None:
        self.bindings[sequence] = handler

    def _enabled(self) -> bool:
        return str(self._options.get("state", "normal")) != "disabled"


class Frame(_Widget):
    OPTIONS = {"padding", "style"}


class Label(_Widget):
    OPTIONS = {"text", "textvariable"} | _LAYOUT


class Entry(_Widget):
    OPTIONS = {"textvariable", "state"} | _LAYOUT


class Combobox(_Widget):
    OPTIONS = {"textvariable", "state", "values"} | _LAYOUT


class Button(_Widget):
    OPTIONS = {"text", "command", "state"} | _LAYOUT

    def invoke(self):
        """As the real button: a disabled one does nothing; an enabled one runs its command."""
        if self._enabled() and self._options.get("command") is not None:
            return self._options["command"]()
        return None


class Checkbutton(_Widget):
    OPTIONS = {"text", "variable", "command", "state"} | _LAYOUT

    def invoke(self):
        """As the real box: a disabled one does nothing; an enabled one flips its variable, then runs its command."""
        if not self._enabled():
            return None
        variable = self._options.get("variable")
        if variable is not None:
            variable.set(not variable.get())
        command = self._options.get("command")
        return command() if command is not None else None


class Root:
    """The application window: holds the calls handed to `after` until the test ticks."""

    def __init__(self) -> None:
        self._waiting: List[Tuple[int, Callable[[], None]]] = []
        self._destroyed = False

    def after(self, ms: int, call: Callable[[], None]) -> str:
        self._waiting.append((ms, call))
        return f"after#{len(self._waiting)}"

    def tick(self) -> None:
        """Run every call that was waiting when the tick began; a call it schedules waits for the next tick."""
        waiting, self._waiting = self._waiting, []
        for _ms, call in waiting:
            call()

    def winfo_exists(self) -> bool:
        return not self._destroyed

    def destroy(self) -> None:
        self._destroyed = True


class Toplevel:
    """A dialog or pop-up window: its title, and whether it still exists."""

    def __init__(self, master=None) -> None:
        self.master = master
        self._title = ""
        self._destroyed = False
        self.bindings = {}
        self.protocols = {}
        self.attributes_set = {}

    def title(self, text=None):
        if text is None:
            return self._title
        self._title = text
        return None

    def resizable(self, width, height) -> None:
        pass

    def transient(self, master) -> None:
        pass

    def attributes(self, name: str, value) -> None:
        self.attributes_set[name] = value

    def bind(self, sequence: str, handler: Callable) -> None:
        self.bindings[sequence] = handler

    def protocol(self, name: str, handler: Callable) -> None:
        self.protocols[name] = handler

    def winfo_exists(self) -> bool:
        return not self._destroyed

    def destroy(self) -> None:
        self._destroyed = True


def modules() -> Tuple[types.ModuleType, types.ModuleType]:
    """A `tkinter` module and its `ttk` submodule, to put in sys.modules in place of the real ones."""
    ttk = types.ModuleType("tkinter.ttk")
    for widget in (Frame, Label, Entry, Combobox, Button, Checkbutton):
        setattr(ttk, widget.__name__, widget)
    tk = types.ModuleType("tkinter")
    tk.Toplevel, tk.StringVar, tk.BooleanVar, tk.TclError, tk.ttk = Toplevel, StringVar, BooleanVar, TclError, ttk
    return tk, ttk


class Threads:
    """Stands in for the `threading` module in src/transfer_window: each thread is real, and remembered to be joined."""

    def __init__(self) -> None:
        self.started: List[threading.Thread] = []

    def Thread(self, *args, **kwargs) -> threading.Thread:  # noqa: N802 - the name the product calls
        thread = threading.Thread(*args, **kwargs)
        self.started.append(thread)
        return thread

    def join_all(self) -> None:
        """Wait for every thread started so far to finish. There is no timeout: the work each test hands the worker
        returns at once, so a thread that never ends is a defect in the code under test."""
        while self.started:
            self.started.pop(0).join()
