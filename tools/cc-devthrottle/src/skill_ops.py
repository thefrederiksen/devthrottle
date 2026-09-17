"""Gateway skill operations for cc-devthrottle - the central skill library (devthrottle_internal
issue 995).

A Skill is a capability an agent reaches for mid-task: a markdown body plus optional supporting
files, held and versioned on the Gateway rather than copied onto every machine by the installer.

THE ONE RULE THIS MODULE EXISTS TO KEEP: discovery is cheap, use is what costs. Every session's
launch briefing carries one line per skill; `skill get` is the only command that pulls a body, and
it is run once, by a session that is about to use that skill.

There is NO offline fallback, deliberately. `skill get` resolves the current published version from
the Gateway every time; if the Gateway cannot be reached the command FAILS and says so. A stale
skill that looks current is worse than a missing one that announces itself - an agent acting on
withdrawn instructions is exactly the failure the central library exists to make impossible.

The authoring loop round-trips a directory, like workflows:

    <dir>/skill.json       metadata: id, name, summary, triggers, the Agent Skills standard's
                           frontmatter fields, and which files are executable
    <dir>/SKILL.md         the body an agent reads
    <dir>/<anything>       the supporting files, at their own relative paths - "references/tracing.md"
                           is a file called tracing.md inside a references directory, because a skill
                           is a DIRECTORY in the Agent Skills standard and every agent this product
                           supervises reads that same shape
    <dir>/.skill-hash      sidecar written by pull; sent as If-Match on push so a stale copy is
                           refused instead of clobbering a concurrent author

A push sends text files as text and everything else base64-encoded, so an image, an archive or a
compiled command-line program survives the round trip byte for byte.
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlencode

import requests
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from urllib.parse import quote as urllib_quote

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

from . import axi_cli, bundle_files  # noqa: E402

TIMEOUT_SECONDS = 15
SKILL_JSON = "skill.json"
SKILL_MD = "SKILL.md"
HASH_SIDECAR = ".skill-hash"

console = Console()
err_console = Console(stderr=True)
gateway_override: Optional[str] = None


#: THE SAME CLASS THE SHARED TRANSPORT RAISES, not a look-alike beside it.
#:
#: This used to be its own `class GatewayError(Exception)`, while `gateway.gateway_base_url()` and
#: `gateway.session_key()` - called directly from this module - raise `cc_shared.gateway.GatewayError`.
#: Every `except GatewayError` here therefore missed the no-Gateway failure entirely, and the command
#: died with a Rich traceback. The owner accepted "no Gateway means no agent tooling" on the promise of
#: a CLEAR SENTENCE naming the remedy; a stack trace is not that sentence.
#:
#: Aliasing rather than catching both is deliberate: two names for one idea is what caused this, and a
#: second except clause on every handler would leave the trap in place for the next handler written.
GatewayError = gateway.GatewayError


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    """The Gateway this SESSION was told to call.

    Remove-the-network-port mission, phase 2. This used to read gateway.url out of config.json and fall
    back to a loopback address, which meant the command line kept its own opinion about where the
    Gateway is - one that could be right on the machine and wrong for the session. The session is TOLD
    the address at launch, beside the credential that goes with it, and one source for both is what
    makes them impossible to mismatch.
    """
    return gateway.gateway_base_url()


def _auth_token() -> str:
    """This session's own Gateway key.

    IT USED TO BE THE ACCOUNT'S. This function read `gateway.token` from config.json - the shared
    machine credential, which has authority over the whole account on every machine - and presented it
    straight to the Gateway. So every agent that ran one of these commands held the run of the account,
    which is precisely the hole Phase 1b was chartered to prevent, already open on this path. The
    session key closes it: bound to one session, one tenant, and the fleet's agent routes only.
    """
    return gateway.session_key()




def default_authored_by() -> str:
    session_id = (os.environ.get("CC_SESSION_ID") or "").strip()
    if session_id:
        return f"session:{session_id}"
    return f"machine:{socket.gethostname()}"


class SkillClient:
    """Talks to one Gateway's skill surface."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()

    def _headers(self, extra: Optional[Dict[str, str]] = None) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        if extra:
            headers.update(extra)
        return headers

    def _request(
        self,
        method: str,
        path: str,
        json_body: Optional[Dict[str, Any]] = None,
        extra_headers: Optional[Dict[str, str]] = None,
    ) -> requests.Response:
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method,
                url,
                json=json_body,
                headers=self._headers(extra_headers),
                timeout=TIMEOUT_SECONDS,
            )
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"the Gateway at {self.base_url} could not be reached. Skills are held centrally "
                "and are NOT cached for offline use, so there is nothing local to fall back on. "
                "Fix the connection and run this again - do NOT proceed from memory of what this "
                "skill used to say."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"the Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s. "
                "Run this again once it is reachable - do NOT proceed from memory of what this "
                "skill used to say."
            ) from exc

    @staticmethod
    def _gateway_message(resp: requests.Response) -> str:
        try:
            data = resp.json()
            if isinstance(data, dict) and data.get("error"):
                return str(data["error"])
        except ValueError:
            pass
        text = (resp.text or "").strip()
        return text if text else f"Gateway returned HTTP {resp.status_code}"

    # A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. The Gateway serves the Cockpit
    # single-page app at "/" and falls UNKNOWN page paths back to index.html, so a Gateway that has
    # never heard of skills answers GET /gateway/skills with HTTP 200, Content-Type text/html, and
    # ~800 bytes of the app shell - not a 404. That is the live state of every machine whose Gateway
    # has not been upgraded yet, which is precisely the window this command has to survive.
    #
    # Believed at face value it is worse than an error: `skill get` would print the HTML shell into
    # an agent's context AS THE SKILL'S INSTRUCTIONS, and `skill list` would raise a raw ValueError
    # traceback. So both read paths assert the content type they were PROMISED, and an app-shell
    # answer is reported as "this Gateway does not serve the skill library yet" - which is the true
    # statement, and the one that tells the user what to do about it.
    def _not_the_skill_library(self, saw: str) -> GatewayError:
        return GatewayError(
            f"the Gateway at {self.base_url} answered with {saw} instead of skill data, which means "
            "it does not serve the skill library yet - it is running a build from before the library "
            "existed, and the request fell through to its web app. Upgrade or redeploy that Gateway. "
            "Do NOT treat what it returned as a skill."
        )

    @staticmethod
    def _content_type(resp: requests.Response) -> str:
        return (resp.headers.get("Content-Type") or "").split(";")[0].strip().lower()

    def _json_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            if self._content_type(resp) != "application/json":
                raise self._not_the_skill_library(self._content_type(resp) or "an unlabelled body")
            try:
                return resp.json()
            except ValueError as exc:
                # Labelled JSON that is not JSON: a proxy or error page, never something to act on.
                raise GatewayError(
                    f"the Gateway at {self.base_url} returned a body labelled JSON that could not be "
                    f"parsed: {exc}"
                ) from exc
        raise GatewayError(self._gateway_message(resp))

    def _text_or_raise(self, resp: requests.Response) -> str:
        if 200 <= resp.status_code < 300:
            # The body and file routes promise markdown and plain text. HTML here is the app shell,
            # and printing it would put a web page into an agent's context dressed as instructions.
            if self._content_type(resp) == "text/html":
                raise self._not_the_skill_library("its web app page")
            return resp.text
        raise GatewayError(self._gateway_message(resp))

    # ---- reads --------------------------------------------------------------------------------

    def list_skills(self) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", "/gateway/skills"))
        return list(data.get("skills", []))

    def get_skill(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/skills/{skill_id}"))

    def get_body(self, skill_id: str, version: Optional[int]) -> str:
        path = f"/gateway/skills/{skill_id}/body"
        if version is not None:
            path += f"?version={version}"
        return self._text_or_raise(self._request("GET", path))

    def list_versions(self, skill_id: str) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", f"/gateway/skills/{skill_id}/versions"))
        return list(data.get("versions", []))

    def skill_exists(self, skill_id: str) -> bool:
        """True when the skill head exists at all - drafts included. GET /{id} cannot answer this
        (it 404s for a draft-only skill), so existence goes through the versions route."""
        resp = self._request("GET", f"/gateway/skills/{skill_id}/versions")
        if resp.status_code == 404:
            return False
        self._json_or_raise(resp)
        return True

    def get_version_detail(self, skill_id: str, version: int) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("GET", f"/gateway/skills/{skill_id}/versions/{version}")
        )

    # ---- writes -------------------------------------------------------------------------------

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", "/gateway/skills", body))

    def update_draft(
        self, skill_id: str, body: Dict[str, Any], if_match: Optional[str]
    ) -> Dict[str, Any]:
        extra = {"If-Match": if_match} if if_match else None
        return self._json_or_raise(
            self._request("PUT", f"/gateway/skills/{skill_id}/draft", body, extra)
        )

    def publish(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", f"/gateway/skills/{skill_id}/publish"))

    def clone(self, skill_id: str, new_id: str) -> Dict[str, Any]:
        query = urlencode({"newId": new_id, "by": default_authored_by()})
        return self._json_or_raise(
            self._request("POST", f"/gateway/skills/{skill_id}/clone?{query}")
        )

    def delete(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("DELETE", f"/gateway/skills/{skill_id}"))

    def set_enabled(self, skill_id: str, enabled: bool) -> Dict[str, Any]:
        verb = "enable" if enabled else "disable"
        actor = urllib_quote(default_authored_by())
        return self._json_or_raise(
            self._request("POST", f"/gateway/skills/{skill_id}/{verb}?by={actor}")
        )


_FIND_A_SKILL = "cc-devthrottle skill list"


def _fail(message: str, next_commands: List[str]) -> None:
    """Report a failure on standard error with what to run next, and exit 1."""
    axi_cli.fail(message, next_commands)


def _describe(ex: Exception) -> str:
    """An exception as one line for an Error: its message, and its kind when that says more."""
    if isinstance(ex, (GatewayError, OSError)):
        return str(ex)
    return f"{type(ex).__name__}: {ex}"


def _ref(skill_id: str) -> str:
    """The skill id for a help line, or a placeholder when it cannot be pasted back as it is."""
    return axi_cli.bare(skill_id, "<skill-id>")


def _dir_arg(directory: str) -> str:
    return axi_cli.quoted(directory, "<dir>")


def _client() -> SkillClient:
    return SkillClient(base_url=gateway_override)


RESERVED_WINDOWS_NAMES = {
    "con", "prn", "aux", "nul",
    "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
    "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
}


def _safe_relative_path(name: str) -> str:
    """Refuse any server-supplied file path that could write outside the skill's own directory.

    A skill is a DIRECTORY, so 'references/tracing.md' is an ordinary and expected path here. The
    Gateway validates paths on write, but this command MUST NOT trust that: a misconfigured, older,
    or hostile server must not be able to steer a write anywhere but under the target directory. This
    is the same rule the Gateway enforces, enforced again at the point where bytes hit this disk.
    """
    shown = axi_cli.ascii_text(name)
    if not name or name != name.strip():
        raise GatewayError(f"the Gateway returned an unsafe file path: '{shown}'.")
    if "\\" in name or ":" in name or name.startswith("/") or name.endswith("/"):
        raise GatewayError(f"the Gateway returned an unsafe file path: '{shown}'.")
    segments = name.split("/")
    if len(segments) > 5:
        raise GatewayError(f"the Gateway returned a file path nested too deeply: '{shown}'.")
    for segment in segments:
        if not segment or segment in (".", ".."):
            raise GatewayError(f"the Gateway returned an unsafe file path: '{shown}'.")
        problem = bundle_files.name_problem(segment)
        if problem is not None:
            raise GatewayError(
                f"the Gateway returned an unsafe file path: '{shown}' ({problem}), so nothing was written."
            )
        if segment.split(".")[0].lower() in RESERVED_WINDOWS_NAMES:
            raise GatewayError(
                f"the Gateway returned a file path using a reserved Windows device name: '{shown}'."
            )
    return name


def _read_exact(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


#: Paths a skill directory's own writers produce. A supporting file may not use one, at any letter
#: case, because it would overwrite that file - or the file would overwrite it - on a disk that
#: ignores case, and a push skips these names so the file could never round-trip anyway.
_RESERVED_PATHS = (SKILL_MD, SKILL_JSON, HASH_SIDECAR)

#: The authored text fields of a version detail, all written to skill.json and pushed back.
_REQUIRED_TEXT = ("skillId", "status", "name", "summary", "bodyMarkdown", "contentHash")
_OPTIONAL_TEXT = ("license", "compatibility", "allowedTools")


def _checked_bundle(detail: Any, skill_id: str, version: int) -> Dict[str, Any]:
    """Check the WHOLE version detail before anything on disk is touched, and return the exact bytes
    every file will hold: the body, skill.json, each supporting file and the hash sidecar.

    A pull or a cache refresh replaces the local files with the version's, and a later push sends
    skill.json back, so a partial or broken answer must be refused outright rather than read as
    "empty": a file with no content would become an empty file, a missing body an empty SKILL.md, a
    missing list a wiped directory, and a missing name or summary an emptied skill on the next push.
    Every field the Gateway's version detail carries and these writers use must be present with its
    own type; the answer must be for the skill and version that were asked for. Every text must be
    writable as UTF-8 and every path a name the operating system accepts, so the writes that follow
    cannot fail on the data itself."""
    import base64
    import binascii

    def refuse(what: str) -> GatewayError:
        return GatewayError(f"the Gateway's answer {what}, so nothing was written.")

    if not isinstance(detail, dict):
        raise refuse("was not a skill version")
    for field in _REQUIRED_TEXT:
        if not isinstance(detail.get(field), str):
            raise refuse(f"did not include the skill version's '{field}' as text")
    if not detail["contentHash"]:
        raise refuse("had an empty 'contentHash'")
    for field in _OPTIONAL_TEXT:
        if field not in detail or not (detail[field] is None or isinstance(detail[field], str)):
            raise refuse(f"did not include the skill version's '{field}' as text or null")
    if detail["skillId"].strip().lower() != skill_id.strip().lower():
        raise refuse(f"was for skill '{detail['skillId']}', not the '{skill_id}' that was asked for")
    answered = detail.get("version")
    if not isinstance(answered, int) or isinstance(answered, bool) or answered != version:
        raise refuse(f"was for version {answered!r}, not the version {version} that was asked for")
    triggers = detail.get("triggers")
    if not isinstance(triggers, list) or not all(isinstance(t, str) for t in triggers):
        raise refuse("did not include the skill version's 'triggers' as a list of text")
    standard_metadata = detail.get("metadata")
    if not isinstance(standard_metadata, dict) or not all(
        isinstance(v, str) for v in standard_metadata.values()
    ):
        raise refuse("did not include the skill version's 'metadata' as a map of text")
    entries = detail.get("files")
    if not isinstance(entries, list):
        raise refuse("did not list the version's supporting files")

    reserved = {p.lower() for p in _RESERVED_PATHS}
    files: List[Dict[str, Any]] = []
    seen = set()
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("fileName"), str):
            raise refuse("lists a supporting file with no name")
        relative = _safe_relative_path(entry["fileName"])
        folded = relative.lower()
        if folded.split("/")[0] in reserved:
            raise refuse(
                f"lists a supporting file '{axi_cli.ascii_text(relative)}' at a path the skill's own files use "
                f"({', '.join(_RESERVED_PATHS)})"
            )
        shown = axi_cli.ascii_text(relative)
        if folded in seen:
            raise refuse(f"lists the supporting file '{shown}' twice")
        seen.add(folded)
        content = entry.get("content")
        if not isinstance(content, str):
            raise refuse(f"has no content for the supporting file '{shown}'")
        encoding = entry.get("encoding")
        if not isinstance(encoding, str):
            raise refuse(f"has no encoding for the supporting file '{shown}'")
        if not isinstance(entry.get("executable"), bool):
            raise refuse(f"does not say whether the supporting file '{shown}' is executable")
        encoding = encoding.strip().lower()
        try:
            if encoding == "base64":
                data = base64.b64decode(content, validate=True)
            elif encoding == "utf8":
                data = content.encode("utf-8")
            else:
                raise GatewayError(
                    f"the Gateway sent file '{shown}' with an encoding this command does not know "
                    f"('{axi_cli.ascii_text(encoding)}'), so nothing was written. Upgrade cc-devthrottle."
                )
        except (binascii.Error, UnicodeEncodeError) as exc:
            raise refuse(f"has content for '{shown}' that does not decode ({exc})") from exc
        files.append({"fileName": relative, "data": data, "executable": entry["executable"]})

    # A path that is both a file and the folder of another file cannot be written.
    for name in seen:
        parts = name.split("/")
        for depth in range(1, len(parts)):
            if "/".join(parts[:depth]) in seen:
                raise refuse(
                    f"lists '{axi_cli.ascii_text('/'.join(parts[:depth]))}' as a file and as a folder"
                )

    # Every authored field, including the Agent Skills standard's own frontmatter, so a pulled skill
    # can be pushed back without losing what its author wrote. Which files are executable: on
    # Windows this list IS the answer, because the filesystem has no bit to read; on Linux and macOS
    # the bit on disk wins and this is a record of what was pulled.
    metadata = {
        "id": detail["skillId"],
        "name": detail["name"],
        "summary": detail["summary"],
        "triggers": list(triggers),
        "license": detail["license"],
        "compatibility": detail["compatibility"],
        "allowedTools": detail["allowedTools"],
        "metadata": dict(standard_metadata),
        "executable": sorted(f["fileName"] for f in files if f["executable"]),
    }
    # json.dumps would escape a lone surrogate and write it happily, and the next push would send
    # it back; encoding the unescaped form finds it.
    if bundle_files.text_bytes(json.dumps(metadata, ensure_ascii=False)) is None:
        raise refuse("has a field that cannot be written as UTF-8")
    body = bundle_files.text_bytes(detail["bodyMarkdown"])
    if body is None:
        raise refuse("has a body that cannot be written as UTF-8")
    content_hash = bundle_files.text_bytes(detail["contentHash"])
    if content_hash is None:
        raise refuse("has a 'contentHash' that cannot be written as UTF-8")
    # Text files are written with this platform's line ending, as they always were.
    metadata_text = (json.dumps(metadata, indent=2) + "\n").replace("\n", os.linesep)
    return {
        "body": body,
        "contentHash": detail["contentHash"],
        "hash": content_hash,
        "files": files,
        "metadata": metadata_text.encode("utf-8"),
    }


# HOW A PULL OR A CACHE REFRESH WRITES, AND WHAT IT DOES NOT PROMISE.
#
# `_checked_bundle` checks the WHOLE Gateway answer before anything on disk is touched, and turns it
# into the exact bytes of every file: the body, skill.json, each supporting file (decoded) and the
# hash sidecar. Any text that cannot be written as UTF-8, any name the operating system would refuse
# (a NUL or other control character, < > : " | ? * or a backslash, a name over 255 bytes, a reserved
# Windows device name), any path that collides with the skill's own files, and - on macOS and Linux -
# any final path longer than the machine allows is refused there, so a partial or malformed answer
# leaves the old files exactly as they were. The writes that follow only put those prepared bytes at
# those checked paths, in this order: the new files over the old ones, then every old entry the new
# version no longer has is removed, then the hash is written LAST, so a hash never vouches for files
# that are not all on disk. Whatever still fails is this machine, not the data, and the command
# reports it as an Error line with next steps.
#
# NOT GUARANTEED (moved to its own issue, not solved here):
# - A write that fails part way because of this machine (a full disk, a file another program holds
#   open, a path over the Windows length limit, which depends on a machine-wide setting and so is
#   not checked in advance), or a process killed part way, can leave a MIXED directory: some new
#   files, some old. That was also true
#   before this change. The old hash is left in place, and it does not match the new version, so
#   the next cache read rewrites the directory and a push is compared against the old version.
# - Windows name aliases are not yet refused: a supporting file named `SKILL.md.` or `SKILL.md `
#   (a trailing dot or space) is written by Windows to `SKILL.md` and overwrites the body.
# - On a disk that ignores letter case, an existing folder keeps its old letter case when the new
#   version spells it differently; the files inside are the new ones.


def _check_final_paths(root: Path, bundle: Dict[str, Any], own: List[Path]) -> None:
    """Refuse the answer when any path the write would use is longer than this machine allows.
    `own` is the writer's own files (the body, skill.json, the hash sidecar)."""
    bundle_files.check_paths(
        own + [root / Path(f["fileName"]) for f in bundle["files"]],
        lambda what: GatewayError(f"the Gateway's answer {what}, so nothing was written."),
    )


def _clear_the_way(root: Path, relative: str) -> Path:
    """Make `root/relative` writable as a file, and return it.

    Removes only entries the new version cannot have (the checked bundle has no path that is both a
    file and a folder): a file or link where a folder of this path must be, a folder or link at the
    path itself, and a sibling spelled with other letter case - which on a disk that ignores case IS
    this file, and would keep its old spelling if written over. A link is removed rather than
    followed, so a write never lands outside `root`."""
    target = root / Path(relative)
    parent = root
    for part in Path(relative).parts[:-1]:
        parent = parent / part
        if parent.is_symlink() or (parent.exists() and not parent.is_dir()):
            parent.unlink()
    if target.is_symlink():
        target.unlink()
    elif target.is_dir():
        shutil.rmtree(target)
    if target.parent.is_dir():
        for sibling in target.parent.iterdir():
            if sibling.name != target.name and sibling.name.lower() == target.name.lower():
                if sibling.is_dir() and not sibling.is_symlink():
                    shutil.rmtree(sibling)
                else:
                    sibling.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    return target


def _write_bundle_files(root: Path, files: List[Dict[str, Any]]) -> None:
    """Write checked supporting files under `root` at their own relative paths, setting the
    executable bit where the platform has one."""
    import stat

    for entry in files:
        target = _clear_the_way(root, entry["fileName"])
        target.write_bytes(entry["data"])
        # Windows has no executable bit; on Linux and macOS a bundled script the skill tells an agent
        # to run is useless without it.
        if entry["executable"] and os.name != "nt":
            mode = target.stat().st_mode
            target.chmod(mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def _remove_unlisted(root: Path, keep: List[str]) -> None:
    """Remove every entry under `root` that is not one of the `keep` paths (relative, `/`-separated)
    or a folder holding one. Run AFTER the new files are written, so it only ever removes what the
    new version no longer has. A path that differs from a kept one only by letter case is kept when
    it is the same file on disk (a disk that ignores case), and removed when it is not."""
    exact = set(keep)
    folded: Dict[str, str] = {k.lower(): k for k in keep}
    for current, folders, names in os.walk(root, topdown=False):
        here = Path(current)
        for name in names + [f for f in folders if (here / f).is_symlink()]:
            path = here / name
            relative = path.relative_to(root).as_posix()
            if relative in exact:
                continue
            twin = folded.get(relative.lower())
            if twin is not None and path.exists() and os.path.samefile(path, root / Path(twin)):
                continue
            path.unlink()
        for name in folders:
            path = here / name
            if not path.is_symlink() and path.is_dir() and not any(path.iterdir()):
                path.rmdir()


def _read_tree(root: Path, executable_paths: List[str]) -> List[Dict[str, Any]]:
    """Read a skill directory's supporting files: every file under `root` at any depth, EXCEPT the
    two bookkeeping files this command owns (skill.json and the hash sidecar) and the body itself.

    Text is sent as text and everything else base64-encoded, so an image, an archive or a compiled
    program survives the trip intact. A file is treated as text when its bytes decode as UTF-8 and
    contain no NUL - the test is on the CONTENT, not the extension, because a .md file can be
    anything and a .bin can be plain text.

    WHO OWNS THE EXECUTABLE BIT depends on the platform, and it has to, because Windows does not have
    one. On Linux and macOS the FILESYSTEM is authoritative, so `chmod +x` is how you mark a script
    and `chmod -x` genuinely unmarks it. On Windows the filesystem cannot carry the answer at all, so
    the `executable` list in skill.json is authoritative - which is exactly what a pull writes there.
    Neither platform silently invents a bit it cannot know.
    """
    import base64

    skipped = {SKILL_JSON, HASH_SIDECAR, SKILL_MD}
    files: List[Dict[str, Any]] = []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in skipped:
            continue
        data = path.read_bytes()
        entry: Dict[str, Any] = {"fileName": relative}
        try:
            if b"\x00" in data:
                raise UnicodeDecodeError("utf-8", data, 0, 1, "contains a NUL byte")
            entry["content"] = data.decode("utf-8")
            entry["encoding"] = "utf8"
        except UnicodeDecodeError:
            entry["content"] = base64.b64encode(data).decode("ascii")
            entry["encoding"] = "base64"
        if os.name == "nt":
            entry["executable"] = relative in set(executable_paths)
        else:
            entry["executable"] = bool(path.stat().st_mode & 0o111)
        files.append(entry)
    return files


def _pick_authoring_version(versions: List[Dict[str, Any]]) -> Optional[int]:
    """The version number an author edits next: the draft when one exists, else the published head."""
    for wanted in ("draft", "published"):
        for row in versions:
            if not isinstance(row, dict) or not isinstance(row.get("status"), str):
                raise GatewayError("the Gateway listed a skill version with no status.")
            if row["status"].lower() == wanted:
                return _version_number(row.get("version"), "listed a skill version")
    return None


def _version_number(value: Any, what: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise GatewayError(f"the Gateway {what} without a valid version number (got {value!r}).")
    return value


# ---- commands -------------------------------------------------------------------------------------


def list_skills(json_output: bool) -> None:
    try:
        skills = _client().list_skills()
    except GatewayError as ex:
        _fail(str(ex), ["cc-devthrottle setup status"])
        return

    if json_output:
        print(json.dumps({"skills": skills}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("V", justify="right")
    table.add_column("Kind")
    table.add_column("State")
    table.add_column("Files", justify="right")
    table.add_column("What it does", overflow="fold")
    for skill in skills:
        # An older Gateway omits the enabled field; absent means available.
        state = "OFF" if skill.get("enabled") is False else "available"
        table.add_row(
            skill.get("id", ""),
            skill.get("name", ""),
            str(skill.get("version", "")),
            "built-in" if skill.get("isBuiltIn") else "yours",
            state,
            str(skill.get("fileCount") or ""),
            skill.get("summary", ""),
        )
    console.print(table)
    console.print(
        "Read one IN FULL only when you are about to use it: cc-devthrottle skill get <id>"
    )


def get_skill(skill_id: str, version: Optional[int]) -> None:
    """Fetch one skill and print its body verbatim - the command an agent runs at the moment it
    reaches for a skill. The body goes straight into the agent's context, so nothing is rendered or
    wrapped; supporting files are written to the machine's version-keyed cache and their absolute
    paths are printed after the body, because a script has to exist on disk to be run.

    The version is resolved from the Gateway on EVERY call, so what is printed is always the
    currently published content. There is no offline path: an unreachable Gateway fails the command.
    """
    try:
        client = _client()
        detail: Optional[Dict[str, Any]] = None
        if version is None:
            head = client.get_skill(skill_id)
            if not isinstance(head, dict):
                raise GatewayError("the Gateway's answer was not a skill.")
            version = _version_number(head.get("version"), "described the skill")
            file_count = head.get("fileCount")
            if not isinstance(file_count, int) or isinstance(file_count, bool) or file_count < 0:
                # Absent is not "no files": an agent would be told nothing about files it needs.
                raise GatewayError(
                    f"the Gateway did not say how many supporting files the skill has (got {file_count!r})."
                )
        else:
            detail = client.get_version_detail(skill_id, version)
            file_count = len(_checked_bundle(detail, skill_id, version)["files"])
        body = client.get_body(skill_id, version)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL, f"cc-devthrottle skill versions {_ref(skill_id)}"])
        return

    sys.stdout.write(body)
    if not body.endswith("\n"):
        sys.stdout.write("\n")

    if file_count == 0:
        return

    try:
        if detail is None:
            detail = _client().get_version_detail(skill_id, version)
        paths = _materialize(skill_id, int(version), detail)
    except Exception as ex:  # noqa: BLE001 - the command's entry point: every failure is reported
        # A GatewayError: the answer was refused before anything was written. Anything else: the
        # cache could not be written on this machine.
        # The body already printed, so the agent has the instructions but not the files it was told
        # to run. Say exactly that rather than letting it discover a missing path itself.
        _fail(
            f"the body of '{skill_id}' printed above, but its supporting files could not be "
            f"fetched: {_describe(ex)}",
            [f"cc-devthrottle skill get {_ref(skill_id)} --version {version}"],
        )
        return

    sys.stdout.write("\nFiles for this skill:\n")
    for path in paths:
        sys.stdout.write(f"  {path}\n")


def _materialize(skill_id: str, version: int, detail: Dict[str, Any]) -> List[Path]:
    """Write a version's supporting files to the per-machine cache and return their absolute paths.

    The cache is keyed by (skill, VERSION) and verified by content hash, so it can never serve one
    version's files as another's: a newly published version is a new directory, and a half-written
    or partly-deleted bundle is rewritten rather than reported as intact. It is a cache of what was
    fetched, never a substitute for fetching - `get_skill` always resolves the version from the
    Gateway first.
    """
    bundle = _checked_bundle(detail, skill_id, version)
    files = bundle["files"]

    local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
    versions_root = Path(local_app_data) / "cc-director" / "skills" / skill_id
    # The materialized directory IS the skill's own directory - SKILL.md at its root and every file
    # at the path the body's relative links point at ("references/tracing.md" lands at
    # references/tracing.md). A flattened copy, or one nested under an extra folder, would make every
    # relative link in every skill wrong.
    root = versions_root / str(version)
    # The hash sidecar sits BESIDE the directory, not inside it: inside, it would be a file the skill
    # did not put there, and it could collide with one the skill did.
    hash_file = versions_root / f"{version}.hash"
    partial = versions_root / f"{version}.hash.tmp"
    expected = bundle["contentHash"]
    _check_final_paths(root, bundle, [root / SKILL_MD, hash_file, partial])

    intact = (
        hash_file.is_file()
        and hash_file.read_text(encoding="utf-8").strip() == expected
        and (root / SKILL_MD).is_file()
        and all((root / Path(f["fileName"])).is_file() for f in files)
    )
    if not intact:
        # A sidecar that already names this version vouches for files that are not all there, so it
        # goes first: otherwise a refresh that fails part way would leave it vouching for a mix. Any
        # other sidecar cannot match this version, so it stays until the new one replaces it.
        if hash_file.is_file() and hash_file.read_text(encoding="utf-8").strip() == expected:
            hash_file.unlink()
        root.mkdir(parents=True, exist_ok=True)
        _clear_the_way(root, SKILL_MD).write_bytes(bundle["body"])
        _write_bundle_files(root, files)
        _remove_unlisted(root, [SKILL_MD] + [f["fileName"] for f in files])
        # Written last, and swapped in whole, so it never vouches for files that are not on disk.
        partial.write_bytes(bundle["hash"])
        os.replace(partial, hash_file)

    return [root / Path(f["fileName"]) for f in files]


def show_skill(skill_id: str, version: Optional[int], json_output: bool) -> None:
    try:
        client = _client()
        if version is not None:
            data = client.get_version_detail(skill_id, version)
        else:
            data = client.get_skill(skill_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL, f"cc-devthrottle skill versions {_ref(skill_id)}"])
        return

    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"[bold]{data.get('name', skill_id)}[/bold]  ({skill_id})")
    if data.get("status"):
        console.print(f"Status: {data['status']}  Version: {data.get('version')}")
    else:
        console.print(
            f"Version: {data.get('version')}  "
            f"Kind: {'built-in' if data.get('isBuiltIn') else 'yours'}  "
            f"Draft waiting: {'yes' if data.get('hasDraft') else 'no'}"
        )
    console.print(f"What it does: {data.get('summary', '')}")
    triggers = data.get("triggers") or []
    if triggers:
        console.print("Triggers: " + ", ".join(triggers))
    files = data.get("files")
    if files:
        console.print("Files: " + ", ".join(f.get("fileName", "") for f in files))
    elif data.get("fileCount"):
        console.print(f"Files: {data['fileCount']}")
    if data.get("isBuiltIn"):
        console.print(
            "Built in and read-only. To customize it: "
            f"cc-devthrottle skill clone {_ref(skill_id)} <new-id>"
        )
    console.print(
        f"Read it in full: cc-devthrottle skill get {_ref(skill_id)}"
        + (f" --version {version}" if version is not None else "")
    )


def list_versions(skill_id: str, json_output: bool) -> None:
    try:
        versions = _client().list_versions(skill_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL])
        return

    if json_output:
        print(json.dumps({"versions": versions}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Version", justify="right")
    table.add_column("Status")
    table.add_column("Authored by")
    table.add_column("Created (UTC)")
    table.add_column("Note", overflow="fold")
    for row in versions:
        table.add_row(
            str(row.get("version", "")),
            row.get("status", ""),
            row.get("authoredBy", ""),
            (row.get("createdUtc") or "").replace("T", " ")[:19],
            row.get("changeNote") or "",
        )
    console.print(table)


def pull_skill(skill_id: str, directory: str, version: Optional[int]) -> None:
    try:
        client = _client()
        if version is None:
            versions = client.list_versions(skill_id)
            picked = _pick_authoring_version(versions)
            if picked is None:
                _fail(
                    f"skill '{skill_id}' has no versions to pull.",
                    [f"cc-devthrottle skill versions {_ref(skill_id)}", _FIND_A_SKILL],
                )
                return
            version = picked
        detail = client.get_version_detail(skill_id, version)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL, f"cc-devthrottle skill versions {_ref(skill_id)}"])
        return

    target = Path(directory)
    try:
        _write_pulled(target, skill_id, version, detail)
    except GatewayError as ex:
        _fail(str(ex), [f"cc-devthrottle skill show {_ref(skill_id)} --version {version}"])
        return
    except Exception as ex:  # noqa: BLE001 - the command's entry point: every failure is reported
        # The answer was checked in full, so what is left is this machine: a full disk, a file another
        # program holds, a path over Windows' length limit.
        _fail(
            f"could not write the skill into {target}: {_describe(ex)}",
            [f'cc-devthrottle skill pull {_ref(skill_id)} --dir "<writable-dir>"'],
        )
        return

    axi_cli.write_lines(
        f"Pulled '{skill_id}' v{version} ({detail['status']}) into {target.resolve()}",
        f"Edit the files, then push with: cc-devthrottle skill push {_ref(skill_id)} --dir {_dir_arg(directory)}",
    )
    axi_cli.print_next([f"cc-devthrottle skill push {_ref(skill_id)} --dir {_dir_arg(directory)}"])


