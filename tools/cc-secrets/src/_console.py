"""ASCII-only terminal output (house rule), installed once at import.

The same two patches every shipped tool carries: Rich truncates an overflowing table cell with the
Unicode ellipsis, and Typer draws its help and error panels with a Unicode box. Both are forced to ASCII.
"""


def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return

    def _truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    _truncate._ascii_ellipsis = True
    rich.text.Text.truncate = _truncate


def _install_ascii_typer_panels():
    try:
        import typer.rich_utils as _tru
        from rich import box as _rbox
    except ImportError:
        return
    if getattr(_tru.Panel, "_ascii_box", False):
        return
    _OrigPanel = _tru.Panel

    class _AsciiPanel(_OrigPanel):
        _ascii_box = True

        def __init__(self, *args, **kwargs):
            kwargs.setdefault("box", _rbox.ASCII)
            super().__init__(*args, **kwargs)

    _tru.Panel = _AsciiPanel


_install_ascii_truncation()
_install_ascii_typer_panels()
