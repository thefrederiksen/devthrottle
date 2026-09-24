"""Tests for the guided setup: the fork, the download watch, and the proof.

The three things that decide whether a person gets a working mailbox or a
mystery: which consent-screen user type they are told to pick, whether the
client JSON they downloaded lands where the account reads it from, and whether
"it worked" is claimed on an absence of errors.
"""

import json
import os
import time

import pytest

from src import auth, setup_flow


# -- Which fork the address is on --------------------------------------------


class TestDetectFork:
    def test_gmail_address_is_personal_without_a_lookup(self):
        def no_lookups(domain):
            raise AssertionError("a gmail.com address needs no DNS lookup")

        assert setup_flow.detect_fork("someone@gmail.com", no_lookups) == setup_flow.FORK_PERSONAL

    def test_googlemail_address_is_personal(self):
        assert setup_flow.detect_fork("someone@googlemail.com", lambda d: []) == setup_flow.FORK_PERSONAL

    def test_a_domain_whose_mail_google_hosts_is_workspace(self):
        mx = ["10 aspmx.l.google.com.", "20 alt1.aspmx.l.google.com."]
        assert setup_flow.detect_fork("me@example.com", lambda d: mx) == setup_flow.FORK_WORKSPACE

    def test_googlemail_mx_also_means_workspace(self):
        assert setup_flow.detect_fork("me@example.com", lambda d: ["30 aspmx2.googlemail.com."]) == setup_flow.FORK_WORKSPACE

    def test_a_domain_google_does_not_host_is_a_personal_google_account(self):
        mx = ["10 example-com.mail.protection.outlook.com."]
        assert setup_flow.detect_fork("me@example.com", lambda d: mx) == setup_flow.FORK_PERSONAL

    def test_a_failed_lookup_is_unknown_and_never_guessed(self):
        def boom(domain):
            raise OSError("no network")

        assert setup_flow.detect_fork("me@example.com", boom) == setup_flow.FORK_UNKNOWN

    def test_a_lookup_with_no_answer_is_unknown(self):
        assert setup_flow.detect_fork("me@example.com", lambda d: []) == setup_flow.FORK_UNKNOWN

    def test_an_address_with_no_domain_is_unknown(self):
        assert setup_flow.detect_fork("not-an-address", lambda d: []) == setup_flow.FORK_UNKNOWN

    def test_a_google_lookalike_domain_is_not_workspace(self):
        # notgoogle.com ends in "google.com" only if you compare without the dot.
        mx = ["10 mx.notgoogle.com."]
        assert setup_flow.detect_fork("me@example.com", lambda d: mx) == setup_flow.FORK_PERSONAL


# -- Recognising the client JSON ---------------------------------------------


def write_client_json(directory, filename, top_key="installed", **overrides):
    body = {
        "client_id": "123-abc.apps.googleusercontent.com",
        "client_secret": "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inted",
        "project_id": "cc-gmail-000001",
        "redirect_uris": ["http://localhost"],
    }
    body.update(overrides)
    path = directory / filename
    path.write_text(json.dumps({top_key: body}))
    return path


class TestClassifyClientJson:
    def test_a_desktop_client_is_recognised_whatever_it_is_called(self, tmp_path):
        path = write_client_json(tmp_path, "renamed-by-the-user.json")
        kind, document = setup_flow.classify_client_json(path)
        assert kind == setup_flow.CLIENT_DESKTOP
        assert document["installed"]["project_id"] == "cc-gmail-000001"

    def test_a_web_client_is_recognised_as_the_wrong_type(self, tmp_path):
        path = write_client_json(tmp_path, "client_secret_web.json", top_key="web")
        kind, _ = setup_flow.classify_client_json(path)
        assert kind == setup_flow.CLIENT_WEB

    def test_a_file_named_like_a_client_but_shaped_otherwise_is_not_one(self, tmp_path):
        path = tmp_path / "client_secret_123.json"
        path.write_text(json.dumps({"installed": {"client_id": "only-an-id"}}))
        assert setup_flow.classify_client_json(path) is None

    def test_an_unrelated_json_file_is_not_a_client(self, tmp_path):
        path = tmp_path / "package.json"
        path.write_text(json.dumps({"name": "something", "version": "1.0.0"}))
        assert setup_flow.classify_client_json(path) is None

    def test_a_file_that_is_not_json_is_not_a_client(self, tmp_path):
        path = tmp_path / "notes.json"
        path.write_text("this is not json {{{")
        assert setup_flow.classify_client_json(path) is None

    def test_a_token_file_is_not_mistaken_for_a_client(self, tmp_path):
        # token.json carries a client_id and client_secret too, but not nested
        # under "installed" - matching on the nesting is what keeps them apart.
        path = tmp_path / "token.json"
        path.write_text(json.dumps({"token": "at", "refresh_token": "rt", "client_id": "cid", "client_secret": "cs"}))
        assert setup_flow.classify_client_json(path) is None

    def test_a_huge_file_is_not_read(self, tmp_path):
        path = tmp_path / "big.json"
        path.write_text(json.dumps({"installed": {"client_id": "a", "client_secret": "b", "pad": "x" * 70000}}))
        assert setup_flow.classify_client_json(path) is None

    def test_a_missing_file_is_not_a_client(self, tmp_path):
        assert setup_flow.classify_client_json(tmp_path / "gone.json") is None


