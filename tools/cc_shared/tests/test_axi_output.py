"""Tests for the shared AXI output helper (cc_shared/axi_output.py).

This file runs in the continuous integration job `tool-contracts`, which installs only pytest,
typer, rich and requests - so it must not import anything else from cc_shared that needs more.
"""

import io
import random
import sys
from pathlib import Path

import pytest
import typer
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared.axi_output import (  # noqa: E402
    FieldsError,
    ListParseError,
    escape_ascii,
    format_count,
    format_help,
    format_value,
    parse_fields,
    parse_fields_or_exit,
    parse_list,
    render_list,
    write_blocks,
)

FIELDS = ["id", "name", "state"]


def _round_trip(records, fields=FIELDS):
    text = render_list("sessions", fields, records)
    assert text.isascii()
    parsed_fields, parsed = parse_list(text)
    assert parsed_fields == list(fields)
    return text, parsed


# ---------------------------------------------------------------------------------------------------
# Quoting
# ---------------------------------------------------------------------------------------------------


class TestQuoting:
    def test_format_value_PlainValue_WrittenAsIs(self):
        assert format_value("cc-consult") == "cc-consult"

    def test_format_value_Comma_Quoted(self):
        assert format_value("a,b") == '"a,b"'

    def test_format_value_DoubleQuote_QuotedAndEscaped(self):
        assert format_value('say "hi"') == '"say \\"hi\\""'

    def test_format_value_Newline_QuotedAndEscapedOnOneLine(self):
        rendered = format_value("line one\nline two\r\n")
        assert rendered == '"line one\\nline two\\r\\n"'
        assert "\n" not in rendered and "\r" not in rendered

    @pytest.mark.parametrize("value", [" leading", "trailing ", "\tpadded\t", " "])
    def test_format_value_SurroundingWhitespace_Quoted(self, value):
        rendered = format_value(value)
        assert rendered.startswith('"') and rendered.endswith('"')

    def test_format_value_BackslashAlone_LiteralAndUnquoted(self):
        assert format_value("C:\\repos\\x") == "C:\\repos\\x"

    def test_format_value_BackslashInQuotedValue_Escaped(self):
        assert format_value("C:\\a, b") == '"C:\\\\a, b"'

    def test_format_value_EmptyAndNone_Distinct(self):
        assert format_value(None) == ""
        assert format_value("") == '""'

    def test_format_value_NumbersAndBooleans_WrittenAsText(self):
        assert format_value(3) == "3"
        assert format_value(True) == "true"
        assert format_value(False) == "false"

    def test_format_value_UnsupportedType_Raises(self):
        with pytest.raises(TypeError):
            format_value(["a"])

    def test_render_list_TrickyValues_RoundTripExactly(self):
        records = [
            {"id": "1", "name": "a,b", "state": "ready"},
            {"id": "2", "name": 'quote " inside', "state": "working"},
            {"id": "3", "name": "two\nlines", "state": "needs-you"},
            {"id": "4", "name": "  padded  ", "state": "snoozed"},
            {"id": "5", "name": "", "state": None},
            {"id": "6", "name": '"', "state": ","},
            {"id": "7", "name": "back\\slash, \\n not a newline", "state": "crashed"},
        ]
        text, parsed = _round_trip(records)
        assert len(text.splitlines()) == 1 + len(records)
        assert parsed == records


# ---------------------------------------------------------------------------------------------------
# ASCII safety
# ---------------------------------------------------------------------------------------------------


class TestAsciiSafety:
    def test_escape_ascii_NonAscii_EscapedNotDropped(self):
        assert escape_ascii("caf\u00e9") == "caf\\u00e9"

    def test_escape_ascii_AboveBasicPlane_UsesLongEscape(self):
        assert escape_ascii("\U0001f600") == "\\U0001f600"

    def test_escape_ascii_ControlCharacters_Escaped(self):
        assert escape_ascii("\x00\x1b\x7f") == "\\u0000\\u001b\\u007f"

    def test_render_list_NonAscii_OutputAsciiAndParsesBackExactly(self):
        records = [
            {"id": "1", "name": "caf\u00e9", "state": "ready"},
            {"id": "2", "name": "\u65e5\u672c\u8a9e \U0001f680", "state": "working"},
            {"id": "3", "name": "em\u2014dash \u201cquoted\u201d", "state": "ready"},
            {"id": "4", "name": "\ud800 lone surrogate", "state": "ready"},
            {"id": "5", "name": "literal \\u00e9 text", "state": "ready"},
        ]
        text, parsed = _round_trip(records)
        assert "?" not in text
        assert parsed == records

    def test_write_blocks_NonAscii_Refused(self):
        with pytest.raises(ValueError):
            write_blocks(io.StringIO(), "caf\u00e9")


# ---------------------------------------------------------------------------------------------------
# Round trip and parse-back strictness
# ---------------------------------------------------------------------------------------------------


