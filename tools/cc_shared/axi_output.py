"""Shared output rendering for command-line tools that follow the AXI standard.

The standard is in docs/axi-standard.md (principles by Kun Chen, https://axi.md). Every list
command in cc-devthrottle renders through this module, so every command prints the same shape:

    count: 26 (needs-you 3, working 4, ready 17, snoozed 2)
    sessions[26]{id,name,state,repo}:
      d2a4069f,cc-consult,needs-you,cc-consult
      ...
    help[2]:
      cc-devthrottle session list --fields id,name,state,repo,machine
      cc-devthrottle message send <id> "<message>"

Everything here is a pure function that returns text. Nothing prints, except `write_blocks`, which
writes to the stream the caller hands it. The module has no third-party dependencies.

THE LIST FORMAT
---------------
A list is a header line, `name[N]{field1,field2,...}:`, followed by exactly N rows. Each row is two
spaces, then the record's values in field order, separated by commas.

A value is written one of three ways:

- None is written as nothing at all: `a,,c` has None in the middle.
- A plain value is written as-is. A value is plain when it is non-empty, is pure printable ASCII
  (space through tilde), has no leading or trailing whitespace, and contains no comma and no double
  quote. A backslash in a plain value is literal: `C:\\repos` is written `C:\\repos` unchanged in
  the row, because escapes are only ever read inside quotes.
- Every other value is quoted: wrapped in double quotes, with these escapes inside -
  `\\\\` backslash, `\\"` double quote, `\\n` newline, `\\r` carriage return, `\\t` tab,
  `\\uXXXX` any other control character or any non-ASCII character up to U+FFFF, and
  `\\UXXXXXXXX` any character above U+FFFF. So the empty string is written `""`, which keeps it
  distinct from None, and a row always stays on one line and is pure ASCII.

`parse_list` reads that back. A record whose values are strings or None comes back exactly. A
number or a boolean is written as its text (`true` / `false` for booleans) and comes back as that
text, because the list format does not carry types - use `--json` when types matter.

Field names and list names are restricted to letters, digits, `_`, `-` and `.`, so the header never
needs quoting.
"""

from __future__ import annotations

import re
import sys
from collections.abc import Mapping, Sequence
from typing import TextIO

__all__ = [
    "FieldsError",
    "ListParseError",
    "escape_ascii",
    "format_count",
    "format_help",
    "format_value",
    "parse_fields",
    "parse_fields_or_exit",
    "parse_list",
    "render_list",
    "write_blocks",
]

USAGE_ERROR_EXIT_CODE = 2

# Both are used with fullmatch: "$" would also match just before a trailing newline, so a name like
# "sessions\n" would pass and split the header line in two.
_NAME_PATTERN = re.compile(r"[A-Za-z0-9_.-]+")
_HEADER_PATTERN = re.compile(r"([A-Za-z0-9_.-]+)\[(\d+)\]\{([A-Za-z0-9_.,-]*)\}:")
_ROW_INDENT = "  "

# The escapes that have a short form. Everything else that needs escaping uses \u or \U.
_SHORT_ESCAPES = {"\\": "\\\\", '"': '\\"', "\n": "\\n", "\r": "\\r", "\t": "\\t"}
_SHORT_UNESCAPES = {"\\": "\\", '"': '"', "n": "\n", "r": "\r", "t": "\t"}


class FieldsError(ValueError):
    """A `--fields` value named a field that does not exist, or was malformed."""


class ListParseError(ValueError):
    """Text handed to `parse_list` is not a well-formed rendered list."""


# ---------------------------------------------------------------------------------------------------
# Values
# ---------------------------------------------------------------------------------------------------


def escape_ascii(text: str) -> str:
    """Escape `text` so the result is pure printable ASCII and can be unescaped exactly.

    This is the escaping used inside a quoted value. It does not add the surrounding quotes.
    """
    parts: list[str] = []
    for ch in text:
        short = _SHORT_ESCAPES.get(ch)
        if short is not None:
            parts.append(short)
            continue
        code = ord(ch)
        if 0x20 <= code <= 0x7E:
            parts.append(ch)
        elif code <= 0xFFFF:
            parts.append(f"\\u{code:04x}")
        else:
            parts.append(f"\\U{code:08x}")
    return "".join(parts)


