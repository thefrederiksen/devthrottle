"""Where the cc-secrets windows go on screen, and what the list remembers between openings - worked out without a
screen, so it is tested like the rest.

A rectangle is (left, top, right, bottom) in screen pixels; a monitor is the rectangle of its work area (the
screen without the taskbar). Windows of this tool open where the owner is looking: a form over the list it was
opened from, the agent's pop-up on the monitor under the mouse, and the list where it was last closed, as long as
that place is still on a monitor.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

from . import filelog
from .window_actions import SHOW_ALL, SHOW_CHOICES, SORT_COLUMNS

Rect = Tuple[int, int, int, int]

# How much of a remembered window must still be on a monitor for it to go back there: enough of the title bar to
# grab and move it.
MIN_VISIBLE_WIDTH = 120
MIN_VISIBLE_HEIGHT = 40


def _overlap(a: Rect, b: Rect) -> Tuple[int, int]:
    return max(0, min(a[2], b[2]) - max(a[0], b[0])), max(0, min(a[3], b[3]) - max(a[1], b[1]))


def monitor_at(x: int, y: int, monitors: Sequence[Rect]) -> Rect:
    """The monitor holding the point, or the nearest one when the point is between monitors."""
    if not monitors:
        raise ValueError("no monitors")
    for m in monitors:
        if m[0] <= x < m[2] and m[1] <= y < m[3]:
            return m

    def distance(m: Rect) -> int:
        dx = max(m[0] - x, 0, x - (m[2] - 1))
        dy = max(m[1] - y, 0, y - (m[3] - 1))
        return dx * dx + dy * dy
    return min(monitors, key=distance)


def clamp_into(x: int, y: int, width: int, height: int, monitor: Rect) -> Tuple[int, int]:
    """Move a window of this size at (x, y) just enough to sit inside the monitor; its top left wins when it is
    bigger than the monitor, so the title bar stays reachable."""
    x = min(x, monitor[2] - width)
    y = min(y, monitor[3] - height)
    return max(x, monitor[0]), max(y, monitor[1])


def centre_over(parent: Rect, width: int, height: int, monitors: Sequence[Rect]) -> Tuple[int, int]:
    """Where a form of this size goes: centred over the window it was opened from, kept on that window's
    monitor. Before this, a form centred itself on the main screen, so with the list on a second screen the
    form opened in the corner of another one."""
    cx, cy = (parent[0] + parent[2]) // 2, (parent[1] + parent[3]) // 2
    x, y = cx - width // 2, cy - height // 2
    return clamp_into(x, y, width, height, monitor_at(cx, cy, monitors))


def centre_on_monitor(point: Tuple[int, int], width: int, height: int, monitors: Sequence[Rect]) -> Tuple[int, int]:
    """A window with no parent - the agent's pop-up, a list opened for the first time - centred on the monitor
    under the point (the mouse), a little above the middle where the eye goes first."""
    m = monitor_at(point[0], point[1], monitors)
    x = m[0] + (m[2] - m[0] - width) // 2
    y = m[1] + (m[3] - m[1] - height) // 3
    return clamp_into(x, y, width, height, m)


def still_visible(rect: Rect, monitors: Sequence[Rect]) -> bool:
    """True when enough of a remembered window's top edge is on some monitor to grab it: a monitor unplugged
    since must not leave the list opening where nobody can see it."""
    title_bar = (rect[0], rect[1], rect[2], rect[1] + MIN_VISIBLE_HEIGHT)
    return any(w >= MIN_VISIBLE_WIDTH and h >= MIN_VISIBLE_HEIGHT for w, h in (_overlap(title_bar, m) for m in monitors))


@dataclass
class ListPrefs:
    """What the list remembers: where it was and how big, and how it was sorted and filtered. Nothing about any
    entry - no names, no values."""
    x: Optional[int] = None
    y: Optional[int] = None
    width: int = 1060
    height: int = 620
    sort_column: str = "name"
    sort_descending: bool = False
    show: str = SHOW_ALL

    def rect(self) -> Optional[Rect]:
        if self.x is None or self.y is None:
            return None
        return self.x, self.y, self.x + self.width, self.y + self.height


def load_prefs(path: Path) -> ListPrefs:
    """The remembered settings, or the defaults when there are none yet. A file that cannot be understood is
    logged and replaced by the defaults on the next close: it only holds where a window was."""
    if not path.exists():
        return ListPrefs()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        prefs = ListPrefs(
            x=None if raw.get("x") is None else int(raw["x"]),
            y=None if raw.get("y") is None else int(raw["y"]),
            width=max(640, int(raw.get("width", 1060))),
            height=max(360, int(raw.get("height", 620))),
            sort_column=str(raw.get("sort_column", "name")),
            sort_descending=bool(raw.get("sort_descending", False)),
            show=str(raw.get("show", SHOW_ALL)),
        )
    except (ValueError, TypeError, AttributeError, OSError) as exc:
        filelog.write(f"[window_layout] load_prefs: {path} not understood ({type(exc).__name__}); using defaults")
        return ListPrefs()
    if prefs.sort_column not in SORT_COLUMNS:
        prefs.sort_column = "name"
    if prefs.show not in SHOW_CHOICES:
        prefs.show = SHOW_ALL
    return prefs


def save_prefs(path: Path, prefs: ListPrefs) -> None:
    path.write_text(json.dumps(asdict(prefs), indent=2), encoding="utf-8")


def list_geometry(prefs: ListPrefs, monitors: Sequence[Rect], pointer: Tuple[int, int]) -> Tuple[int, int, int, int]:
    """(x, y, width, height) for the list: where it was last closed when that is still on a monitor, otherwise
    centred on the monitor under the mouse. Never larger than that monitor."""
    remembered = prefs.rect()
    if remembered is not None and still_visible(remembered, monitors):
        m = monitor_at(remembered[0] + MIN_VISIBLE_WIDTH // 2, remembered[1] + MIN_VISIBLE_HEIGHT // 2, monitors)
        width, height = min(prefs.width, m[2] - m[0]), min(prefs.height, m[3] - m[1])
        return remembered[0], remembered[1], width, height
    m = monitor_at(pointer[0], pointer[1], monitors)
    width, height = min(prefs.width, m[2] - m[0]), min(prefs.height, m[3] - m[1])
    x, y = centre_on_monitor(pointer, width, height, monitors)
    return x, y, width, height


def windows_monitors() -> List[Rect]:
    """The work area of every monitor, from Windows itself (Tk only knows the main screen)."""
    import ctypes
    from ctypes import wintypes

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT), ("rcWork", wintypes.RECT),
                    ("dwFlags", wintypes.DWORD)]

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    found: List[Rect] = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMONITOR, wintypes.HDC, ctypes.POINTER(wintypes.RECT),
                                       wintypes.LPARAM)

    def collect(monitor, _dc, _rect, _data):
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        if user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
            r = info.rcWork
            found.append((r.left, r.top, r.right, r.bottom))
        return True

    user32.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.c_void_p, callback_type, wintypes.LPARAM]
    if not user32.EnumDisplayMonitors(None, None, callback_type(collect), 0) or not found:
        raise OSError(f"Windows did not list the monitors (error {ctypes.get_last_error()}).")
    return found