class TestRoundTrip:
    ALPHABET = ['a', 'Z', '0', ' ', ',', '"', '\\', '\n', '\r', '\t', '\x00', '\u00e9', '\u2603',
                '\U0001f600', 'u', '{', '}', '[', ']', ':', "'", '-']

    def test_render_list_RandomRecords_RoundTripExactly(self):
        rng = random.Random(2922)
        for _ in range(500):
            records = []
            for i in range(rng.randint(0, 6)):
                record = {"id": str(i)}
                for field in ("name", "state"):
                    if rng.random() < 0.1:
                        record[field] = None
                    else:
                        record[field] = "".join(rng.choice(self.ALPHABET) for _ in range(rng.randint(0, 12)))
                records.append(record)
            _, parsed = _round_trip(records)
            assert parsed == records

    def test_render_list_Empty_HeaderOnlyAndParsesToNothing(self):
        text, parsed = _round_trip([])
        assert text == "sessions[0]{id,name,state}:"
        assert parsed == []

    def test_render_list_MissingField_Raises(self):
        with pytest.raises(KeyError):
            render_list("sessions", FIELDS, [{"id": "1", "name": "x"}])

    @pytest.mark.parametrize("bad", ["has space", "a,b", "caf\u00e9", "", "id\n"])
    def test_render_list_BadFieldName_Raises(self, bad):
        with pytest.raises(ValueError):
            render_list("sessions", ["id", bad], [])

    @pytest.mark.parametrize("bad", ["sessions\n", "has space", ""])
    def test_render_list_BadListName_Raises(self, bad):
        with pytest.raises(ValueError):
            render_list(bad, ["id"], [{"id": "x"}])

    def test_parse_list_WholeCommandOutput_FindsTheList(self):
        records = [{"id": "d2a4069f", "name": "cc-consult", "state": "needs-you"}]
        output = "\n".join([
            format_count(1, total=26),
            render_list("sessions", FIELDS, records),
            format_help(["cc-devthrottle message send <id> \"<message>\""]),
        ])
        assert parse_list(output) == (FIELDS, records)

    def test_parse_list_TwoLists_NeedsName(self):
        text = render_list("a", ["x"], [{"x": "1"}]) + "\n" + render_list("b", ["x"], [{"x": "2"}])
        with pytest.raises(ListParseError):
            parse_list(text)
        assert parse_list(text, name="b") == (["x"], [{"x": "2"}])

    @pytest.mark.parametrize("text", [
        "sessions[2]{id}:\n  1",                 # fewer rows than the header says
        "sessions[1]{id}:\n  1\n  2",            # more rows than the header says
        "sessions[1]{id,name}:\n  1",            # too few values
        "sessions[1]{id}:\n  \"unterminated",    # quote never closed
        "sessions[1]{id}:\n  \"a\"b",            # junk after a quoted value
        "sessions[1]{id}:\n  a\"b",              # quote inside an unquoted value
        "sessions[1]{id}:\n  \"\\x\"",           # unknown escape
        "sessions[1]{id}:\n  \"\\u12\"",         # short unicode escape
        "sessions[1]{id}:\n  \"\\U00110000\"",   # escape beyond U+10FFFF
        "sessions[1]{id,id}:\n  first,second",  # repeated field name
        "sessions[1]{id}:\n   a",                # three-space indent
        "sessions[1]{id}:\n a",                  # one-space indent
        "sessions[1]{id}:\n  caf\u00e9",         # raw non-ASCII
        "nothing to see here",
    ])
    def test_parse_list_Malformed_Raises(self, text):
        with pytest.raises(ListParseError):
            parse_list(text)

    @pytest.mark.parametrize("text", [
        "sessions[\u0661]{id}:\n  x",              # Arabic-Indic digit one: int() accepts it
        "sessions[" + "9" * 4301 + "]{id}:\n  x",  # too many digits for int() to convert
    ], ids=["non-ascii-digit", "too-many-digits"])
    def test_parse_list_HeaderCountNotAsciiDigitsOrTooLong_RaisesListParseError(self, text):
        with pytest.raises(ListParseError):
            parse_list(text)


# ---------------------------------------------------------------------------------------------------
# count:
# ---------------------------------------------------------------------------------------------------