def _is_plain(text: str) -> bool:
    if text == "" or text != text.strip():
        return False
    if "," in text or '"' in text:
        return False
    return all(0x20 <= ord(ch) <= 0x7E for ch in text)


def _value_text(value: object) -> str:
    # bool is checked before int because bool is a subclass of int.
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (str, int, float)):
        return str(value)
    raise TypeError(
        f"cannot render a value of type {type(value).__name__} in a list; "
        "pass a string, number, boolean or None"
    )


def format_value(value: object) -> str:
    """Render one value for a list row, following the rules in the module docstring."""
    if value is None:
        return ""
    text = _value_text(value)
    if _is_plain(text):
        return text
    return '"' + escape_ascii(text) + '"'


# ---------------------------------------------------------------------------------------------------
# Lists
# ---------------------------------------------------------------------------------------------------


def _check_name(kind: str, name: str) -> None:
    if not isinstance(name, str) or not _NAME_PATTERN.fullmatch(name):
        raise ValueError(
            f"{kind} {name!r} is not allowed; use only letters, digits, '_', '-' and '.'"
        )


def render_list(name: str, fields: Sequence[str], records: Sequence[Mapping[str, object]]) -> str:
    """Render records as a list block: the header line, then one indented row per record.

    Every record must have every field as a key - a missing key is a caller defect and raises
    KeyError rather than being rendered as if it were None. An empty `records` renders the header
    alone, `name[0]{...}:`. The result has no trailing newline.
    """
    _check_name("list name", name)
    if not fields:
        raise ValueError("a list needs at least one field")
    for field in fields:
        _check_name("field name", field)
    if len(set(fields)) != len(fields):
        raise ValueError(f"field names repeat: {', '.join(fields)}")

    lines = [f"{name}[{len(records)}]{{{','.join(fields)}}}:"]
    for index, record in enumerate(records):
        missing = [field for field in fields if field not in record]
        if missing:
            raise KeyError(f"record {index} has no value for field(s): {', '.join(missing)}")
        lines.append(_ROW_INDENT + ",".join(format_value(record[field]) for field in fields))
    return "\n".join(lines)


def _read_quoted(row: str, start: int) -> tuple[str, int]:
    """Read a quoted value whose opening quote is at `start`. Returns the value and the index
    just past the closing quote."""
    parts: list[str] = []
    i = start + 1
    while i < len(row):
        ch = row[i]
        if ch == '"':
            return "".join(parts), i + 1
        if ch != "\\":
            if not 0x20 <= ord(ch) <= 0x7E:
                raise ListParseError(f"non-ASCII or control character at column {i} in row: {row!r}")
            parts.append(ch)
            i += 1
            continue
        if i + 1 >= len(row):
            raise ListParseError(f"row ends inside an escape: {row!r}")
        kind = row[i + 1]
        if kind in _SHORT_UNESCAPES:
            parts.append(_SHORT_UNESCAPES[kind])
            i += 2
        elif kind in ("u", "U"):
            width = 4 if kind == "u" else 8
            digits = row[i + 2 : i + 2 + width]
            if len(digits) != width or not all(c in "0123456789abcdefABCDEF" for c in digits):
                raise ListParseError(f"bad \\{kind} escape at column {i} in row: {row!r}")
            code_point = int(digits, 16)
            if code_point > 0x10FFFF:
                raise ListParseError(f"\\{kind} escape beyond U+10FFFF at column {i} in row: {row!r}")
            parts.append(chr(code_point))
            i += 2 + width
        else:
            raise ListParseError(f"unknown escape \\{kind} at column {i} in row: {row!r}")
    raise ListParseError(f"row ends inside a quoted value: {row!r}")


