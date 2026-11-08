"""Printing for dt-zoho, in the AXI shape (docs/axi-standard.md): a `count:` line, a list block, a
`help[]` block, or `key: value` lines for one record. `--json` prints the same data as JSON with every
field, ASCII only. Nothing here talks to a server."""

from __future__ import annotations

import json
import sys
from typing import Iterable, List, Mapping, Optional, Sequence

from cc_shared.axi_output import format_count, format_help, render_list, write_blocks


def ascii_text(text: str) -> str:
    """Free text (a message body) as printable ASCII, keeping its line breaks and backslashes readable.
    Any other character becomes a \\uXXXX escape; --json carries the exact text."""
    out: List[str] = []
    for ch in text.replace("\r\n", "\n"):
        code = ord(ch)
        if ch in "\n\t" or 0x20 <= code <= 0x7E:
            out.append(ch)
        elif code <= 0xFFFF:
            out.append(f"\\u{code:04x}")
        else:
            out.append(f"\\U{code:08x}")
    return "".join(out)


def kv_block(pairs: Iterable[tuple[str, object]]) -> str:
    lines = []
    for key, value in pairs:
        if value is None:
            continue
        if isinstance(value, bool):
            value = "true" if value else "false"
        one_line = ascii_text(str(value)).replace("\n", "\\n").replace("\t", " ")
        lines.append(f"{key}: {one_line}")
    return "\n".join(lines)


def emit_json(data: object) -> None:
    sys.stdout.write(json.dumps(data, indent=2, ensure_ascii=True) + "\n")


def emit(blocks: Sequence[Optional[str]]) -> None:
    write_blocks(sys.stdout, *[b for b in blocks if b])


def emit_list(name: str, fields: Sequence[str], records: Sequence[Mapping[str, object]], total: Optional[int],
              help_lines: Sequence[str] = ()) -> None:
    blocks: List[Optional[str]] = [format_count(len(records), total=total)]
    blocks.append(render_list(name, fields, records))
    if help_lines:
        blocks.append(format_help(help_lines))
    emit(blocks)
