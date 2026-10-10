"""The cc-secrets list driven on a real screen: where the form opens, what it shows, how fast each click is, what
the list remembers when it closes, that nothing is cut off, and that the key the keyboard labels delete works.
Skipped where there is no screen (a continuous integration runner).

These are the owner's complaints of 2026-10-10, each checked the way he met it: Edit opened its form on the
wrong screen, the QA email's form was empty, and every click took about two seconds.

The last three were found running the window on macOS, where the system font is wider and the window system
moves a window up on its first showing to keep it clear of the Dock. All three are written to hold on every
system rather than only on a Mac: they measure what the window needs and where it actually ended up, instead
of a pixel count taken from one machine.
"""

import json
import os
import subprocess
import sys
import textwrap
import time
from pathlib import Path

import pytest

from conftest import new_secret
from src import entry_window, paths, window_layout
from src.audit import AuditLog, OwnerApproval
from src.store import KIND_SETTING, make_entry
from src.window_actions import WINDOW_RECORD, WindowActions


def _screen_problem():
    try:
        problem = entry_window.display_problem()
    except Exception as exc:  # noqa: BLE001 - any failure to ask means no usable screen here
        return f"{type(exc).__name__}"
    return problem


pytestmark = pytest.mark.skipif(_screen_problem() is not None, reason="needs a screen")

BUDGET_SECONDS = 0.5  # was about 2 s a click before the fix


@pytest.fixture(scope="module")
def tk_root():
    """One Tk for the module: Tk on Windows sometimes cannot start again after many in one process."""
    root = entry_window.open_root("cc-secrets screen test")
    yield root
    root.destroy()


@pytest.fixture
def window(store, tk_root):
    import tkinter as tk

    entries = [make_entry("mindzie-qa-email", "", "qa@mindzie.com", [], "n", True, ["run"], kind=KIND_SETTING),
               make_entry("devlinux", "soren", new_secret(), [], "", True, ["run"])]
    entries += [make_entry(f"filler-{index:03}", "", new_secret(), [], "", True, ["run"]) for index in range(150)]
    store.put_many(entries, replace=False)
    actions = WindowActions(store, AuditLog(paths.audit_path()), OwnerApproval(text=WINDOW_RECORD, session_name=""),
                            detail=WINDOW_RECORD)
    win = tk.Toplevel(tk_root)
    listing = entry_window.ListWindow(win, actions, window_layout.ListPrefs())
    monitor = entry_window.monitors(win)[0]
    win.geometry(f"1000x600+{monitor[0] + 40}+{monitor[1] + 40}")
    win.update()
    yield win, listing
    win.destroy()


def _forms(root):
    return [w for w in root.winfo_children() if w.winfo_class() == "Toplevel"]


def cut_off(window):
    """Every widget on screen that was given less room than it asked for."""
    found, stack = [], list(window.winfo_children())
    while stack:
        widget = stack.pop()
        stack.extend(widget.winfo_children())
        if not widget.winfo_ismapped():
            continue
        if widget.winfo_width() < widget.winfo_reqwidth() or widget.winfo_height() < widget.winfo_reqheight():
            found.append(f"{widget.winfo_class()} needs {widget.winfo_reqwidth()}x{widget.winfo_reqheight()}, "
                         f"got {widget.winfo_width()}x{widget.winfo_height()}")
    return found


