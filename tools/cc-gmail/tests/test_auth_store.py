"""Tests for where cc-gmail keeps its OAuth store, and for honest account status.

Issue #3011: the store resolved through CcStorage.tool_config(), which honours
CC_DIRECTOR_ROOT. A Director sets that variable to its instance home for every
session it runs and a plain terminal sets nothing, so there were two independent
stores - and the tool's own "Re-authenticate by running: cc-gmail auth" was an
instruction that could not reach the store the session was reading. On top of
that, 'accounts list' called an account Ready whenever a token FILE existed,
which is how a token Google had already revoked kept reporting Ready.
"""

import json
import os

import pytest
from google.auth.exceptions import RefreshError

from src import auth


@pytest.fixture
def user_base(tmp_path, monkeypatch):
    """Point the per-user store at a throwaway directory, and forget past adoptions."""
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.delenv("CC_DIRECTOR_ROOT", raising=False)
    monkeypatch.setattr(auth, "_adopted_legacy_stores", False)
    monkeypatch.setattr(auth, "_adoption_notes", [])
    return tmp_path


def write_account(store, account, token=None, credentials=True, config=None):
    """Put an account on disk the way a real setup leaves it."""
    account_dir = store / "accounts" / account
    account_dir.mkdir(parents=True, exist_ok=True)
    if credentials:
        (account_dir / "credentials.json").write_text(json.dumps({"installed": {"client_id": "x"}}))
    if token is not None:
        (account_dir / "token.json").write_text(json.dumps(token))
    (account_dir / "config.json").write_text(json.dumps(config or {"auth_method": "oauth", "email": f"{account}@example.com"}))
    return account_dir


def a_usable_token():
    return {"token": "at", "refresh_token": "rt", "client_id": "cid", "client_secret": "cs"}


# -- One store per user -------------------------------------------------------


class TestOneStorePerUser:
    def test_store_is_the_same_inside_and_outside_a_director_session(self, user_base, monkeypatch):
        outside = auth.config_dir_path()

        monkeypatch.setenv("CC_DIRECTOR_ROOT", str(user_base / "cc-director" / "instances" / "default"))
        inside = auth.config_dir_path()

        assert inside == outside, (
            "CC_DIRECTOR_ROOT moved the token store, so authenticating in a terminal "
            "cannot fix a Director session (issue #3011)"
        )

    def test_store_sits_under_the_per_user_cc_director_directory(self, user_base):
        assert auth.config_dir_path() == user_base / "cc-director" / "config" / "gmail"
        assert auth.get_token_path("personal") == (
            user_base / "cc-director" / "config" / "gmail" / "accounts" / "personal" / "token.json"
        )

    def test_token_written_in_a_session_is_read_from_a_terminal(self, user_base, monkeypatch):
        """The whole point: one write, both readers."""
        monkeypatch.setenv("CC_DIRECTOR_ROOT", str(user_base / "cc-director" / "instances" / "default"))
        in_session = auth.get_token_path("personal")
        in_session.parent.mkdir(parents=True, exist_ok=True)
        in_session.write_text(json.dumps(a_usable_token()))

        monkeypatch.delenv("CC_DIRECTOR_ROOT")
        assert auth.get_token_path("personal").read_text() == in_session.read_text()


# -- Adopting the older per-instance stores -----------------------------------


class TestAdoptLegacyStores:
    def test_adopts_an_account_that_exists_only_in_an_instance_home(self, user_base):
        legacy = user_base / "cc-director" / "instances" / "default" / "config" / "gmail"
        write_account(legacy, "cottage", token=a_usable_token())

        notes = auth.adopt_legacy_stores()

        adopted_token = auth.get_token_path("cottage")
        assert adopted_token.exists()
        assert json.loads(adopted_token.read_text())["refresh_token"] == "rt"
        assert any("cottage" in note for note in notes)
        assert str(legacy) in notes[0], "the note has to say where the account came from"

    def test_leaves_the_legacy_copy_in_place(self, user_base):
        legacy = user_base / "cc-director" / "instances" / "default" / "config" / "gmail"
        legacy_dir = write_account(legacy, "cottage", token=a_usable_token())

        auth.adopt_legacy_stores()

        assert (legacy_dir / "token.json").exists(), "adoption copies, it never deletes"

    def test_never_overwrites_an_account_this_store_already_has(self, user_base):
        mine = auth.config_dir_path()
        write_account(mine, "personal", token={"token": "the_good_one", "refresh_token": "rt"})
        legacy = user_base / "cc-director" / "instances" / "default" / "config" / "gmail"
        write_account(legacy, "personal", token={"token": "the_stale_one", "refresh_token": "rt"})

        notes = auth.adopt_legacy_stores()

        assert json.loads(auth.get_token_path("personal").read_text())["token"] == "the_good_one"
        assert notes == []

    def test_prefers_the_instance_store_with_the_newest_token(self, user_base):
        older = user_base / "cc-director" / "instances" / "alpha" / "config" / "gmail"
        newer = user_base / "cc-director" / "instances" / "beta" / "config" / "gmail"
        older_dir = write_account(older, "personal", token={"token": "older", "refresh_token": "rt"})
        newer_dir = write_account(newer, "personal", token={"token": "newer", "refresh_token": "rt"})
        os.utime(older_dir / "token.json", (1_600_000_000, 1_600_000_000))
        os.utime(newer_dir / "token.json", (1_700_000_000, 1_700_000_000))

        auth.adopt_legacy_stores()

        assert json.loads(auth.get_token_path("personal").read_text())["token"] == "newer"

    def test_runs_once_per_process(self, user_base):
        legacy = user_base / "cc-director" / "instances" / "default" / "config" / "gmail"
        write_account(legacy, "cottage", token=a_usable_token())

        first = auth.adopt_legacy_stores()
        auth.get_token_path("cottage").write_text(json.dumps({"token": "changed_since"}))
        second = auth.adopt_legacy_stores()

        assert first == second, "the notes stay readable for whichever command wants to print them"
        assert json.loads(auth.get_token_path("cottage").read_text())["token"] == "changed_since"

    def test_ignores_a_legacy_directory_that_holds_no_token_and_no_credentials(self, user_base):
        legacy = user_base / "cc-director" / "instances" / "default" / "config" / "gmail"
        (legacy / "accounts" / "leftover").mkdir(parents=True)

        notes = auth.adopt_legacy_stores()

        assert notes == []
        assert not (auth.accounts_dir_path() / "leftover").exists()

    def test_does_nothing_when_there_is_no_legacy_store(self, user_base):
        assert auth.adopt_legacy_stores() == []
        assert auth.list_accounts() == []

    def test_the_current_store_is_never_treated_as_legacy(self, user_base, monkeypatch):
        """With no CC_DIRECTOR_ROOT set, tool_config() IS the per-user store."""
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())

        assert auth.legacy_store_dirs() == []