class TestDescribeClientJson:
    def test_the_client_secret_is_never_in_what_is_shown(self, tmp_path):
        path = write_client_json(tmp_path, "c.json")
        _, document = setup_flow.classify_client_json(path)
        shown = json.dumps(setup_flow.describe_client_json(document))
        assert "GOCSPX" not in shown
        assert "secret" not in shown.lower()

    def test_it_names_the_project_and_the_tail_of_the_client_id(self, tmp_path):
        path = write_client_json(tmp_path, "c.json")
        _, document = setup_flow.classify_client_json(path)
        facts = setup_flow.describe_client_json(document)
        assert facts["project_id"] == "cc-gmail-000001"
        assert facts["client_id_tail"] in "123-abc.apps.googleusercontent.com"

    def test_a_file_without_a_project_id_says_so_rather_than_inventing_one(self, tmp_path):
        path = tmp_path / "c.json"
        path.write_text(json.dumps({"installed": {"client_id": "a", "client_secret": "b"}}))
        _, document = setup_flow.classify_client_json(path)
        assert setup_flow.describe_client_json(document)["project_id"] == "(not in the file)"


# -- Finding the download ----------------------------------------------------


class TestFindClientJsons:
    def test_it_finds_a_client_downloaded_after_the_watch_started(self, tmp_path):
        started = time.time()
        path = write_client_json(tmp_path, "client_secret_123.json")
        os.utime(path, (started + 1, started + 1))
        assert setup_flow.find_client_jsons([tmp_path], started) == [path]

    def test_it_ignores_a_client_that_was_already_there(self, tmp_path):
        old = write_client_json(tmp_path, "an-old-client.json")
        started = time.time()
        os.utime(old, (started - 3600, started - 3600))
        assert setup_flow.find_client_jsons([tmp_path], started) == []

    def test_it_ignores_a_web_client_when_asked_for_a_desktop_one(self, tmp_path):
        started = time.time() - 10
        write_client_json(tmp_path, "web.json", top_key="web")
        assert setup_flow.find_client_jsons([tmp_path], started) == []

    def test_it_returns_the_newest_first(self, tmp_path):
        started = time.time() - 100
        first = write_client_json(tmp_path, "first.json")
        second = write_client_json(tmp_path, "second.json")
        os.utime(first, (started + 1, started + 1))
        os.utime(second, (started + 2, started + 2))
        assert setup_flow.find_client_jsons([tmp_path], started)[0] == second

    def test_a_missing_directory_is_skipped_not_raised(self, tmp_path):
        assert setup_flow.find_client_jsons([tmp_path / "nope"], 0) == []


class TestWatchForClientJson:
    def test_it_returns_as_soon_as_the_file_appears(self, tmp_path):
        clock = {"now": 0.0}
        appeared = {}

        def sleep(seconds):
            clock["now"] += seconds
            if clock["now"] >= 3 and "path" not in appeared:
                path = write_client_json(tmp_path, "client_secret_123.json")
                os.utime(path, (time.time(), time.time()))
                appeared["path"] = path

        found = setup_flow.watch_for_client_json(
            [tmp_path],
            modified_after=time.time() - 5,
            timeout=30,
            sleep=sleep,
            now=lambda: clock["now"],
        )
        assert found == appeared["path"]

    def test_it_gives_up_at_the_timeout_rather_than_hanging(self, tmp_path):
        clock = {"now": 0.0}

        def sleep(seconds):
            clock["now"] += seconds

        found = setup_flow.watch_for_client_json(
            [tmp_path],
            modified_after=0,
            timeout=5,
            sleep=sleep,
            now=lambda: clock["now"],
        )
        assert found is None

    def test_a_web_client_is_reported_once_instead_of_silently_ignored(self, tmp_path):
        clock = {"now": 0.0}
        seen = []
        write_client_json(tmp_path, "web.json", top_key="web")

        def sleep(seconds):
            clock["now"] += seconds

        setup_flow.watch_for_client_json(
            [tmp_path],
            modified_after=0,
            timeout=5,
            sleep=sleep,
            now=lambda: clock["now"],
            on_wrong_type=seen.append,
        )
        assert [p.name for p in seen] == ["web.json"]


# -- Installing it where the account reads it from ---------------------------