class TestCount:
    def test_format_count_Plain(self):
        assert format_count(26) == "count: 26"

    def test_format_count_Breakdown_InCallerOrder(self):
        line = format_count(26, breakdown=[("needs-you", 3), ("working", 4), ("ready", 17), ("snoozed", 2)])
        assert line == "count: 26 (needs-you 3, working 4, ready 17, snoozed 2)"

    def test_format_count_Breakdown_ZerosKept(self):
        assert format_count(2, breakdown=[("ready", 2), ("crashed", 0)]) == "count: 2 (ready 2, crashed 0)"

    def test_format_count_FilterNarrowed_OfTotal(self):
        assert format_count(3, total=26) == "count: 3 of 26 total"

    def test_format_count_OfTotalWithBreakdown(self):
        assert format_count(3, total=26, breakdown=[("working", 3)]) == "count: 3 of 26 total (working 3)"

    def test_format_count_Empty_SaysZero(self):
        assert format_count(0) == "count: 0"

    def test_format_count_EmptyAfterFilter_SaysZeroOfTotal(self):
        assert format_count(0, total=26) == "count: 0 of 26 total"

    def test_format_count_FilterOnEmptySource_SaysPlainZero(self):
        assert format_count(0, total=0) == "count: 0"

    def test_format_count_FilterOnEmptySourceWithBreakdown_SaysPlainZero(self):
        assert format_count(0, total=0, breakdown=[("offline", 0)]) == "count: 0 (offline 0)"

    def test_format_count_BreakdownDoesNotAddUp_Raises(self):
        with pytest.raises(ValueError):
            format_count(5, breakdown=[("ready", 2)])

    def test_format_count_EmptyBreakdown_Raises(self):
        with pytest.raises(ValueError):
            format_count(5, breakdown=[])

    @pytest.mark.parametrize("bad", ["ready\n", "has space", ""])
    def test_format_count_BadLabel_Raises(self, bad):
        with pytest.raises(ValueError):
            format_count(1, breakdown=[(bad, 1)])

    def test_format_count_TotalSmallerThanShown_Raises(self):
        with pytest.raises(ValueError):
            format_count(5, total=4)

    @pytest.mark.parametrize("shown", [-1, True, "3"])
    def test_format_count_BadShown_Raises(self, shown):
        with pytest.raises(ValueError):
            format_count(shown)

    def test_write_blocks_EmptyResult_NeverBlank(self):
        stream = io.StringIO()
        write_blocks(stream, format_count(0, total=26), render_list("sessions", FIELDS, []))
        assert stream.getvalue() == "count: 0 of 26 total\nsessions[0]{id,name,state}:\n"


# ---------------------------------------------------------------------------------------------------
# help[]
# ---------------------------------------------------------------------------------------------------


class TestHelp:
    def test_format_help_Commands_IndentedUnderCountedHeader(self):
        text = format_help([
            "cc-devthrottle session list --fields id,name,state,repo,machine",
            'cc-devthrottle message send <id> "<message>"',
        ])
        assert text == (
            "help[2]:\n"
            "  cc-devthrottle session list --fields id,name,state,repo,machine\n"
            '  cc-devthrottle message send <id> "<message>"'
        )

    def test_format_help_Empty_Raises(self):
        with pytest.raises(ValueError):
            format_help([])

    @pytest.mark.parametrize("bad", ["two\nlines", "caf\u00e9", " padded", ""])
    def test_format_help_BadCommand_Raises(self, bad):
        with pytest.raises(ValueError):
            format_help([bad])


# ---------------------------------------------------------------------------------------------------
# --fields
# ---------------------------------------------------------------------------------------------------

VALID = ["id", "name", "state", "repo", "machine"]
DEFAULT = ["id", "name", "state", "repo"]


class TestFields:
    def test_parse_fields_NotGiven_ReturnsDefault(self):
        assert parse_fields(None, VALID, DEFAULT) == DEFAULT

    def test_parse_fields_Given_ReturnsInRequestedOrder(self):
        assert parse_fields("machine, id", VALID, DEFAULT) == ["machine", "id"]

    def test_parse_fields_Unknown_RaisesListingEveryValidName(self):
        with pytest.raises(FieldsError) as caught:
            parse_fields("id,bogus,nope", VALID, DEFAULT)
        message = str(caught.value)
        assert "bogus" in message and "nope" in message
        assert "id, name, state, repo, machine" in message

    @pytest.mark.parametrize("bad", ["", "id,", "id,,name", "id,id"])
    def test_parse_fields_EmptyOrRepeated_Raises(self, bad):
        with pytest.raises(FieldsError):
            parse_fields(bad, VALID, DEFAULT)

    def test_parse_fields_NonAsciiUnknown_MessageStaysAscii(self):
        with pytest.raises(FieldsError) as caught:
            parse_fields("caf\u00e9", VALID, DEFAULT)
        assert str(caught.value).isascii()
        assert "caf\\u00e9" in str(caught.value)

    def test_parse_fields_InvalidDefault_Raises(self):
        with pytest.raises(ValueError):
            parse_fields(None, VALID, ["id", "missing"])

    def test_parse_fields_or_exit_Unknown_ExitsTwoWithMessage(self):
        err = io.StringIO()
        with pytest.raises(SystemExit) as caught:
            parse_fields_or_exit("bogus", VALID, DEFAULT, err=err)
        assert caught.value.code == 2
        assert "bogus" in err.getvalue()
        assert "id, name, state, repo, machine" in err.getvalue()

    def test_parse_fields_or_exit_InsideTyperCommand_ExitsTwoListingValidNames(self):
        app = typer.Typer()

        @app.command()
        def listing(fields: str = typer.Option(None, "--fields")):
            chosen = parse_fields_or_exit(fields, VALID, DEFAULT)
            typer.echo(",".join(chosen))

        runner = CliRunner()
        bad = runner.invoke(app, ["--fields", "id,bogus"])
        assert bad.exit_code == 2
        combined = bad.output + (bad.stderr if bad.stderr_bytes is not None else "")
        assert "bogus" in combined
        assert "id, name, state, repo, machine" in combined

        good = runner.invoke(app, ["--fields", "name,id"])
        assert good.exit_code == 0
        assert good.output == "name,id\n"
