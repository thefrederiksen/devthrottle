"""Tests for the cc-devthrottle skill command group - the central skill library
(devthrottle_internal issue 995).

The Gateway is mocked at the requests layer (the endpoint behavior is proven by the Gateway's own
suite); what these tests pin is the command line's half of the contract, and two properties in
particular:

  1. `skill get` prints the body VERBATIM. That output goes straight into an agent's context, so
     anything the command adds, wraps, or reformats is text the agent will treat as instructions.
  2. There is NO offline fallback. An unreachable Gateway fails the command and says so, including
     the instruction not to proceed from memory - a stale skill that looks current is worse than a
     missing one that announces itself.
"""

import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import requests
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import skill_ops  # noqa: E402
from src.cli import app  # noqa: E402

# A WIDE terminal: Rich wraps to the terminal width, and the default test width breaks messages
# and table cells mid-word. These tests assert on CONTENT, so the width must not be part of it.
runner = CliRunner(env={"COLUMNS": "200"})


def _flat(text: str) -> str:
    """Collapse whitespace so an assertion about wording is not an assertion about line wrapping."""
    return " ".join(text.split())


def _fake_response(status_code: int, json_body=None, text: str = "") -> MagicMock:
    """A stand-in for a real response, INCLUDING its Content-Type. The header is not decoration: the
    client asserts it, because a Gateway from before the skill library answers 200 with its web app
    page and a fake without headers would let that slip through untested."""
    resp = MagicMock(spec=requests.Response)
    resp.status_code = status_code
    resp.content = b"x" if (json_body is not None or text) else b""
    resp.text = text
    resp.headers = {"Content-Type": "text/markdown" if (text and json_body is None) else "application/json"}
    if json_body is not None:
        resp.json.return_value = json_body
    else:
        resp.json.side_effect = ValueError("no json")
    return resp


HEAD = {
    "id": "move-session",
    "name": "Move a session",
    "summary": "Relocate a live session to another Director.",
    "triggers": ["move session", "migrate session"],
    "version": 5,
    "isBuiltIn": True,
    "hasDraft": False,
    "contentHash": "bundle-hash-5",
    "fileCount": 0,
    "enabled": True,
    "editable": False,
}

BODY = "# Move Session\n\nRelocate a live session. The handover goes through the GATEWAY.\n"

DETAIL_WITH_FILES = {
    "skillId": "with-files",
    "version": 3,
    "status": "published",
    "name": "With files",
    "summary": "Carries a helper.",
    "triggers": ["run the helper"],
    "bodyMarkdown": "# With files\n\nRun helper.py.\n",
    "files": [{"fileName": "helper.py", "contentHash": "abc", "content": "print('hello')\n",
               "encoding": "utf8", "executable": False}],
    "license": None,
    "compatibility": None,
    "allowedTools": None,
    "metadata": {},
    "contentHash": "bundle-hash-3",
    "authoredBy": "session:test",
    "changeNote": None,
    "createdUtc": "2026-09-01T00:00:00Z",
    "publishedUtc": "2026-09-01T00:00:00Z",
}


class TestActionsDiscoverability:
    def test_actions_json_lists_the_skill_commands(self):
        result = runner.invoke(app, ["actions", "--json"])
        assert result.exit_code == 0
        ids = {action["id"] for action in json.loads(result.output)["actions"]}
        assert {"skill-list", "skill-get", "skill-publish", "skill-clone"} <= ids

    def test_skill_get_is_not_marked_as_mutating(self):
        result = runner.invoke(app, ["actions", "--json"])
        actions = {a["id"]: a for a in json.loads(result.output)["actions"]}
        assert actions["skill-get"]["mutatesState"] is False
        assert actions["skill-publish"]["mutatesState"] is True