def _split_row(row: str) -> list[str | None]:
    values: list[str | None] = []
    i = 0
    while True:
        if i < len(row) and row[i] == '"':
            value, i = _read_quoted(row, i)
            values.append(value)
        else:
            end = row.find(",", i)
            end = len(row) if end == -1 else end
            raw = row[i:end]
            if '"' in raw:
                raise ListParseError(f"double quote inside an unquoted value at column {i}: {row!r}")
            if not _is_plain(raw) and raw != "":
                raise ListParseError(f"unquoted value {raw!r} should have been quoted: {row!r}")
            values.append(raw if raw != "" else None)
            i = end
        if i == len(row):
            return values
        if row[i] != ",":
            raise ListParseError(f"expected a comma at column {i} in row: {row!r}")
        i += 1


def parse_list(text: str, name: str | None = None) -> tuple[list[str], list[dict[str, str | None]]]:
    """Read a list block back out of rendered output. Returns (fields, records).

    `text` may be a whole command's output - count line, list, help lines - and the list block is
    found by its header. When `name` is given only that list is read; otherwise the text must hold
    exactly one list. The header's count must match the number of rows that follow it, exactly.
    """
    lines = text.splitlines()
    headers = []
    for index, line in enumerate(lines):
        match = _HEADER_PATTERN.fullmatch(line)
        if match and (name is None or match.group(1) == name):
            headers.append((index, match))
    if not headers:
        wanted = f"a list named {name!r}" if name else "a list"
        raise ListParseError(f"no header for {wanted} in the text")
    if len(headers) > 1:
        raise ListParseError(f"{len(headers)} list headers found; pass name= to pick one")

    index, match = headers[0]
    count = int(match.group(2))
    fields = match.group(3).split(",")
    if any(field == "" for field in fields):
        raise ListParseError(f"empty field name in header: {lines[index]!r}")
    if len(set(fields)) != len(fields):
        raise ListParseError(f"repeated field name in header: {lines[index]!r}")

    rows = lines[index + 1 : index + 1 + count]
    if len(rows) != count:
        raise ListParseError(f"header says {count} rows but only {len(rows)} lines follow")
    records: list[dict[str, str | None]] = []
    for row in rows:
        if not row.startswith(_ROW_INDENT) or row.startswith(_ROW_INDENT + " "):
            raise ListParseError(f"row is not indented by exactly two spaces: {row!r}")
        values = _split_row(row[len(_ROW_INDENT) :])
        if len(values) != len(fields):
            raise ListParseError(f"row has {len(values)} values, header has {len(fields)} fields: {row!r}")
        records.append(dict(zip(fields, values)))

    following = index + 1 + count
    if following < len(lines) and lines[following].startswith(_ROW_INDENT):
        raise ListParseError(f"header says {count} rows but more indented lines follow")
    return fields, records


# ---------------------------------------------------------------------------------------------------
# The count line
# ---------------------------------------------------------------------------------------------------


def format_count(
    shown: int,
    total: int | None = None,
    breakdown: Sequence[tuple[str, int]] | None = None,
) -> str:
    """Render the `count:` line.

    - `format_count(26)` -> `count: 26`
    - `format_count(7, breakdown=[("needs-you", 3), ("working", 4)])`
      -> `count: 7 (needs-you 3, working 4)`
    - `format_count(3, total=26)` -> `count: 3 of 26 total`

    Pass `total` whenever a filter was applied, even if it matched everything. `breakdown` is
    printed in the order given, zeros included, and must add up to `shown` - a breakdown that does
    not is a caller defect and raises. An empty result is `count: 0` or `count: 0 of N total`;
    that line is what makes an empty result definitive, so always print it.
    """
    if not isinstance(shown, int) or isinstance(shown, bool) or shown < 0:
        raise ValueError(f"shown must be a non-negative integer, got {shown!r}")
    line = f"count: {shown}"
    if total is not None:
        if not isinstance(total, int) or isinstance(total, bool) or total < shown:
            raise ValueError(f"total must be an integer no smaller than shown ({shown}), got {total!r}")
        line += f" of {total} total"
    if breakdown:
        parts = []
        for label, number in breakdown:
            _check_name("count label", label)
            if not isinstance(number, int) or isinstance(number, bool) or number < 0:
                raise ValueError(f"count for {label!r} must be a non-negative integer, got {number!r}")
            parts.append(f"{label} {number}")
        added = sum(number for _, number in breakdown)
        if added != shown:
            raise ValueError(f"breakdown adds up to {added} but shown is {shown}")
        line += " (" + ", ".join(parts) + ")"
    return line


