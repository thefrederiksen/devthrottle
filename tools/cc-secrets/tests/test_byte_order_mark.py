"""Issue 3420: Windows PowerShell pipes text with a byte-order mark (U+FEFF) in front. `add` stored it as part of
the value, and `list` then crashed on a cp1252 console with UnicodeEncodeError."""

from typer.testing import CliRunner

from conftest import add_entry, run_entry_point
from src import cli

runner = CliRunner()
BOM = "\ufeff"
VOICE_ID = "voice-id-1234567890"


def test_AddSetting_PipedWithAByteOrderMark_StoresTheValueWithoutIt(store):
    result = runner.invoke(cli.app, ["add", "voice-id", "--username", "", "--domains", "", "--agents", "--setting",
                                     "--env-name", "VOICE_ID"], input=BOM + VOICE_ID + "\r\n")

    assert result.exit_code == 0, result.output
    assert store.get("voice-id").secret.reveal() == VOICE_ID


def test_List_OnACp1252Console_AStoredByteOrderMark_IsShownAsAnEscape_AndDoesNotCrash(store, home):
    add_entry(store, name="voice-id", notes=BOM + "imported through PowerShell")

    result = run_entry_point(["list"], {"CC_SECRETS_HOME": str(home), "PYTHONIOENCODING": "cp1252", "COLUMNS": "300"})

    assert result.returncode == 0, result.stderr.decode("cp1252", "replace")
    assert b"UnicodeEncodeError" not in result.stderr
    assert b"\\ufeffimported through PowerShell" in result.stdout
