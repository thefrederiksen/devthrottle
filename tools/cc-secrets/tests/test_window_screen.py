"""The cc-secrets list driven on a real screen: where the form opens, what it shows, how fast each click is, and
what the list remembers when it closes. Skipped where there is no screen (a continuous integration runner).

These are the owner's complaints of 2026-10-10, each checked the way he met it: Edit opened its form on the
wrong screen, the QA email's form was empty, and every click took about two seconds.
"""

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
    handler a moment after it opens."""
    script = textwrap.dedent("""
        import sys, tkinter as tk
        sys.path.insert(0, sys.argv[1])
        from src import entry_window, paths
        from src.audit import AuditLog, OwnerApproval
        from src.store import SecretStore
        from src.storefile import UserOnlyFile
        from src.window_actions import WINDOW_RECORD, WindowActions
        real = tk.Tk.mainloop
        def close_soon(self, n=0):
            self.after(300, lambda: self.tk.call(self.protocol("WM_DELETE_WINDOW")))
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
    prefs = window_layout.load_prefs(paths.secrets_home() / entry_window.PREFS_FILE)
    assert prefs.x is not None and prefs.width >= 760