def _write_pulled(target: Path, skill_id: str, version: int, detail: Dict[str, Any]) -> None:
    """Write one pulled version into `target`. The whole answer is checked BEFORE anything is
    written, so a bad answer leaves the directory exactly as it was. What a write that fails part
    way can leave is described above `_clear_the_way`.

    The directory mirrors the SERVER: a supporting file another author deleted on the Gateway must
    not survive locally and be resurrected by the next push, so every entry the version does not
    have is removed once the new files are written."""
    bundle = _checked_bundle(detail, skill_id, version)
    files = bundle["files"]
    _check_final_paths(target, bundle, [target / name for name in _RESERVED_PATHS])

    target.mkdir(parents=True, exist_ok=True)
    _clear_the_way(target, SKILL_MD).write_bytes(bundle["body"])
    _clear_the_way(target, SKILL_JSON).write_bytes(bundle["metadata"])
    _write_bundle_files(target, files)
    _remove_unlisted(target, [SKILL_MD, SKILL_JSON, HASH_SIDECAR] + [f["fileName"] for f in files])
    # The hash is written last: it names the version a push is compared against, so it moves on only
    # once the new files are all on disk.
    _clear_the_way(target, HASH_SIDECAR).write_bytes(bundle["hash"])


def _read_directory(skill_id: str, directory: str, note: Optional[str]) -> Dict[str, Any]:
    source = Path(directory)
    if not source.is_dir():
        raise GatewayError(f"directory not found: {source}")

    metadata: Dict[str, Any] = {}
    metadata_path = source / SKILL_JSON
    if metadata_path.is_file():
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        except ValueError as exc:
            raise GatewayError(f"{SKILL_JSON} is not valid JSON: {exc}") from exc
        if not isinstance(metadata, dict):
            raise GatewayError(
                f"{SKILL_JSON} must be a JSON object with the skill's metadata, "
                f"not {type(metadata).__name__}."
            )
    declared_id = (metadata.get("id") or "").strip()
    if declared_id and declared_id != skill_id:
        raise GatewayError(
            f"{SKILL_JSON} declares id '{declared_id}' but the push targets '{skill_id}'. "
            "Make them agree before pushing."
        )

    body_path = source / SKILL_MD
    body = _read_exact(body_path) if body_path.is_file() else ""

    executable = metadata.get("executable") or []
    if not isinstance(executable, list):
        raise GatewayError(
            f"{SKILL_JSON} has an 'executable' entry that is not a list of file paths."
        )
    files = _read_tree(source, [str(p) for p in executable])

    standard_metadata = metadata.get("metadata") or {}
    if not isinstance(standard_metadata, dict):
        raise GatewayError(f"{SKILL_JSON} has a 'metadata' entry that is not an object.")

    return {
        "id": skill_id,
        "name": metadata.get("name") or skill_id,
        "summary": metadata.get("summary") or "",
        "triggers": metadata.get("triggers") or [],
        "bodyMarkdown": body,
        "files": files,
        "license": metadata.get("license"),
        "compatibility": metadata.get("compatibility"),
        "allowedTools": metadata.get("allowedTools"),
        "metadata": standard_metadata,
        "authoredBy": default_authored_by(),
        "changeNote": note,
    }


