"""How the cc-secrets windows look: one palette, one set of fonts and one set of ttk styles for all three screens,
and the padlock icon. Built on ttk's 'clam' theme, the one theme every Tk ships on Windows, macOS and Linux and
that can be restyled completely, so the windows look the same on all three.

Nothing here knows about entries; it only draws.
"""

from __future__ import annotations

import sys

# The palette. Quiet greys, one blue for what to press, amber for "this replaces", red for errors.
BACKGROUND = "#f5f6f8"
SURFACE = "#ffffff"
TEXT = "#1f2328"
MUTED = "#626b75"
FAINT = "#8b949e"
BORDER = "#d4d9df"
STRIPE = "#f8f9fb"
ACCENT = "#2563eb"
ACCENT_HOVER = "#1d4ed8"
ACCENT_PRESSED = "#1e40af"
SELECTION = "#dbe7fd"
AMBER = "#8a5300"
AMBER_BACKGROUND = "#fff6e0"
RED = "#b42318"
RED_HOVER = "#fdecea"
ASK_BACKGROUND = "#eef4ff"
ASK_BORDER = "#c7d7fb"

BASE_SIZE = 10


def make_dpi_aware() -> None:
    """Windows: draw at the screen's real resolution instead of being stretched (and blurred) by Windows. Must run
    before the first window. System awareness, not per monitor: Tk 8.6 cannot redraw itself for a second monitor
    of a different scale, and Windows does that for it at this level."""
    if sys.platform != "win32":
        return
    import ctypes

    PROCESS_SYSTEM_DPI_AWARE = 1
    E_ACCESSDENIED = -2147024891  # already set for this process, by its manifest or an earlier window
    result = ctypes.windll.shcore.SetProcessDpiAwareness(PROCESS_SYSTEM_DPI_AWARE)
    if result not in (0, E_ACCESSDENIED):
        raise OSError(f"Windows refused to make the window sharp on high-resolution screens (HRESULT {result}).")


def set_taskbar_identity() -> None:
    """Windows: show cc-secrets in the taskbar under its own icon, not grouped with every other Python program."""
    if sys.platform != "win32":
        return
    import ctypes

    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("DevThrottle.cc-secrets")


def _base_family(root) -> str:
    from tkinter import font

    return font.nametofont("TkDefaultFont", root=root).actual("family")