class TestInstallClientJson:
    def test_it_lands_on_the_path_auth_resolves_for_the_account(self, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "appdata"))
        monkeypatch.delenv("CC_DIRECTOR_ROOT", raising=False)
        monkeypatch.setattr(auth, "_adopted_legacy_stores", True)

        source = write_client_json(tmp_path, "client_secret_123.json")
        destination = auth.get_credentials_path("proof")

        setup_flow.install_client_json(source, destination)

        assert destination.exists()
        assert json.loads(destination.read_text()) == json.loads(source.read_text())

    def test_the_bytes_are_copied_unchanged(self, tmp_path):
        source = write_client_json(tmp_path, "c.json")
        destination = tmp_path / "account" / "credentials.json"
        setup_flow.install_client_json(source, destination)
        assert destination.read_bytes() == source.read_bytes()

    @pytest.mark.skipif(os.name != "posix", reason="POSIX file modes")
    def test_the_installed_file_is_readable_only_by_its_owner(self, tmp_path):
        source = write_client_json(tmp_path, "c.json")
        destination = tmp_path / "account" / "credentials.json"
        setup_flow.install_client_json(source, destination)
        assert oct(destination.stat().st_mode & 0o777) == "0o600"

    def test_it_refuses_a_file_that_is_not_a_client_json(self, tmp_path):
        source = tmp_path / "notes.json"
        source.write_text(json.dumps({"hello": "world"}))
        with pytest.raises(setup_flow.SetupError):
            setup_flow.install_client_json(source, tmp_path / "account" / "credentials.json")

    def test_nothing_is_written_when_the_source_is_refused(self, tmp_path):
        source = tmp_path / "notes.json"
        source.write_text("{}")
        destination = tmp_path / "account" / "credentials.json"
        with pytest.raises(setup_flow.SetupError):
            setup_flow.install_client_json(source, destination)
        assert not destination.exists()


# -- The proof ---------------------------------------------------------------


class FakeGmail:
    def __init__(self, profile, labels):
        self._profile = profile
        self._labels = labels

    def get_profile(self):
        return self._profile

    def list_labels(self):
        return self._labels


class TestVerifyGmail:
    def test_a_real_answer_is_reported_with_what_came_back(self):
        client = FakeGmail({"emailAddress": "me@example.com", "messagesTotal": 42}, [{"id": "INBOX"}, {"id": "SENT"}])
        facts = setup_flow.verify_gmail(client)
        assert facts == {"email": "me@example.com", "messages_total": 42, "labels": 2}

    def test_an_empty_label_list_is_a_broken_instrument_not_a_clean_run(self):
        client = FakeGmail({"emailAddress": "me@example.com"}, [])
        with pytest.raises(setup_flow.SetupError, match="broken answer"):
            setup_flow.verify_gmail(client)

    def test_a_profile_without_an_address_is_not_proof(self):
        client = FakeGmail({}, [{"id": "INBOX"}])
        with pytest.raises(setup_flow.SetupError, match="no email address"):
            setup_flow.verify_gmail(client)

    def test_a_profile_of_none_is_not_proof(self):
        client = FakeGmail(None, [{"id": "INBOX"}])
        with pytest.raises(setup_flow.SetupError):
            setup_flow.verify_gmail(client)


class TestVerifyCalendarAndContacts:
    def test_at_least_one_calendar_is_the_proof(self):
        class Calendar:
            def list_calendars(self):
                return [{"id": "primary"}]

        assert setup_flow.verify_calendar(Calendar()) == {"calendars": 1}

    def test_no_calendars_at_all_is_a_broken_answer(self):
        class Calendar:
            def list_calendars(self):
                return []

        with pytest.raises(setup_flow.SetupError, match="broken answer"):
            setup_flow.verify_calendar(Calendar())

    def test_zero_contacts_is_a_real_answer_for_a_fresh_account(self):
        class Contacts:
            def list_contacts(self, max_results=100):
                return []

        assert setup_flow.verify_contacts(Contacts()) == {"contacts_sampled": 0}

    def test_something_that_is_not_a_list_is_not_an_answer(self):
        class Contacts:
            def list_contacts(self, max_results=100):
                return None

        with pytest.raises(setup_flow.SetupError):
            setup_flow.verify_contacts(Contacts())


# -- How long the token will last --------------------------------------------


class TestTokenLifetimeNote:
    def test_an_unpublished_external_app_is_warned_about_the_seven_days(self):
        severity, note = setup_flow.token_lifetime_note(setup_flow.USER_TYPE_EXTERNAL, False, "personal")
        assert severity == "warning"
        assert "7 days" in note
        assert "cc-gmail --account personal auth --force" in note

    def test_an_external_app_whose_publishing_was_never_established_is_warned_too(self):
        severity, _ = setup_flow.token_lifetime_note(setup_flow.USER_TYPE_EXTERNAL, None, "personal")
        assert severity == "warning"

    def test_a_published_external_app_is_told_the_expiry_does_not_apply(self):
        severity, note = setup_flow.token_lifetime_note(setup_flow.USER_TYPE_EXTERNAL, True, "personal")
        assert severity == "ok"
        assert "In production" in note

    def test_an_internal_app_is_never_warned_about_publishing(self):
        for published in (None, False, True):
            severity, note = setup_flow.token_lifetime_note(setup_flow.USER_TYPE_INTERNAL, published, "work")
            assert severity == "ok"
            assert "no 7-day token expiry" in note

    def test_the_warning_names_the_account_it_is_about(self):
        _, note = setup_flow.token_lifetime_note(setup_flow.USER_TYPE_EXTERNAL, False, "second-mailbox")
        assert "second-mailbox" in note