def push_skill(skill_id: str, directory: str, note: Optional[str], force: bool = False) -> None:
    try:
        client = _client()
        body = _read_directory(skill_id, directory, note)

        if not client.skill_exists(skill_id):
            result = client.create(body)
            verb = "Created"
        else:
            sidecar = Path(directory) / HASH_SIDECAR
            if_match = sidecar.read_text(encoding="utf-8").strip() if sidecar.is_file() else None
            if not if_match and not force:
                raise GatewayError(
                    f"no {HASH_SIDECAR} sidecar in {directory}, so this push cannot prove it builds "
                    "on the current content and could silently overwrite another author's edit. "
                    "Pull first (which writes the sidecar), or pass --force to overwrite "
                    "deliberately."
                )
            result = client.update_draft(skill_id, body, if_match)
            verb = "Updated"
    except GatewayError as ex:
        _fail(str(ex), [
            f"cc-devthrottle skill pull {_ref(skill_id)} --dir {_dir_arg(directory)}",
            axi_cli.help_for("skill push"),
        ])
        return
    except (OSError, UnicodeDecodeError) as ex:
        _fail(
            f"could not read the skill files in {directory}: {ex}. Nothing was pushed.",
            [f"cc-devthrottle skill pull {_ref(skill_id)} --dir {_dir_arg(directory)}"],
        )
        return

    if not isinstance(result, dict):
        result = {}
    new_hash = result.get("contentHash")
    if not isinstance(new_hash, str) or not new_hash:
        # The old sidecar no longer matches the draft, so the next push would be refused as stale.
        _fail(
            f"the draft WAS saved on the Gateway (v{result.get('version')}), but its answer did not "
            f"include the new content hash, so {HASH_SIDECAR} could not be updated. Pull before the "
            "next push.",
            [f"cc-devthrottle skill pull {_ref(skill_id)} --dir {_dir_arg(directory)}"],
        )
        return
    try:
        (Path(directory) / HASH_SIDECAR).write_text(new_hash, encoding="utf-8")
    except OSError as exc:
        _fail(
            f"the draft WAS updated on the Gateway (v{result.get('version')}), but the local "
            f"hash sidecar could not be written: {exc}. Resynchronize before the next push.",
            [f"cc-devthrottle skill pull {_ref(skill_id)} --dir {_dir_arg(directory)}"],
        )
        return
    axi_cli.write_lines(
        f"{verb} draft v{result.get('version')} of '{skill_id}'. "
        "No agent sees it until it publishes: "
        f"cc-devthrottle skill publish {_ref(skill_id)}"
    )
    axi_cli.print_next([
        f"cc-devthrottle skill publish {_ref(skill_id)}",
        f"cc-devthrottle skill versions {_ref(skill_id)}",
    ])