def apply(root) -> dict:
    """Style every window of this Tk. Returns the fonts by role, for widgets that take a font directly."""
    from tkinter import font, ttk

    family = _base_family(root)
    for name in ("TkDefaultFont", "TkTextFont", "TkMenuFont"):
        font.nametofont(name, root=root).configure(family=family, size=BASE_SIZE)
    fonts = {
        "base": (family, BASE_SIZE),
        "bold": (family, BASE_SIZE, "bold"),
        "small": (family, BASE_SIZE - 1),
        "title": (family, BASE_SIZE + 6, "bold"),
        "heading": (family, BASE_SIZE, "bold"),
        "mono": ("Consolas" if sys.platform == "win32" else "Menlo" if sys.platform == "darwin" else "DejaVu Sans Mono",
                 BASE_SIZE),
    }
    line = font.Font(root=root, family=family, size=BASE_SIZE).metrics("linespace")

    root.configure(background=BACKGROUND)
    style = ttk.Style(root)
    style.theme_use("clam")
    style.configure(".", background=BACKGROUND, foreground=TEXT, font=fonts["base"], bordercolor=BORDER,
                    lightcolor=BORDER, darkcolor=BORDER, troughcolor=BACKGROUND, focuscolor=ACCENT,
                    selectbackground=SELECTION, selectforeground=TEXT, insertcolor=TEXT)
    style.configure("TFrame", background=BACKGROUND)
    style.configure("Surface.TFrame", background=SURFACE)
    style.configure("TLabel", background=BACKGROUND, foreground=TEXT)
    style.configure("Muted.TLabel", foreground=MUTED)
    style.configure("Faint.TLabel", foreground=FAINT, font=fonts["small"])
    style.configure("Title.TLabel", font=fonts["title"])
    style.configure("Field.TLabel", foreground=MUTED)
    style.configure("Error.TLabel", foreground=RED)
    style.configure("Warning.TLabel", foreground=AMBER, background=AMBER_BACKGROUND, padding=(10, 8))
    style.configure("Ask.TFrame", background=ASK_BACKGROUND)
    style.configure("Ask.TLabel", background=ASK_BACKGROUND)
    style.configure("AskTitle.TLabel", background=ASK_BACKGROUND, font=fonts["bold"])
    style.configure("AskWarning.TLabel", background=ASK_BACKGROUND, foreground=AMBER, font=fonts["bold"])

    pad = (14, 6)
    style.configure("TButton", background=SURFACE, foreground=TEXT, padding=pad, relief="flat", borderwidth=1,
                    focusthickness=1, anchor="center")
    style.map("TButton", background=[("disabled", BACKGROUND), ("pressed", "#e9ecf0"), ("active", "#f0f2f5")],
              foreground=[("disabled", FAINT)], bordercolor=[("focus", ACCENT)])
    style.configure("Accent.TButton", background=ACCENT, foreground="#ffffff", bordercolor=ACCENT,
                    lightcolor=ACCENT, darkcolor=ACCENT, font=fonts["bold"])
    style.map("Accent.TButton", background=[("disabled", "#9db7f5"), ("pressed", ACCENT_PRESSED),
                                            ("active", ACCENT_HOVER)],
              foreground=[("disabled", "#ffffff")], bordercolor=[("focus", ACCENT_PRESSED)],
              lightcolor=[("active", ACCENT_HOVER)], darkcolor=[("active", ACCENT_HOVER)])
    style.configure("Danger.TButton", foreground=RED)
    style.map("Danger.TButton", background=[("disabled", BACKGROUND), ("active", RED_HOVER)],
              foreground=[("disabled", FAINT)])
    style.configure("Small.TButton", padding=(8, 2), font=fonts["small"])

    # A row of choices that look like one control (All / Passwords / Settings, Password / Setting).
    style.configure("Choice.Toolbutton", background=SURFACE, foreground=MUTED, padding=(12, 5), relief="flat",
                    borderwidth=1, anchor="center")
    style.map("Choice.Toolbutton", background=[("selected", SELECTION), ("active", "#f0f2f5")],
              foreground=[("selected", ACCENT_PRESSED), ("disabled", FAINT)],
              bordercolor=[("selected", "#a9c2f7")])

    style.configure("TEntry", fieldbackground=SURFACE, padding=(8, 5), bordercolor=BORDER)
    style.map("TEntry", bordercolor=[("focus", ACCENT)], lightcolor=[("focus", ACCENT)],
              fieldbackground=[("disabled", BACKGROUND), ("readonly", BACKGROUND)],
              foreground=[("disabled", MUTED)])
    style.configure("Placeholder.TEntry", foreground=FAINT)
    style.configure("TCheckbutton", background=BACKGROUND, indicatorbackground=SURFACE, indicatorforeground=ACCENT,
                    padding=(0, 2))
    style.map("TCheckbutton", indicatorbackground=[("disabled", BACKGROUND)], foreground=[("disabled", FAINT)],
              background=[("active", BACKGROUND)])
    style.configure("TSeparator", background=BORDER)

    style.configure("Treeview", background=SURFACE, fieldbackground=SURFACE, foreground=TEXT, borderwidth=0,
                    rowheight=int(line * 2.0), font=fonts["base"])
    style.map("Treeview", background=[("selected", SELECTION)], foreground=[("selected", TEXT)])
    style.configure("Treeview.Heading", background=BACKGROUND, foreground=MUTED, font=fonts["heading"],
                    relief="flat", borderwidth=0, padding=(8, 6))
    style.map("Treeview.Heading", background=[("active", "#eceff3")])
    style.layout("Treeview", [("Treeview.treearea", {"sticky": "nswe"})])  # no sunken border round the table
    style.configure("Vertical.TScrollbar", background="#dfe3e8", troughcolor=SURFACE, bordercolor=SURFACE,
                    lightcolor="#dfe3e8", darkcolor="#dfe3e8", arrowcolor=FAINT, relief="flat", gripcount=0)
    style.map("Vertical.TScrollbar", background=[("active", "#c5cbd3")])
    return fonts


def padlock_icon(root, size: int):
    """The window icon: a blue padlock, drawn pixel by pixel so the tool carries no image file."""
    import tkinter as tk

    image = tk.PhotoImage(master=root, width=size, height=size)
    s = size / 32.0
    body = (5 * s, 14 * s, 27 * s, 30 * s)
    cx, cy = 16 * s, 14 * s
    outer, inner = 9 * s, 5.5 * s
    hole_x, hole_y, hole_r = 16 * s, 20.5 * s, 2.4 * s
    for y in range(size):
        run_start, run_colour = 0, None
        for x in range(size + 1):
            colour = None
            if x < size:
                px, py = x + 0.5, y + 0.5
                corner = 3 * s
                nearest_x = min(max(px, body[0] + corner), body[2] - corner)
                nearest_y = min(max(py, body[1] + corner), body[3] - corner)
                in_body = (px - nearest_x) ** 2 + (py - nearest_y) ** 2 <= corner ** 2
                distance = ((px - cx) ** 2 + (py - cy) ** 2) ** 0.5
                in_shackle = py <= cy + 1 and inner <= distance <= outer and abs(px - cx) <= outer
                in_hole = ((px - hole_x) ** 2 + (py - hole_y) ** 2 <= hole_r ** 2 or
                           (abs(px - hole_x) <= hole_r * 0.55 and hole_y <= py <= hole_y + 5 * s))
                if in_body and not in_hole:
                    colour = ACCENT
                elif in_shackle and not in_body:
                    colour = "#5b6573"
            if colour != run_colour:
                if run_colour is not None and x > run_start:
                    image.put(run_colour, to=(run_start, y, x, y + 1))
                run_start, run_colour = x, colour
    return image


def set_icon(root) -> None:
    """The padlock for this window and every window it opens."""
    icons = [padlock_icon(root, 64), padlock_icon(root, 32), padlock_icon(root, 16)]
    root.iconphoto(True, *icons)
    root._cc_secrets_icons = icons  # Tk drops an image Python no longer holds
