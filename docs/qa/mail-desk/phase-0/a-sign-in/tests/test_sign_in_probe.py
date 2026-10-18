"""Tests for definition/checks/sign_in_probe.py: the sign-in lookup's three cases (none, exactly one, more than one),
cc-outlook's file naming rule, and the Graph guard.

Every test runs with every CC_ variable removed, as a check is run, plus only CC_CHECK_STATE_FILE where main() needs
it. The base folders are scratch folders holding dummy files; the real saved sign-in is never read or copied.

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-0/a-sign-in/tests -v
"""

import importlib.util
import sys
import json
import os
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[5]
CHECK = HERE.parent / "definition" / "checks" / "sign_in_probe.py"
AUTH = REPO / "tools" / "cc-outlook" / "src" / "auth.py"


sys.dont_write_bytecode = True  # a __pycache__ beside the check would end up in the published definition


def load_check():
    spec = importlib.util.spec_from_file_location("sign_in_probe", CHECK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(autouse=True)
def no_cc_variables(monkeypatch):
    for name in [n for n in os.environ if n.startswith("CC_")]:
        monkeypatch.delenv(name)


@pytest.fixture
def probe():
    return load_check()


def plant(base, instance, name="email_devthrottle_com_msal.json"):
    """A dummy cache file where cc-outlook would save one, under one instance folder of a scratch base."""
    folder = base / "instances" / instance / "config" / "outlook" / "tokens"
    folder.mkdir(parents=True)
    path = folder / name
    path.write_text("{}", encoding="utf-8")
    return path


class NoMsal:
    """Stands in for msal.PublicClientApplication: fails the test if the check gets that far."""

    def __init__(self, *args, **kwargs):
        raise AssertionError("the check reached the sign-in library although the lookup should have stopped it")


class EmptyApp:
    """A sign-in library that holds no account: lets a test see which file the check opened, and nothing more."""

    def __init__(self, client_id, authority, token_cache):
        assert authority == "https://login.microsoftonline.com/common"

    def get_accounts(self, username=None):
        return []


def run_main(probe, monkeypatch, tmp_path, home):
    state = tmp_path / "state"
    state.write_text("", encoding="utf-8")
    monkeypatch.setenv("CC_CHECK_STATE_FILE", str(state))
    monkeypatch.setenv("HOME", str(tmp_path / "scratch-home"))
    monkeypatch.setattr(probe, "account_record_home", lambda: str(home))
    with pytest.raises(SystemExit) as exit_info:
        probe.main()
    return exit_info.value.code, json.loads(state.read_text(encoding="utf-8"))["runs"][-1]


# ---------------------------------------------------------------------------------------------- the file name


def test_CacheFileName_TheMailbox_IsCcOutlooksName(probe):
    assert probe.cache_file_name("email@devthrottle.com") == "email_devthrottle_com_msal.json"
    assert probe.CACHE_NAME == "email_devthrottle_com_msal.json"


def test_CacheFileName_TheRuleIsTheOneInCcOutlook(probe):
    source = AUTH.read_text(encoding="utf-8")
    assert "safe_name = email.replace('@', '_').replace('.', '_')" in source
    assert "return TOKENS_DIR / f'{safe_name}_msal.json'" in source


# ---------------------------------------------------------------------------------------------- the lookup


def test_FindSignIn_NoInstanceFolder_FailsNamingTheFix(probe, tmp_path):
    path, why = probe.find_sign_in(tmp_path)
    assert path is None
    assert "not signed in" in why and "run cc-outlook -a devthrottle auth" in why
    assert str(tmp_path) in why


def test_FindSignIn_InstancesWithoutTheMailbox_FailsNamingTheFix(probe, tmp_path):
    plant(tmp_path, "default", name="someone_else_example_com_msal.json")
    plant(tmp_path, "second", name="email@devthrottle.com_msal.json")  # the name with "@" is not cc-outlook's
    path, why = probe.find_sign_in(tmp_path)
    assert path is None
    assert "run cc-outlook -a devthrottle auth" in why


def test_FindSignIn_ExactlyOne_ReturnsIt(probe, tmp_path):
    planted = plant(tmp_path, "default")
    path, why = probe.find_sign_in(tmp_path)
    assert why is None
    assert path == planted


def test_FindSignIn_MoreThanOne_FailsNamingEveryPathAndPicksNone(probe, tmp_path):
    first = plant(tmp_path, "default")
    second = plant(tmp_path, "slot5")
    path, why = probe.find_sign_in(tmp_path)
    assert path is None
    assert "more than once" in why
    assert str(first) in why and str(second) in why


def test_FindSignIn_ACacheOnlyInTheOldFallbackFolder_IsNotFound(probe, tmp_path):
    old = tmp_path / ".cc-director" / "config" / "outlook" / "tokens"
    old.mkdir(parents=True)
    (old / "email_devthrottle_com_msal.json").write_text("{}", encoding="utf-8")
    path, why = probe.find_sign_in(probe.director_base(tmp_path))
    assert path is None and "not signed in" in why


@pytest.mark.skipif(os.name == "nt", reason="the Linux and macOS rule")
def test_DirectorBase_LinuxOrMac_IsUnderTheGivenHomeNotHome(probe, tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", "/nowhere")
    base = probe.director_base(tmp_path)
    assert str(base).startswith(str(tmp_path))
    assert base.name == "cc-director"


# ---------------------------------------------------------------------------------------------- the whole check


def test_Main_NoSignIn_ExitsOneWithTheFixAndNeverReachesTheSignInLibrary(probe, monkeypatch, tmp_path):
    home = tmp_path / "account-home"
    monkeypatch.setattr(probe.msal, "PublicClientApplication", NoMsal)
    code, run = run_main(probe, monkeypatch, tmp_path, home)
    assert code == 1
    assert "not signed in" in run["result"] and "run cc-outlook -a devthrottle auth" in run["result"]
    assert run["ccVariableNames"] == ["CC_CHECK_STATE_FILE"]


def test_Main_TwoSignIns_ExitsOneNamingBothAndNeverReachesTheSignInLibrary(probe, monkeypatch, tmp_path):
    home = tmp_path / "account-home"
    base = probe.director_base(home)
    first, second = plant(base, "default"), plant(base, "other")
    monkeypatch.setattr(probe.msal, "PublicClientApplication", NoMsal)
    code, run = run_main(probe, monkeypatch, tmp_path, home)
    assert code == 1
    assert str(first) in run["result"] and str(second) in run["result"]
    assert sorted(run["fromAccountRecord"]["found"]) == sorted([str(first), str(second)])


def test_Main_ExactlyOne_OpensThatFileAndNotTheOneUnderHome(probe, monkeypatch, tmp_path):
    home = tmp_path / "account-home"
    planted = plant(probe.director_base(home), "default")
    plant(probe.director_base(tmp_path / "scratch-home"), "decoy")
    monkeypatch.setattr(probe.msal, "PublicClientApplication", EmptyApp)
    code, run = run_main(probe, monkeypatch, tmp_path, home)
    assert code == 1  # the dummy cache holds no account, so the check stops there, after choosing the file
    assert run["fromAccountRecord"]["found"] == [str(planted)]
    assert str(planted) in run["result"] and "holds no account" in run["result"]


# ---------------------------------------------------------------------------------------------- the guard


@pytest.mark.parametrize("method", ["POST", "PATCH", "PUT", "DELETE"])
def test_GraphCall_AnythingButGet_IsRefusedBeforeAnyRequest(probe, method, monkeypatch):
    monkeypatch.setattr(probe.urllib.request, "urlopen", NoMsal)
    with pytest.raises(probe.RefusedCall):
        probe.graph_call(method, "https://graph.microsoft.com/v1.0/me/messages", "unused")


def test_GraphGet_AnotherHost_IsRefusedBeforeAnyRequest(probe, monkeypatch):
    monkeypatch.setattr(probe.urllib.request, "urlopen", NoMsal)
    with pytest.raises(probe.RefusedCall):
        probe.graph_get("https://example.com/v1.0/me", "unused")