class TestGet:
    def test_prints_the_body_verbatim(self, capsys):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.side_effect = [
                _fake_response(200, HEAD),
                _fake_response(200, text=BODY),
            ]
            skill_ops.get_skill("move-session", None)

        # Byte for byte: no banner, no wrapping, no rendering. What the Gateway holds is what the
        # agent reads.
        assert capsys.readouterr().out == BODY

    def test_resolves_the_current_version_from_the_gateway_every_time(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.side_effect = [
                _fake_response(200, HEAD),
                _fake_response(200, text=BODY),
            ]
            skill_ops.get_skill("move-session", None)

        # The head read is what makes a fetch always current: there is no cached version number to
        # go stale, so a skill published a second ago is what the next fetch returns.
        assert request.call_args_list[0][0][1] == "/gateway/skills/move-session"
        assert request.call_args_list[1][0][1] == "/gateway/skills/move-session/body?version=5"

    def test_a_pinned_version_is_requested_as_asked(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.side_effect = [
                _fake_response(200, dict(DETAIL_WITH_FILES, skillId="move-session", version=2, files=[])),
                _fake_response(200, text=BODY),
            ]
            skill_ops.get_skill("move-session", 2)

        assert request.call_args_list[0][0][1] == "/gateway/skills/move-session/versions/2"
        assert request.call_args_list[1][0][1] == "/gateway/skills/move-session/body?version=2"

    def test_supporting_files_are_written_and_their_paths_printed(self, tmp_path, capsys, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        head = dict(HEAD, id="with-files", version=3, fileCount=1)
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.side_effect = [
                _fake_response(200, head),
                _fake_response(200, text=DETAIL_WITH_FILES["bodyMarkdown"]),
                _fake_response(200, DETAIL_WITH_FILES),
            ]
            skill_ops.get_skill("with-files", None)

        out = capsys.readouterr().out
        written = tmp_path / "cc-director" / "skills" / "with-files" / "3" / "helper.py"
        # A script has to exist on disk to be run, so the file lands and its ABSOLUTE path is named.
        assert written.is_file()
        assert written.read_text(encoding="utf-8") == "print('hello')\n"
        assert str(written) in out
        assert out.startswith(DETAIL_WITH_FILES["bodyMarkdown"])

    def test_the_file_cache_is_keyed_by_version_so_versions_never_collide(self, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        v3 = dict(DETAIL_WITH_FILES)
        v4 = dict(
            DETAIL_WITH_FILES,
            version=4,
            contentHash="bundle-hash-4",
            files=[{"fileName": "helper.py", "contentHash": "def", "content": "print('newer')\n",
                    "encoding": "utf8", "executable": False}],
        )

        skill_ops._materialize("with-files", 3, v3)
        skill_ops._materialize("with-files", 4, v4)

        root = tmp_path / "cc-director" / "skills" / "with-files"
        assert (root / "3" / "helper.py").read_text(encoding="utf-8") == "print('hello')\n"
        assert (root / "4" / "helper.py").read_text(encoding="utf-8") == "print('newer')\n"

    def test_a_half_deleted_cache_is_rewritten_not_reported_as_intact(self, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        skill_ops._materialize("with-files", 3, DETAIL_WITH_FILES)
        written = tmp_path / "cc-director" / "skills" / "with-files" / "3" / "helper.py"
        written.unlink()

        skill_ops._materialize("with-files", 3, DETAIL_WITH_FILES)

        # The hash sidecar alone is not proof the bundle is there - every listed file must exist, or
        # an agent is handed a path to a file that was deleted underneath it.
        assert written.is_file()

    def test_an_unsafe_server_supplied_file_name_is_refused(self, tmp_path, monkeypatch):
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        evil = dict(
            DETAIL_WITH_FILES,
            files=[{"fileName": "../escape.py", "contentHash": "x", "content": "bad",
                    "encoding": "utf8", "executable": False}],
        )

        try:
            skill_ops._materialize("with-files", 3, evil)
            assert False, "an unsafe file name must be refused"
        except skill_ops.GatewayError as ex:
            assert "unsafe file path" in str(ex)
        assert not (tmp_path / "cc-director" / "skills" / "escape.py").exists()


class TestNoOfflineFallback:
    def test_an_unreachable_gateway_fails_and_says_not_to_proceed_from_memory(self, capsys):
        with patch("src.skill_ops.requests.request") as request:
            request.side_effect = requests.exceptions.ConnectionError("refused")
            result = runner.invoke(app, ["skill", "get", "move-session"])

        assert result.exit_code == 1
        combined = _flat(result.output + capsys.readouterr().err)
        assert "could not be reached" in combined
        assert "do NOT proceed from memory" in combined

    def test_a_timeout_fails_the_same_way(self):
        with patch("src.skill_ops.requests.request") as request:
            request.side_effect = requests.exceptions.Timeout("slow")
            result = runner.invoke(app, ["skill", "get", "move-session"])

        assert result.exit_code == 1
        assert "did not respond" in _flat(result.output)

    def test_a_skill_switched_off_surfaces_the_gateways_reason(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(
                400, {"error": "Skill 'move-session' is turned OFF on this fleet."}
            )
            result = runner.invoke(app, ["skill", "get", "move-session"])

        assert result.exit_code == 1
        assert "turned OFF" in _flat(result.output)


class TestDirectoryRoundTrip:
    def test_pull_writes_a_directory_push_can_read_back(self, tmp_path):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, DETAIL_WITH_FILES)
            skill_ops.pull_skill("with-files", str(tmp_path), 3)

        metadata = json.loads((tmp_path / "skill.json").read_text(encoding="utf-8"))
        assert metadata["id"] == "with-files"
        assert metadata["triggers"] == ["run the helper"]
        assert (tmp_path / "SKILL.md").read_text(encoding="utf-8") == DETAIL_WITH_FILES["bodyMarkdown"]
        # The file lands at its own path inside the skill directory - a skill IS a directory, and the
        # body's relative links point at these paths.
        assert (tmp_path / "helper.py").read_text(encoding="utf-8") == "print('hello')\n"
        assert (tmp_path / ".skill-hash").read_text(encoding="utf-8") == "bundle-hash-3"

        body = skill_ops._read_directory("with-files", str(tmp_path), note="n")
        assert body["bodyMarkdown"] == DETAIL_WITH_FILES["bodyMarkdown"]
        assert body["summary"] == "Carries a helper."
        assert body["triggers"] == ["run the helper"]
        assert body["files"] == [
            {
                "fileName": "helper.py",
                "content": "print('hello')\n",
                "encoding": "utf8",
                "executable": False,
            }
        ]

    def test_pull_mirrors_the_server_so_a_deleted_file_does_not_come_back(self, tmp_path):
        (tmp_path / "references").mkdir(parents=True)
        (tmp_path / "references" / "stale.py").write_text("old", encoding="utf-8")

        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, dict(DETAIL_WITH_FILES, files=[]))
            skill_ops.pull_skill("with-files", str(tmp_path), 3)

        # Otherwise the next push would resurrect a file another author deleted on the Gateway.
        assert not (tmp_path / "references" / "stale.py").exists()

    def test_push_without_a_sidecar_is_refused_rather_than_clobbering(self, tmp_path):
        (tmp_path / "SKILL.md").write_text("# mine", encoding="utf-8")
        (tmp_path / "skill.json").write_text(
            json.dumps({"id": "mine", "name": "Mine", "summary": "s"}), encoding="utf-8"
        )
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, {"versions": [{"version": 1}]})
            result = runner.invoke(app, ["skill", "push", "mine", "--dir", str(tmp_path)])

        assert result.exit_code == 1
        assert "could silently overwrite another author" in _flat(result.output)

    def test_push_refuses_a_directory_whose_declared_id_disagrees(self, tmp_path):
        (tmp_path / "skill.json").write_text(json.dumps({"id": "other"}), encoding="utf-8")

        try:
            skill_ops._read_directory("mine", str(tmp_path), None)
            assert False, "a mismatched id must be refused"
        except skill_ops.GatewayError as ex:
            assert "declares id 'other'" in str(ex)


class TestList:
    def test_list_shows_one_row_per_skill_with_its_state(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(
                200,
                {"skills": [HEAD, dict(HEAD, id="switched-off", enabled=False, isBuiltIn=False)]},
            )
            result = runner.invoke(app, ["skill", "list"])

        assert result.exit_code == 0
        flat = _flat(result.output)
        assert "move-session" in flat
        assert "available" in flat
        assert "OFF" in flat

    def test_list_json_is_the_raw_register(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, {"skills": [HEAD]})
            result = runner.invoke(app, ["skill", "list", "--json"])

        assert json.loads(result.output)["skills"][0]["id"] == "move-session"


class TestAPreLibraryGatewayIsNotBelieved:
    """The Gateway serves the Cockpit at "/" and falls unknown page paths back to index.html, so a
    Gateway from before the skill library answers /gateway/skills with HTTP 200, Content-Type
    text/html and the app shell - never a 404. That is the live state of every machine whose Gateway
    has not been upgraded, so these are the rollout window's tests.

    The dangerous direction is not the error: it is being BELIEVED. Without the content-type
    assertion, `skill get` prints a web page into an agent's context as the literal text of a skill,
    and it looks like it worked.
    """

    APP_SHELL = '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>'

    @staticmethod
    def _shell_response() -> MagicMock:
        resp = MagicMock(spec=requests.Response)
        resp.status_code = 200
        resp.content = TestAPreLibraryGatewayIsNotBelieved.APP_SHELL.encode()
        resp.text = TestAPreLibraryGatewayIsNotBelieved.APP_SHELL
        resp.headers = {"Content-Type": "text/html; charset=utf-8"}
        resp.json.side_effect = ValueError("not json")
        return resp

    def test_skill_get_refuses_the_app_shell_instead_of_printing_it_as_instructions(self, capsys):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = self._shell_response()
            result = runner.invoke(app, ["skill", "get", "move-session"])

        assert result.exit_code == 1
        combined = _flat(result.output + capsys.readouterr().err)
        # Nothing of the page reached standard output as a skill.
        assert "<!doctype html" not in combined
        assert "DevThrottle Cockpit</title>" not in combined
        # And the reason is stated the way the agent hitting it needs to read it.
        assert "does not serve the skill library yet" in combined
        assert "Do NOT treat what it returned as a skill" in combined

    def test_skill_list_says_the_gateway_is_not_deployed_rather_than_raising_a_traceback(self):
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = self._shell_response()
            result = runner.invoke(app, ["skill", "list"])

        assert result.exit_code == 1
        assert "does not serve the skill library yet" in _flat(result.output)

    def test_a_body_labelled_json_that_is_not_json_is_reported_not_raised(self):
        resp = MagicMock(spec=requests.Response)
        resp.status_code = 200
        resp.content = b"{ truncated"
        resp.text = "{ truncated"
        resp.headers = {"Content-Type": "application/json"}
        resp.json.side_effect = ValueError("Expecting property name")
        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = resp
            result = runner.invoke(app, ["skill", "list"])

        assert result.exit_code == 1
        assert "could not be parsed" in _flat(result.output)

    def test_a_real_markdown_body_still_passes_through_untouched(self, capsys):
        resp = MagicMock(spec=requests.Response)
        resp.status_code = 200
        resp.content = BODY.encode()
        resp.text = BODY
        resp.headers = {"Content-Type": "text/markdown; charset=utf-8"}
        head = MagicMock(spec=requests.Response)
        head.status_code = 200
        head.content = b"{}"
        head.headers = {"Content-Type": "application/json"}
        head.json.return_value = HEAD

        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.side_effect = [head, resp]
            skill_ops.get_skill("move-session", None)

        # The guard must not cost the thing it protects: a real body is still byte for byte.
        assert capsys.readouterr().out == BODY


class TestASkillIsADirectory:
    """A skill is a DIRECTORY in the Agent Skills standard - SKILL.md plus any files and
    subdirectories - and every agent this product supervises reads exactly that shape. These pin the
    command line to it, because a flattened or lossy round trip would quietly corrupt real skills."""

    def test_a_tree_with_a_binary_and_a_script_round_trips_byte_for_byte(self, tmp_path):
        import base64
        import os

        raw = bytes([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x7F])
        detail = dict(
            DETAIL_WITH_FILES,
            files=[
                {"fileName": "references/tracing.md", "contentHash": "a", "content": "# Tracing\n",
                 "encoding": "utf8", "executable": False},
                {"fileName": "assets/logo.png", "contentHash": "b",
                 "content": base64.b64encode(raw).decode("ascii"), "encoding": "base64",
                 "executable": False},
                {"fileName": "scripts/build.sh", "contentHash": "c", "content": "#!/bin/sh\necho hi\n",
                 "encoding": "utf8", "executable": True},
            ],
        )

        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, detail)
            skill_ops.pull_skill("with-files", str(tmp_path), 3)

        # Subdirectories are real directories on disk, and the binary is bytes, not mangled text.
        assert (tmp_path / "references" / "tracing.md").read_text(encoding="utf-8") == "# Tracing\n"
        assert (tmp_path / "assets" / "logo.png").read_bytes() == raw
        assert (tmp_path / "scripts" / "build.sh").is_file()

        # The executable bit is recorded where the platform can actually keep it: in the filesystem on
        # Linux and macOS, in skill.json on Windows, which has no such bit to read back.
        metadata = json.loads((tmp_path / "skill.json").read_text(encoding="utf-8"))
        assert metadata["executable"] == ["scripts/build.sh"]
        if os.name != "nt":
            assert os.stat(tmp_path / "scripts" / "build.sh").st_mode & 0o111

        # And pushing the directory back reproduces exactly what the Gateway sent.
        body = skill_ops._read_directory("with-files", str(tmp_path), note=None)
        by_path = {f["fileName"]: f for f in body["files"]}
        assert set(by_path) == {"references/tracing.md", "assets/logo.png", "scripts/build.sh"}
        assert by_path["assets/logo.png"]["encoding"] == "base64"
        assert base64.b64decode(by_path["assets/logo.png"]["content"]) == raw
        assert by_path["references/tracing.md"]["encoding"] == "utf8"
        assert by_path["scripts/build.sh"]["executable"] is True

    def test_the_standards_frontmatter_survives_the_round_trip(self, tmp_path):
        detail = dict(
            DETAIL_WITH_FILES,
            license="Apache-2.0",
            compatibility="Requires git and jq",
            allowedTools="Bash(git:*) Read",
            metadata={"author": "example-org", "version": "1.0"},
        )

        with patch.object(skill_ops.SkillClient, "_request") as request:
            request.return_value = _fake_response(200, detail)
            skill_ops.pull_skill("with-files", str(tmp_path), 3)

        body = skill_ops._read_directory("with-files", str(tmp_path), note=None)
        assert body["license"] == "Apache-2.0"
        assert body["compatibility"] == "Requires git and jq"
        assert body["allowedTools"] == "Bash(git:*) Read"
        assert body["metadata"] == {"author": "example-org", "version": "1.0"}

    def test_the_materialized_cache_is_a_real_skill_directory(self, tmp_path, monkeypatch):
        # The body's relative links say "references/tracing.md", so the cache has to put the file
        # THERE. A flattened cache would make every link in every skill wrong.
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        detail = dict(
            DETAIL_WITH_FILES,
            files=[{"fileName": "references/tracing.md", "contentHash": "a", "content": "# Tracing\n",
                    "encoding": "utf8", "executable": False}],
        )

        paths = skill_ops._materialize("with-files", 3, detail)

        root = tmp_path / "cc-director" / "skills" / "with-files" / "3"
        assert (root / "references" / "tracing.md").read_text(encoding="utf-8") == "# Tracing\n"
        assert (root / "SKILL.md").read_text(encoding="utf-8") == DETAIL_WITH_FILES["bodyMarkdown"]
        assert paths == [root / "references" / "tracing.md"]
        # The hash sidecar sits BESIDE the directory: inside, it would be a file the skill did not put
        # there, and it could collide with one the skill did.
        assert (tmp_path / "cc-director" / "skills" / "with-files" / "3.hash").is_file()

    def test_a_server_supplied_path_that_escapes_the_directory_is_refused(self, tmp_path, monkeypatch):
        # The Gateway validates paths on write, but this command does not TRUST that: an older,
        # misconfigured or hostile server must not be able to steer a write outside the target.
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        for bad in ("../escape.py", "references/../../escape.py", "/etc/passwd", "nul", "C:/x.dll"):
            evil = dict(
                DETAIL_WITH_FILES,
                files=[{"fileName": bad, "contentHash": "x", "content": "bad", "encoding": "utf8",
                        "executable": False}],
            )
            try:
                skill_ops._materialize("with-files", 3, evil)
                assert False, f"an unsafe file path must be refused: {bad}"
            except skill_ops.GatewayError as ex:
                assert "unsafe file path" in str(ex) or "reserved Windows device name" in str(ex)
        assert not (tmp_path / "cc-director" / "skills" / "escape.py").exists()

    def test_an_encoding_this_version_does_not_know_fails_loudly(self, tmp_path, monkeypatch):
        # Not guessed at, and not silently written as text: a file written from an encoding we do not
        # understand is a corrupt file that looks fine.
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
        detail = dict(
            DETAIL_WITH_FILES,
            files=[{"fileName": "x.bin", "contentHash": "x", "content": "zzz", "encoding": "rot13",
                    "executable": False}],
        )
        try:
            skill_ops._materialize("with-files", 3, detail)
            assert False, "an unknown encoding must fail"
        except skill_ops.GatewayError as ex:
            assert "encoding this command does not know" in str(ex)