# ---------------------------------------------------------------------------------------------------
# help[] lines
# ---------------------------------------------------------------------------------------------------


def format_help(commands: Sequence[str]) -> str:
    """Render `help[N]:` followed by one indented command per line.

    Commands are written exactly as given, so the caller writes the placeholders
    (`cc-devthrottle message send <id> "<message>"`). A command must be one line of printable
    ASCII; anything else raises, because a help line is guidance, not data, and must never be
    silently altered.
    """
    if not commands:
        raise ValueError("help needs at least one command; omit the block instead")
    for command in commands:
        if not command or command != command.strip() or not all(0x20 <= ord(c) <= 0x7E for c in command):
            raise ValueError(f"help command must be one trimmed line of printable ASCII: {command!r}")
    return "\n".join([f"help[{len(commands)}]:"] + [_ROW_INDENT + command for command in commands])


# ---------------------------------------------------------------------------------------------------
# --fields
# ---------------------------------------------------------------------------------------------------


def parse_fields(requested: str | None, valid: Sequence[str], default: Sequence[str]) -> list[str]:
    """Turn a `--fields` value into the list of fields to render.

    None means the option was not given, and returns `default`. Otherwise the value is split on
    commas and each name must be one of `valid`; an unknown, empty or repeated name raises
    FieldsError whose message lists every valid name. Nothing is dropped or corrected.
    """
    unknown_default = [field for field in default if field not in valid]
    if unknown_default:
        raise ValueError(f"default fields are not all valid: {', '.join(unknown_default)}")
    if requested is None:
        return list(default)

    valid_text = ", ".join(valid)
    names = [part.strip() for part in requested.split(",")]
    if any(name == "" for name in names):
        raise FieldsError(f"--fields has an empty field name in {escape_ascii(requested)!r}. Valid fields: {valid_text}")
    unknown = [name for name in names if name not in valid]
    if unknown:
        listed = ", ".join(escape_ascii(name) for name in unknown)
        noun = "field" if len(unknown) == 1 else "fields"
        raise FieldsError(f"unknown {noun} for --fields: {listed}. Valid fields: {valid_text}")
    repeated = sorted({name for name in names if names.count(name) > 1})
    if repeated:
        raise FieldsError(f"--fields names a field more than once: {', '.join(repeated)}. Valid fields: {valid_text}")
    return names


def parse_fields_or_exit(
    requested: str | None,
    valid: Sequence[str],
    default: Sequence[str],
    err: TextIO | None = None,
) -> list[str]:
    """`parse_fields` for a command: on a bad value, write the message to `err` (standard error
    when not given) and exit with code 2, the usage-error code. Raising SystemExit works the same
    inside a typer command as outside one."""
    try:
        return parse_fields(requested, valid, default)
    except FieldsError as exc:
        stream = err if err is not None else sys.stderr
        stream.write(f"Error: {exc}\n")
        raise SystemExit(USAGE_ERROR_EXIT_CODE) from exc


# ---------------------------------------------------------------------------------------------------
# Writing
# ---------------------------------------------------------------------------------------------------


def write_blocks(stream: TextIO, *blocks: str) -> None:
    """Write rendered blocks to `stream`, one per line group, ending with a newline.

    Refuses non-ASCII text, so nothing that bypassed the renderers can reach the output."""
    text = "\n".join(blocks) + "\n"
    if not text.isascii():
        raise ValueError("output must be pure ASCII; render values through this module")
    stream.write(text)