def _open_the_list_and_report():
    """Run show_list the way the owner runs it - its own process - and report where the window ended up."""
    script = textwrap.dedent("""
        import json, sys, tkinter as tk
        sys.path.insert(0, sys.argv[1])
        from src import entry_window, paths, window_layout
        from src.audit import AuditLog, OwnerApproval
        from src.store import SecretStore
        from src.storefile import UserOnlyFile
        from src.window_actions import WINDOW_RECORD, WindowActions
        real = tk.Tk.mainloop
        def report_then_close(self, n=0):
            def act():
                self.update()
                title_bar = max(0, self.winfo_rooty() - self.winfo_y())
                cut_off, stack = [], list(self.winfo_children())
                while stack:
                    widget = stack.pop()
                    stack.extend(widget.winfo_children())
                    if widget.winfo_ismapped() and (widget.winfo_width() < widget.winfo_reqwidth()
                                                    or widget.winfo_height() < widget.winfo_reqheight()):
                        cut_off.append("%s needs %dx%d, got %dx%d" % (
                            widget.winfo_class(), widget.winfo_reqwidth(), widget.winfo_reqheight(),
                            widget.winfo_width(), widget.winfo_height()))
                print("PLACED " + json.dumps({
                    "x": self.winfo_x(), "y": self.winfo_y(), "width": self.winfo_width(),
                    "height": self.winfo_height(), "title_bar": title_bar,
                    "minimum": list(self.wm_minsize()),
                    "needed": [self.winfo_reqwidth(), self.winfo_reqheight()],
                    "cut_off": cut_off,
                    "monitor": list(window_layout.monitor_at(
                        self.winfo_x() + self.winfo_width() // 2, self.winfo_y() + title_bar,
                        entry_window.monitors(self))),
                }))
                self.tk.call(self.protocol("WM_DELETE_WINDOW"))
            self.after(300, act)
            real(self, n)
        tk.Tk.mainloop = report_then_close
        entry_window.show_list(WindowActions(SecretStore(UserOnlyFile(paths.store_path())),
                                             AuditLog(paths.audit_path()),
                                             OwnerApproval(text=WINDOW_RECORD, session_name=""),
                                             detail=WINDOW_RECORD))
    """)
    tool = Path(entry_window.__file__).resolve().parent.parent
    done = subprocess.run([sys.executable, "-c", script, str(tool)], capture_output=True, text=True, timeout=60,
                          env=dict(os.environ, CC_SECRETS_HOME=str(paths.secrets_home())))
    assert done.returncode == 0, done.stderr[-2000:]
    reported = [line for line in done.stdout.splitlines() if line.startswith("PLACED ")]
    assert len(reported) == 1, done.stdout
    placed = json.loads(reported[0][len("PLACED "):])
    placed["monitor"] = tuple(placed["monitor"])
    return placed


def _timed(root, action, undo=lambda: None):
    """The best of three tries: this machine runs other work too, and one slow try is the machine, not the window.
    Before the fix every try took about two seconds."""
    best = None
    for _ in range(3):
        started = time.perf_counter()
        action()
        root.update()
        elapsed = time.perf_counter() - started
        best = elapsed if best is None else min(best, elapsed)
        undo()
        root.update()
    return best