# -- Honest status ------------------------------------------------------------


class TestHonestStatus:
    def test_a_token_with_a_refresh_token_is_ready(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())

        described = auth.describe_token("personal")

        assert described["state"] == "ready"
        assert described["status"] == "Ready"

    def test_a_refused_refresh_is_never_ready(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())
        auth.record_token_failure("personal", "invalid_grant: Token has been expired or revoked.")

        described = auth.describe_token("personal")
        listed = auth.list_accounts()[0]

        assert described["state"] == "refresh_failed"
        assert described["status"] == "Re-auth needed"
        assert "invalid_grant" in described["detail"]
        assert str(auth.get_token_path("personal")) in described["detail"]
        assert listed["status"] == "Re-auth needed"
        assert listed["authenticated"] is False

    def test_a_token_that_cannot_be_renewed_is_never_ready(self, user_base):
        write_account(auth.config_dir_path(), "personal", token={"token": "at", "client_id": "cid"})

        described = auth.describe_token("personal")

        assert described["state"] == "no_refresh_token"
        assert described["status"] == "Re-auth needed"

    def test_a_missing_token_says_setup_needed(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=None)

        described = auth.describe_token("personal")

        assert described["state"] == "no_token"
        assert str(auth.get_token_path("personal")) in described["detail"]

    def test_a_missing_credentials_file_says_setup_needed(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token(), credentials=False)

        assert auth.describe_token("personal")["state"] == "no_credentials"

    def test_an_unreadable_token_says_so_rather_than_raising(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())
        auth.get_token_path("personal").write_text("{not json")

        described = auth.describe_token("personal")

        assert described["state"] == "unreadable"
        assert described["status"] == "Re-auth needed"

    def test_an_app_password_account_reports_its_own_state(self, user_base, monkeypatch):
        write_account(
            auth.config_dir_path(), "work", token=None, credentials=False,
            config={"auth_method": "app_password", "email": "work@example.com"},
        )
        monkeypatch.setattr(auth, "get_app_password", lambda account: None)

        listed = auth.list_accounts()[0]

        assert listed["status"] == "Setup needed"
        assert listed["authenticated"] is False


# -- The refresh-failure record -----------------------------------------------


class TestRefreshFailureRecord:
    def test_a_refused_refresh_is_written_down(self, user_base, monkeypatch):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())

        class RefusingCredentials:
            expired = True
            refresh_token = "rt"
            valid = False

            def refresh(self, request):
                raise RefreshError("invalid_grant: Token has been expired or revoked.")

        monkeypatch.setattr(
            auth.Credentials, "from_authorized_user_file",
            staticmethod(lambda path, scopes: RefusingCredentials()),
        )

        assert auth.load_credentials("personal") is None

        recorded = auth.read_token_failure("personal")
        assert recorded is not None
        assert "invalid_grant" in recorded["error"]
        assert recorded["token_path"] == str(auth.get_token_path("personal"))

    def test_saving_a_token_clears_the_record(self, user_base):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())
        auth.record_token_failure("personal", "invalid_grant: Token has been expired or revoked.")

        class FreshCredentials:
            def to_json(self):
                return json.dumps(a_usable_token())

        auth.save_credentials("personal", FreshCredentials())

        assert auth.read_token_failure("personal") is None
        assert auth.describe_token("personal")["state"] == "ready"

    def test_the_non_interactive_error_names_the_token_it_read(self, user_base, monkeypatch):
        write_account(auth.config_dir_path(), "personal", token=a_usable_token())

        class RefusingCredentials:
            expired = True
            refresh_token = "rt"
            valid = False

            def refresh(self, request):
                raise RefreshError("invalid_grant: Token has been expired or revoked.")

        monkeypatch.setattr(
            auth.Credentials, "from_authorized_user_file",
            staticmethod(lambda path, scopes: RefusingCredentials()),
        )

        with pytest.raises(ValueError) as raised:
            auth.authenticate("personal", interactive=False)

        message = str(raised.value)
        assert str(auth.get_token_path("personal")) in message
        assert "invalid_grant" in message
        assert "auth --force" in message