def publish_skill(skill_id: str) -> None:
    try:
        result = _client().publish(skill_id)
    except GatewayError as ex:
        _fail(str(ex), [f"cc-devthrottle skill versions {_ref(skill_id)}", _FIND_A_SKILL])
        return
    axi_cli.write_lines(
        f"Published '{skill_id}' v{result.get('version')}. Every agent on every machine gets this "
        "version on its next fetch - nothing to deploy, nothing to update."
    )
    axi_cli.print_next([
        f"cc-devthrottle skill get {_ref(skill_id)}",
        f"cc-devthrottle skill versions {_ref(skill_id)}",
    ])


def clone_skill(skill_id: str, new_id: str) -> None:
    try:
        result = _client().clone(skill_id, new_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL, axi_cli.help_for("skill clone")])
        return
    axi_cli.write_lines(
        f"Cloned '{skill_id}' into '{result.get('id')}' v{result.get('version')}. "
        "The clone is yours: published, editable, and independent of the original."
    )
    clone_ref = axi_cli.bare(result.get("id"), "<new-id>")
    axi_cli.print_next([
        f'cc-devthrottle skill pull {clone_ref} --dir "<dir>"',
        f"cc-devthrottle skill show {clone_ref}",
    ])


def set_skill_enabled(skill_id: str, enabled: bool) -> None:
    try:
        _client().set_enabled(skill_id, enabled)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL])
        return
    if enabled:
        axi_cli.write_lines(
            f"'{skill_id}' is AVAILABLE again - back in every agent's briefing and fetchable."
        )
        axi_cli.print_next([
            f"cc-devthrottle skill show {_ref(skill_id)}",
            f"cc-devthrottle skill disable {_ref(skill_id)}",
        ])
    else:
        axi_cli.write_lines(
            f"'{skill_id}' is OFF - left out of every agent's briefing and its fetch refused. "
            "Nothing was deleted; switch it back on anytime with: "
            f"cc-devthrottle skill enable {_ref(skill_id)}"
        )
        axi_cli.print_next([f"cc-devthrottle skill enable {_ref(skill_id)}", _FIND_A_SKILL])


def delete_skill(skill_id: str, yes: bool) -> None:
    confirmed = axi_cli.confirm_or_fail(
        f"Archive skill '{skill_id}'? It leaves the register; its versions remain readable "
        "by explicit version.",
        yes,
        "--yes",
    )
    if not confirmed:
        raise typer.Exit(0)
    try:
        _client().delete(skill_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_SKILL])
        return
    axi_cli.write_lines(f"Archived '{skill_id}'.")
    axi_cli.print_next([_FIND_A_SKILL, f"cc-devthrottle skill versions {_ref(skill_id)}"])