def test_Edit_OpensTheFormCentredOverTheList(window):
    root, listing = window
    listing._tree.selection_set("devlinux")

    listing._edit()
    root.update()

    form = _forms(root)[0]
    centre = (form.winfo_rootx() + form.winfo_width() // 2, form.winfo_rooty() + form.winfo_height() // 2)
    assert root.winfo_rootx() < centre[0] < root.winfo_rootx() + root.winfo_width()
    assert root.winfo_rooty() < centre[1] < root.winfo_rooty() + root.winfo_height()


def test_Edit_ASetting_ShowsItsValue(window):
    root, listing = window
    listing._tree.selection_set("mindzie-qa-email")

    listing._edit()
    root.update()

    assert listing.form._secret.get() == "qa@mindzie.com"


def test_Edit_APassword_StartsEmpty(window):
    root, listing = window
    listing._tree.selection_set("devlinux")

    listing._edit()
    root.update()

    assert listing.form._secret.get() == ""


def test_EveryClick_IsQuick_With150Entries(window):
    root, listing = window
    listing._tree.selection_set("devlinux")

    timings = {
        "reload": _timed(root, listing.reload),
        "show": _timed(root, lambda: listing._toggle("devlinux"), lambda: listing._toggle("devlinux")),
        "edit": _timed(root, listing._edit, lambda: [f.destroy() for f in _forms(root)]),
        "search": _timed(root, lambda: (listing._hide_placeholder(), listing._query.set("filler-1")),
                         lambda: listing._query.set("")),
    }

    assert all(seconds < BUDGET_SECONDS for seconds in timings.values()), timings


def test_SavingFromTheForm_UpdatesTheList_AndSaysSo(window, store):
    root, listing = window
    listing._tree.selection_set("mindzie-qa-email")
    listing._edit()
    root.update()

    listing.form._secret.set("qa2@mindzie.com")
    listing.form.save()
    root.update()

    assert _forms(root) == []
    assert store.get("mindzie-qa-email").secret.reveal() == "qa2@mindzie.com"
    assert listing._tree.selection() == ("mindzie-qa-email",)
    assert listing._status.get() == "Saved mindzie-qa-email."


def test_ClosingTheList_RemembersWhereItWas(store):
    """Run as the owner runs it - show_list in its own process - closed through the window's close button
    handler a moment after it opens. It is moved to a little over its own minimum, because the minimum is
    what the window needs to draw itself and that is more pixels in one system font than in another."""
    script = textwrap.dedent("""
        import json, sys, tkinter as tk
        sys.path.insert(0, sys.argv[1])
        from src import entry_window, paths
        from src.audit import AuditLog, OwnerApproval
        from src.store import SecretStore
        from src.storefile import UserOnlyFile
        from src.window_actions import WINDOW_RECORD, WindowActions
        real = tk.Tk.mainloop
        def close_soon(self, n=0):
            def move_then_close():
                width, height = (n + 120 for n in self.wm_minsize())
                self.geometry("%dx%d+150+120" % (width, height))
                self.update()
                print("MOVED " + json.dumps([150, 120, width, height]))
                self.tk.call(self.protocol("WM_DELETE_WINDOW"))
            self.after(300, move_then_close)
            real(self, n)
        tk.Tk.mainloop = close_soon
        entry_window.show_list(WindowActions(SecretStore(UserOnlyFile(paths.store_path())),
                                             AuditLog(paths.audit_path()),
                                             OwnerApproval(text=WINDOW_RECORD, session_name=""),
                                             detail=WINDOW_RECORD))
    """)
    tool = Path(entry_window.__file__).resolve().parent.parent
    done = subprocess.run([sys.executable, "-c", script, str(tool)], capture_output=True, text=True, timeout=60,
                          env=dict(os.environ, CC_SECRETS_HOME=str(paths.secrets_home())))

    assert done.returncode == 0, done.stderr[-2000:]
    moved = [line for line in done.stdout.splitlines() if line.startswith("MOVED ")]
    assert len(moved) == 1, done.stdout
    prefs = window_layout.load_prefs(paths.secrets_home() / entry_window.PREFS_FILE)
    assert [prefs.x, prefs.y, prefs.width, prefs.height] == json.loads(moved[0][len("MOVED "):])


def test_TheDeleteKeys_AreTheOnesThisKeyboardHas(window, monkeypatch):
    """A Mac's main delete key sends BackSpace; Delete is forward delete, which only a full-size keyboard with a
    numeric keypad has. Bound to Delete alone, the key the owner actually presses on a Mac did nothing.

    Both halves are asserted on every system, so this fails either way round: on a Mac if BackSpace stops
    asking, and anywhere else if BackSpace starts asking - there the main delete key IS Delete, and a stray
    BackSpace binding on the table would be a second, undocumented way to delete an entry.
    """
    from tkinter import messagebox

    root, listing = window
    asked = []
    monkeypatch.setattr(messagebox, "askyesno", lambda title, message, **kw: asked.append(message) or False)
    entry_window.bring_to_front(root)  # a key reaches a widget only while its window is up and has the focus

    def press(key):
        asked.clear()
        listing._tree.selection_set("devlinux")
        listing._tree.focus("devlinux")
        listing._tree.focus_set()
        root.update()
        listing._tree.event_generate(key, when="now")
        root.update()
        return list(asked)

    assert press("<Delete>") == ["Delete devlinux?"], "forward delete must always ask"
    assert press("<BackSpace>") == (["Delete devlinux?"] if sys.platform == "darwin" else [])
    assert any(row.name == "devlinux" for row in listing._actions.rows()), "answering no still deleted it"


def test_BackSpaceInTheSearchBox_EditsTheTextAndNeverAsksToDelete(window, monkeypatch):
    """The new binding is on the table alone. BackSpace is how anybody rubs out a character, so a window-wide
    binding would have turned a typing mistake in the search box into a prompt to delete an entry."""
    from tkinter import messagebox

    root, listing = window
    asked = []
    monkeypatch.setattr(messagebox, "askyesno", lambda title, message, **kw: asked.append(message) or False)
    entry_window.bring_to_front(root)
    listing._tree.selection_set("devlinux")
    listing._search.focus_set()
    listing._hide_placeholder()
    listing._query.set("dev")
    listing._search.icursor("end")
    root.update()

    listing._search.event_generate("<BackSpace>", when="now")
    root.update()

    assert listing._query.get() == "de"
    assert asked == []


def test_NothingIsCutOff_AtAnySizeTheListAllows(window):
    """The list may not be dragged smaller than it needs to draw itself. Its floor is a count of pixels, and the
    same words are wider in the macOS system font than in the Windows one: at the 760 by 420 floor on a Mac the
    search box was squeezed to about a hundred pixels and the hint under the table lost its end."""
    root, listing = window
    root.update_idletasks()
    smallest = window_layout.minimum_size((root.winfo_reqwidth(), root.winfo_reqheight()),
                                          (entry_window.LIST_MIN_WIDTH, entry_window.LIST_MIN_HEIGHT),
                                          entry_window.monitors(root)[0])
    root.minsize(*smallest)

    for width, height in (smallest, (smallest[0] + 200, smallest[1] + 200)):
        root.geometry(f"{width}x{height}")
        root.update()

        assert cut_off(root) == []

    root.geometry("400x200")
    root.update()
    assert (root.winfo_width(), root.winfo_height()) == smallest, "the window system let it go below the minimum"


def test_TheListLeftLowOnTheScreen_IsNotTrimmedWhileItStillFits(store, tk_root):
    """It is trimmed only by as much as the room at the place it ACTUALLY got, which is not always the place it
    asked for: macOS moves a window up on its first showing to keep it clear of the Dock. Measured from the
    asked-for place instead, the trim shortened a window that was already wholly visible - and the shorter
    height was saved, so a list left low on the screen came back smaller every time, down to its minimum.

    The remembered place is as low as the tool will still go back to - its title bar exactly MIN_VISIBLE_HEIGHT
    inside the bottom of the monitor, measured against the monitor rectangle the tool itself works from rather
    than the screen height, so the list really is reopened down there on every system instead of falling
    through to the mouse. Only a window system that MOVES the window can tell the two ways of measuring apart,
    which is macOS; everywhere else this still holds the invariant, and the test asserts the place it got.
    """
    prefs_path = paths.ensure_home() / entry_window.PREFS_FILE
    asked_height = 620
    screens = entry_window.monitors(tk_root)
    monitor = window_layout.monitor_at(100, 100, screens)
    low = monitor[3] - window_layout.MIN_VISIBLE_HEIGHT
    prefs = window_layout.ListPrefs(x=100, y=low, width=1060, height=asked_height)
    assert window_layout.still_visible(prefs.rect(), screens), "the tool would not go back to this place"
    window_layout.save_prefs(prefs_path, prefs)

    placed = _open_the_list_and_report()

    room = placed["monitor"][3] - placed["y"] - placed["title_bar"]
    allowed = min(asked_height, max(room, placed["minimum"][1]))
    assert placed["height"] == allowed, placed
    assert window_layout.load_prefs(prefs_path).height == placed["height"]


def test_TheListCannotBeOpenedSmallerThanItNeedsToDrawItself(store):
    """Opened at the size it was last left, 760 by 420 - the floor the tool used to allow. In the macOS system
    font the list needs 1013 by 514, so at the floor the search box was squeezed to about a hundred pixels and
    the hint under the table lost its end. It must come back at the size it needs, with nothing cut off."""
    prefs_path = paths.ensure_home() / entry_window.PREFS_FILE
    window_layout.save_prefs(prefs_path, window_layout.ListPrefs(x=120, y=140, width=760, height=420))

    placed = _open_the_list_and_report()

    assert placed["cut_off"] == [], placed
    assert placed["width"] >= placed["minimum"][0] and placed["height"] >= placed["minimum"][1], placed
    assert tuple(placed["minimum"]) == window_layout.minimum_size(
        tuple(placed["needed"]), (entry_window.LIST_MIN_WIDTH, entry_window.LIST_MIN_HEIGHT),
        placed["monitor"], placed["title_bar"]), placed
