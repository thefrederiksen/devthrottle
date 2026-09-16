"""Gateway workflow operations for cc-devthrottle (Workflows mission, phase 3).

A Workflow is a fleet-wide, cross-agent unit of conduct: markdown instructions plus optional
helper files, stored and versioned on the Gateway, authored mostly by agents through this
command group. The authoring loop round-trips a skill-like directory:

    <dir>/workflow.json      metadata + steps + outcome criteria
    <dir>/instructions.md    the authoritative conduct (markdown)
    <dir>/helpers/*          optional helper files
    <dir>/.workflow-hash     sidecar written by pull; sent as If-Match on push so a stale
                             copy is refused instead of clobbering a concurrent author
"""

from __future__ import annotations

import json
import os
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

from . import axi_cli, bundle_swap  # noqa: E402

TIMEOUT_SECONDS = 15
WORKFLOW_JSON = "workflow.json"
INSTRUCTIONS_MD = "instructions.md"
HELPERS_DIR = "helpers"
HASH_SIDECAR = ".workflow-hash"
# The entries of a pulled or cached workflow directory this command writes, and so may replace.
_OWNED_ENTRIES = (WORKFLOW_JSON, INSTRUCTIONS_MD, HELPERS_DIR, HASH_SIDECAR)

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


class WorkflowClient:
    """Talks to one Gateway's workflow surface."""

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
                f"Gateway not reachable at {self.base_url}. "
                "Check that the Gateway is running, or point this command at another one with "
                "'cc-devthrottle workflow --gateway <url> ...'."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s."
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

    def _json_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            # The shared guard, not a bare resp.json(): a request no endpoint matches falls
            # through to the Gateway's web app and answers HTTP 200 with text/html, and parsing
            # that unguarded is how 'workflow run <short id>' died with a raw JSONDecodeError
            # traceback (issue #2486).
            return gateway.parse_json_body(resp, self.base_url)
        raise GatewayError(self._gateway_message(resp))

    def _text_or_raise(self, resp: requests.Response) -> str:
        if 200 <= resp.status_code < 300:
            # This output lands in an agent's context as the conduct to follow. The same
            # fallthrough issue #2486 hit on the JSON routes answers HTTP 200 with the web app
            # shell here, and printing that as instructions would have an agent obeying a web
            # page that looks like it worked.
            content_type = (resp.headers.get("Content-Type") or "").split(";")[0].strip().lower()
            if content_type == "text/html":
                raise GatewayError(
                    f"the Gateway at {self.base_url} answered with its web app page instead of "
                    "the requested text, so it did not recognise this request. Nothing in that "
                    "answer is the workflow's conduct."
                )
            return resp.text
        raise GatewayError(self._gateway_message(resp))

    # ---- reads --------------------------------------------------------------------------------

    def list_workflows(self) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", "/gateway/workflows"))
        return list(data.get("workflows", []))

    def get_workflow(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/workflows/{workflow_id}"))

    def list_versions(self, workflow_id: str) -> List[Dict[str, Any]]:
        data = self._json_or_raise(
            self._request("GET", f"/gateway/workflows/{workflow_id}/versions")
        )
        return list(data.get("versions", []))

    def workflow_exists(self, workflow_id: str) -> bool:
        """True when the workflow head exists at all - drafts included. GET /{id} cannot answer
        this (it 404s for a draft-only workflow), so existence goes through the versions route."""
        resp = self._request("GET", f"/gateway/workflows/{workflow_id}/versions")
        if resp.status_code == 404:
            return False
        self._json_or_raise(resp)
        return True

    def get_version_detail(self, workflow_id: str, version: int) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("GET", f"/gateway/workflows/{workflow_id}/versions/{version}")
        )

    def get_instructions(self, workflow_id: str, version: Optional[int]) -> str:
        path = f"/gateway/workflows/{workflow_id}/instructions"
        if version is not None:
            path += f"?version={version}"
        return self._text_or_raise(self._request("GET", path))

    # ---- writes -------------------------------------------------------------------------------

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", "/gateway/workflows", body))

    def update_draft(
        self, workflow_id: str, body: Dict[str, Any], if_match: Optional[str]
    ) -> Dict[str, Any]:
        extra = {"If-Match": if_match} if if_match else None
        return self._json_or_raise(
            self._request("PUT", f"/gateway/workflows/{workflow_id}/draft", body, extra)
        )

    def publish(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("POST", f"/gateway/workflows/{workflow_id}/publish")
        )

    def clone(self, workflow_id: str, new_id: str) -> Dict[str, Any]:
        query = urlencode({"newId": new_id, "by": default_authored_by()})
        return self._json_or_raise(
            self._request("POST", f"/gateway/workflows/{workflow_id}/clone?{query}")
        )

    def delete(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("DELETE", f"/gateway/workflows/{workflow_id}")
        )

    def set_enabled(self, workflow_id: str, enabled: bool) -> Dict[str, Any]:
        verb = "enable" if enabled else "disable"
        # A governance change has an actor: the session flipping the switch names itself.
        actor = urllib_quote(default_authored_by())
        return self._json_or_raise(
            self._request("POST", f"/gateway/workflows/{workflow_id}/{verb}?by={actor}")
        )

    # ---- runs (the governance outcome spine, issue #1771) -------------------------------------

    def list_runs(
        self, workflow_id: Optional[str], status: Optional[str], limit: Optional[int] = None
    ) -> List[Dict[str, Any]]:
        params = []
        if workflow_id:
            params.append(f"workflowId={workflow_id}")
        if status:
            params.append(f"status={status}")
        if limit is not None:
            params.append(f"limit={limit}")
        path = "/gateway/workflow-runs" + (("?" + "&".join(params)) if params else "")
        data = self._json_or_raise(self._request("GET", path))
        return list(data.get("runs", []))

    def get_run(self, run_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/workflow-runs/{run_id}"))


_FIND_A_WORKFLOW = "cc-devthrottle workflow list"


def _fail(message: str, next_commands: List[str]) -> None:
    """Report a failure on standard error with what to run next, and exit 1."""
    axi_cli.fail(message, next_commands)


def _ref(workflow_id: str) -> str:
    """The workflow id for a help line: the id the Gateway just accepted, or a placeholder when it
    cannot be pasted back as it is."""
    return axi_cli.bare(workflow_id, "<workflow-id>")


def _dir_arg(directory: str) -> str:
    return axi_cli.quoted(directory, "<dir>")


def _client() -> WorkflowClient:
    return WorkflowClient(base_url=gateway_override)


def _safe_file_name(name: str) -> str:
    """Refuse any server-supplied file name that is not a bare name. The Gateway validates names on
    write, but this CLI must not trust that: a misconfigured, older, or hostile server must not be
    able to steer a write outside the pull or cache directory."""
    if (
        not name
        or name in (".", "..")
        or "/" in name
        or "\\" in name
        or ":" in name
        or name != name.strip()
    ):
        raise GatewayError(f"The Gateway returned an unsafe helper file name: '{name}'.")
    return name


def _write_exact(path: Path, text: str) -> None:
    """Write text with NO newline translation, so pull/push round-trips are value-faithful even for
    content that already contains carriage returns."""
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def _read_exact(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


def _pick_authoring_version(versions: List[Dict[str, Any]]) -> Optional[int]:
    """The version number an author edits next: the draft when one exists, else the published head."""
    for wanted in ("draft", "published"):
        for row in versions:
            if not isinstance(row, dict) or not isinstance(row.get("status"), str):
                raise GatewayError("the Gateway listed a workflow version with no status.")
            if row["status"].lower() == wanted:
                return _version_number(row.get("version"), "listed a workflow version")
    return None


def _version_number(value: Any, what: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise GatewayError(f"the Gateway {what} without a valid version number (got {value!r}).")
    return value


# ---- commands -------------------------------------------------------------------------------------


def list_workflows(json_output: bool) -> None:
    try:
        workflows = _client().list_workflows()
    except GatewayError as ex:
        _fail(str(ex), ["cc-devthrottle setup status"])
        return

    if json_output:
        print(json.dumps({"workflows": workflows}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Version", justify="right")
    table.add_column("Kind")
    table.add_column("State")
    table.add_column("Draft?")
    table.add_column("Summary", overflow="fold")
    for wf in workflows:
        # An older Gateway omits the enabled field; absent means in force.
        state = "OFF" if wf.get("enabled") is False else "in force"
        table.add_row(
            wf.get("id", ""),
            wf.get("name", ""),
            str(wf.get("version", "")),
            "built-in" if wf.get("isBuiltIn") else "custom",
            state,
            "yes" if wf.get("hasDraft") else "",
            wf.get("summary", ""),
        )
    console.print(table)
    console.print(
        "Read one with: cc-devthrottle workflow instructions <id>   "
        "(the raw conduct an agent follows)"
    )


def show_workflow(workflow_id: str, version: Optional[int], json_output: bool) -> None:
    try:
        client = _client()
        if version is not None:
            data = client.get_version_detail(workflow_id, version)
        else:
            data = client.get_workflow(workflow_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, f"cc-devthrottle workflow versions {_ref(workflow_id)}"])
        return

    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"[bold]{data.get('name', workflow_id)}[/bold]  ({workflow_id})")
    if data.get("status"):
        console.print(f"Status: {data['status']}  Version: {data.get('version')}")
    else:
        console.print(
            f"Version: {data.get('version')}  "
            f"Kind: {'built-in' if data.get('isBuiltIn') else 'custom'}  "
            f"Draft waiting: {'yes' if data.get('hasDraft') else 'no'}"
        )
    console.print(f"Summary: {data.get('summary', '')}")
    if data.get("whenToUse"):
        console.print(f"When to use: {data['whenToUse']}")
    if data.get("humanCheckpoint"):
        console.print(f"Human checkpoint: {data['humanCheckpoint']}")
    steps = data.get("steps") or []
    if steps:
        console.print("Steps:")
        for step in steps:
            reviewer = step.get("reviewer") or "no review"
            console.print(
                f"  - {step.get('name')}: doer {step.get('doer')}, {reviewer}; "
                f"done when {step.get('done')}"
            )
    criteria = data.get("outcomeCriteria") or []
    if criteria:
        console.print("Outcome criteria:")
        for criterion in criteria:
            console.print(f"  - {criterion.get('criterionId')}: {criterion.get('description')}")
    files = data.get("files") or []
    if files:
        console.print("Helper files: " + ", ".join(f.get("fileName", "") for f in files))
    console.print(
        f"Instructions: cc-devthrottle workflow instructions {_ref(workflow_id)}"
        + (f" --version {version}" if version is not None else "")
    )


def print_instructions(workflow_id: str, version: Optional[int]) -> None:
    """Print the raw conduct markdown, verbatim - this output goes into an agent's context, so
    nothing is appended, rendered, or wrapped."""
    try:
        markdown = _client().get_instructions(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, f"cc-devthrottle workflow versions {_ref(workflow_id)}"])
        return
    sys.stdout.write(markdown)


def list_versions(workflow_id: str, json_output: bool) -> None:
    try:
        versions = _client().list_versions(workflow_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW])
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


def pull_workflow(workflow_id: str, directory: str, version: Optional[int]) -> None:
    try:
        client = _client()
        if version is None:
            versions = client.list_versions(workflow_id)
            picked = _pick_authoring_version(versions)
            if picked is None:
                _fail(
                    f"Workflow '{workflow_id}' has no versions to pull.",
                    [f"cc-devthrottle workflow versions {_ref(workflow_id)}", _FIND_A_WORKFLOW],
                )
                return
            version = picked
        detail = client.get_version_detail(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, f"cc-devthrottle workflow versions {_ref(workflow_id)}"])
        return

    target = Path(directory)
    try:
        _write_pulled(target, workflow_id, version, detail)
    except GatewayError as ex:
        _fail(str(ex), [f"cc-devthrottle workflow show {_ref(workflow_id)} --version {version}"])
        return
    except OSError as ex:
        _fail(
            f"could not write the workflow into {target}: {ex}",
            [f'cc-devthrottle workflow pull {_ref(workflow_id)} --dir "<writable-dir>"'],
        )
        return

    axi_cli.write_lines(
        f"Pulled '{workflow_id}' v{version} ({detail['status']}) into {target.resolve()}",
        "Edit the files, then push with: "
        f"cc-devthrottle workflow push {_ref(workflow_id)} --dir {_dir_arg(directory)}",
    )
    axi_cli.print_next([f"cc-devthrottle workflow push {_ref(workflow_id)} --dir {_dir_arg(directory)}"])


#: The authored text fields of a version detail. All of them are written to workflow.json and
#: pushed back, so an omitted one would empty that field on the Gateway at the next push.
_REQUIRED_TEXT = (
    "workflowId", "status", "name", "summary", "whenToUse", "humanCheckpoint",
    "instructionsMarkdown", "contentHash",
)
_STEP_TEXT = ("name", "description", "doer", "done")
_CRITERION_TEXT = ("criterionId", "description")


def _checked_bundle(detail: Any, workflow_id: str, version: int) -> Dict[str, Any]:
    """Check the WHOLE version detail before anything on disk is touched.

    A pull or a cache refresh replaces the local helpers with the version's, and a later push sends
    workflow.json back, so a partial or broken answer must be refused outright rather than read as
    "empty": a helper with no content would become an empty file, missing instructions an empty
    instructions.md, a missing list a wiped helpers directory, and a missing name or step list an
    emptied workflow on the next push. Every field the Gateway's version detail carries and these
    writers use must be present with its own type; the answer must be for the workflow and version
    that were asked for."""

    def refuse(what: str) -> GatewayError:
        return GatewayError(f"the Gateway's answer {what}, so nothing was written.")

    def records(field: str, text: tuple, optional: str) -> List[Dict[str, Any]]:
        rows = detail.get(field)
        if not isinstance(rows, list):
            raise refuse(f"did not include the workflow version's '{field}' as a list")
        for index, row in enumerate(rows):
            if (
                not isinstance(row, dict)
                or not all(isinstance(row.get(key), str) for key in text)
                or optional not in row
                or not (row[optional] is None or isinstance(row[optional], str))
            ):
                raise refuse(
                    f"has an entry {index + 1} in '{field}' without all of "
                    f"{', '.join(text + (optional,))}"
                )
        return [{key: row[key] for key in text + (optional,)} for row in rows]

    if not isinstance(detail, dict):
        raise refuse("was not a workflow version")
    for field in _REQUIRED_TEXT:
        if not isinstance(detail.get(field), str):
            raise refuse(f"did not include the workflow version's '{field}' as text")
    if not detail["contentHash"]:
        raise refuse("had an empty 'contentHash'")
    if detail["workflowId"].strip().lower() != workflow_id.strip().lower():
        raise refuse(f"was for workflow '{detail['workflowId']}', not the '{workflow_id}' that was asked for")
    answered = detail.get("version")
    if not isinstance(answered, int) or isinstance(answered, bool) or answered != version:
        raise refuse(f"was for version {answered!r}, not the version {version} that was asked for")
    steps = records("steps", _STEP_TEXT, "reviewer")
    criteria = records("outcomeCriteria", _CRITERION_TEXT, "proofHint")
    entries = detail.get("files")
    if not isinstance(entries, list):
        raise refuse("did not list the version's helper files")

    files: List[Dict[str, str]] = []
    seen = set()
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("fileName"), str):
            raise refuse("lists a helper file with no name")
        # A helper is a bare name inside helpers/, so it cannot reach the files beside that folder.
        name = _safe_file_name(entry["fileName"])
        if name.lower() in seen:
            raise refuse(f"lists the helper file '{name}' twice")
        seen.add(name.lower())
        content = entry.get("content")
        if not isinstance(content, str):
            raise refuse(f"has no content for the helper file '{name}'")
        try:
            content.encode("utf-8")
        except UnicodeEncodeError as exc:
            raise refuse(f"has content for '{name}' that cannot be written as UTF-8 ({exc})") from exc
        files.append({"fileName": name, "content": content})
    return {
        "instructions": detail["instructionsMarkdown"],
        "contentHash": detail["contentHash"],
        "files": files,
        "metadata": {
            "id": detail["workflowId"],
            "name": detail["name"],
            "summary": detail["summary"],
            "whenToUse": detail["whenToUse"],
            "humanCheckpoint": detail["humanCheckpoint"],
            "steps": steps,
            "outcomeCriteria": criteria,
        },
    }


def _write_bundle(root: Path, bundle: Dict[str, Any]) -> None:
    """Write checked instructions, helpers and hash sidecar into an empty directory."""
    _write_exact(root / INSTRUCTIONS_MD, bundle["instructions"])
    if bundle["files"]:
        helpers = root / HELPERS_DIR
        helpers.mkdir()
        for f in bundle["files"]:
            _write_exact(helpers / f["fileName"], f["content"])
    (root / HASH_SIDECAR).write_text(bundle["contentHash"], encoding="utf-8")


def _write_pulled(target: Path, workflow_id: str, version: int, detail: Dict[str, Any]) -> None:
    """Write one pulled version into `target`. The whole answer is checked BEFORE anything is
    written, and the new files replace the old ones in one swap, so a bad answer or a failed write
    leaves the directory exactly as it was.

    The helpers directory mirrors the SERVER: a helper another author deleted on the Gateway must
    not survive locally and be resurrected by the next push. Files in `target` this command does not
    own are left alone."""
    bundle = _checked_bundle(detail, workflow_id, version)
    metadata = bundle["metadata"]

    def build(staging: Path) -> None:
        (staging / WORKFLOW_JSON).write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        _write_bundle(staging, bundle)

    bundle_swap.replace_directory(target, build, _OWNED_ENTRIES)


def _read_directory(workflow_id: str, directory: str, note: Optional[str]) -> Dict[str, Any]:
    source = Path(directory)
    if not source.is_dir():
        raise GatewayError(f"Directory not found: {source}")
    # A pull that was interrupted is put right first, so a push never sends a half-replaced workflow.
    bundle_swap.recover(source)

    metadata: Dict[str, Any] = {}
    metadata_path = source / WORKFLOW_JSON
    if metadata_path.is_file():
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        except ValueError as exc:
            raise GatewayError(f"{WORKFLOW_JSON} is not valid JSON: {exc}") from exc
        if not isinstance(metadata, dict):
            raise GatewayError(
                f"{WORKFLOW_JSON} must be a JSON object with the workflow's metadata, "
                f"not {type(metadata).__name__}."
            )
    declared_id = (metadata.get("id") or "").strip()
    if declared_id and declared_id != workflow_id:
        raise GatewayError(
            f"{WORKFLOW_JSON} declares id '{declared_id}' but the push targets '{workflow_id}'. "
            "Make them agree before pushing."
        )

    instructions_path = source / INSTRUCTIONS_MD
    instructions = _read_exact(instructions_path) if instructions_path.is_file() else ""

    files: List[Dict[str, str]] = []
    helpers = source / HELPERS_DIR
    if helpers.is_dir():
        for path in sorted(helpers.iterdir()):
            if path.is_file():
                files.append({"fileName": path.name, "content": _read_exact(path)})

    return {
        "id": workflow_id,
        "name": metadata.get("name") or workflow_id,
        "summary": metadata.get("summary") or "",
        "whenToUse": metadata.get("whenToUse") or "",
        "humanCheckpoint": metadata.get("humanCheckpoint") or "",
        "steps": metadata.get("steps") or [],
        "outcomeCriteria": metadata.get("outcomeCriteria") or [],
        "instructionsMarkdown": instructions,
        "files": files,
        "authoredBy": default_authored_by(),
        "changeNote": note,
    }


def push_workflow(workflow_id: str, directory: str, note: Optional[str], force: bool = False) -> None:
    try:
        client = _client()
        body = _read_directory(workflow_id, directory, note)

        if not client.workflow_exists(workflow_id):
            result = client.create(body)
            verb = "Created"
        else:
            sidecar = Path(directory) / HASH_SIDECAR
            if_match = (
                sidecar.read_text(encoding="utf-8").strip() if sidecar.is_file() else None
            )
            if not if_match and not force:
                raise GatewayError(
                    f"No {HASH_SIDECAR} sidecar in {directory}, so this push cannot prove it "
                    "builds on the current content and could silently overwrite another "
                    "author's edit. Pull first (which writes the sidecar), or pass --force to "
                    "overwrite deliberately."
                )
            result = client.update_draft(workflow_id, body, if_match)
            verb = "Updated"
    except GatewayError as ex:
        _fail(str(ex), [
            f"cc-devthrottle workflow pull {_ref(workflow_id)} --dir {_dir_arg(directory)}",
            axi_cli.help_for("workflow push"),
        ])
        return
    except (OSError, UnicodeDecodeError) as ex:
        _fail(
            f"could not read the workflow files in {directory}: {ex}. Nothing was pushed.",
            [f"cc-devthrottle workflow pull {_ref(workflow_id)} --dir {_dir_arg(directory)}"],
        )
        return

    if not isinstance(result, dict):
        result = {}
    new_hash = result.get("contentHash")
    if not isinstance(new_hash, str) or not new_hash:
        # The old sidecar no longer matches the draft, so the next push would be refused as stale.
        _fail(
            f"The draft WAS saved on the Gateway (v{result.get('version')}), but its answer did not "
            f"include the new content hash, so {HASH_SIDECAR} could not be updated. Pull before the "
            "next push.",
            [f"cc-devthrottle workflow pull {_ref(workflow_id)} --dir {_dir_arg(directory)}"],
        )
        return
    try:
        (Path(directory) / HASH_SIDECAR).write_text(new_hash, encoding="utf-8")
    except OSError as exc:
        _fail(
            f"The draft WAS updated on the Gateway (v{result.get('version')}), but the local "
            f"hash sidecar could not be written: {exc}. Resynchronize before the next push.",
            [f"cc-devthrottle workflow pull {_ref(workflow_id)} --dir {_dir_arg(directory)}"],
        )
        return
    axi_cli.write_lines(
        f"{verb} draft v{result.get('version')} of '{workflow_id}'. "
        "Nothing changes for the fleet until it publishes: "
        f"cc-devthrottle workflow publish {_ref(workflow_id)}"
    )
    axi_cli.print_next([
        f"cc-devthrottle workflow publish {_ref(workflow_id)}",
        f"cc-devthrottle workflow versions {_ref(workflow_id)}",
    ])


def publish_workflow(workflow_id: str) -> None:
    try:
        result = _client().publish(workflow_id)
    except GatewayError as ex:
        _fail(str(ex), [f"cc-devthrottle workflow versions {_ref(workflow_id)}", _FIND_A_WORKFLOW])
        return
    axi_cli.write_lines(
        f"Published '{workflow_id}' v{result.get('version')}. "
        "It is now the version every machine and agent reads."
    )
    axi_cli.print_next([
        f"cc-devthrottle workflow instructions {_ref(workflow_id)}",
        f"cc-devthrottle workflow versions {_ref(workflow_id)}",
    ])


# reset_workflow was retired with the Shared Workflow Library phase 3: built-ins are read-only,
# can never diverge from the shipped content, and have nothing to reset.


def clone_workflow(workflow_id: str, new_id: str) -> None:
    try:
        result = _client().clone(workflow_id, new_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, axi_cli.help_for("workflow clone")])
        return
    axi_cli.write_lines(
        f"Cloned '{workflow_id}' into '{result.get('id')}' v{result.get('version')}. "
        "The clone is yours: published, editable, and independent of the original."
    )
    clone_ref = axi_cli.bare(result.get("id"), "<new-id>")
    axi_cli.print_next([
        f'cc-devthrottle workflow pull {clone_ref} --dir "<dir>"',
        f"cc-devthrottle workflow show {clone_ref}",
    ])


def set_workflow_enabled(workflow_id: str, enabled: bool) -> None:
    try:
        _client().set_enabled(workflow_id, enabled)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW])
        return
    if enabled:
        axi_cli.write_lines(
            f"'{workflow_id}' is IN FORCE again - back in every agent's briefing, runs and seats allowed."
        )
        axi_cli.print_next([
            f"cc-devthrottle workflow show {_ref(workflow_id)}",
            f"cc-devthrottle workflow disable {_ref(workflow_id)}",
        ])
    else:
        axi_cli.write_lines(
            f"'{workflow_id}' is OFF - hidden from agents' briefings, no new runs or seats. "
            "Nothing was deleted; re-enable anytime with: "
            f"cc-devthrottle workflow enable {_ref(workflow_id)}"
        )
        axi_cli.print_next([
            f"cc-devthrottle workflow enable {_ref(workflow_id)}",
            _FIND_A_WORKFLOW,
        ])


def delete_workflow(workflow_id: str, yes: bool) -> None:
    confirmed = axi_cli.confirm_or_fail(
        f"Archive workflow '{workflow_id}'? It leaves the catalog; its versions remain "
        "as pinned history.",
        yes,
        "--yes",
    )
    if not confirmed:
        raise typer.Exit(0)
    try:
        _client().delete(workflow_id)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW])
        return
    axi_cli.write_lines(f"Archived '{workflow_id}'.")
    axi_cli.print_next([_FIND_A_WORKFLOW, f'cc-devthrottle workflow push <new-id> --dir "<dir>"'])


def list_runs(workflow_id: Optional[str], status: Optional[str], json_output: bool) -> None:
    try:
        runs = _client().list_runs(workflow_id, status)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, axi_cli.help_for("workflow runs")])
        return

    if json_output:
        print(json.dumps({"runs": runs}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Run id")
    table.add_column("Workflow")
    table.add_column("V", justify="right")
    table.add_column("Name", overflow="fold")
    table.add_column("Status")
    table.add_column("Acceptance")
    table.add_column("Created (UTC)")
    for run in runs:
        table.add_row(
            (run.get("id") or "")[:8],
            run.get("workflowId", ""),
            str(run.get("workflowVersion", "")),
            run.get("name", ""),
            run.get("status", ""),
            run.get("acceptanceStatus", ""),
            (run.get("createdUtc") or "").replace("T", " ")[:19],
        )
    console.print(table)
    console.print("Details with: cc-devthrottle workflow run <run id>")


#: One list read returns at most this many runs - the server's own hard ceiling
#: (WorkflowRunStore.MaxListLimit), in place since the runs endpoint was born. Requesting exactly
#: this many makes the answer PROVE its own completeness: fewer rows back means the whole history
#: was seen, exactly this many means there may be more beyond the page.
RUN_LIST_PROOF_LIMIT = 1000


def _resolve_run_id(client: WorkflowClient, run_id: str) -> str:
    """The full run id for what the user typed, resolved the way session ids are.

    'workflow runs' prints run ids truncated to eight characters and tells the reader to use
    them, so a short prefix is a first-class input here. It also cannot be sent to the Gateway
    raw: the run route only matches a full id ('/gateway/workflow-runs/{id:guid}'), and an
    unmatched path falls through to the Gateway's web app with HTTP 200 (issue #2486). Unknown
    and ambiguous prefixes are refused in one sentence, like every other lookup on this tool.

    Runs are retained forever and one list read is capped, so resolution only trusts a list
    that PROVED itself complete (came back smaller than RUN_LIST_PROOF_LIMIT). Against a
    possibly-truncated page, a lone match may have an invisible older twin and a miss may hide
    an older hit - both are refused rather than guessed. More than one match is ambiguous no
    matter what lies beyond the page, so that keeps its more specific error.
    """
    candidate = run_id.strip().lower()
    if not candidate:
        raise GatewayError("A run id, or a unique prefix of one, is required.")
    if len(candidate) == 36:
        return candidate
    runs = client.list_runs(None, None, RUN_LIST_PROOF_LIMIT)
    matches = sorted(
        {
            (run.get("id") or "")
            for run in runs
            if (run.get("id") or "").lower().startswith(candidate)
        }
    )
    if len(matches) > 1:
        shown = ", ".join(matches[:5]) + (
            f", and {len(matches) - 5} more" if len(matches) > 5 else ""
        )
        raise GatewayError(
            f"'{run_id}' matches {len(matches)} workflow runs: {shown}. "
            "Give more of the id."
        )
    if len(runs) >= RUN_LIST_PROOF_LIMIT:
        raise GatewayError(
            f"The Gateway holds at least {RUN_LIST_PROOF_LIMIT} workflow runs - more than one "
            "list read returns - so a short prefix cannot be proven unique or absent against "
            "the whole history. Give the full run id "
            "('cc-devthrottle workflow runs --json' prints full ids)."
        )
    if not matches:
        raise GatewayError(
            f"No workflow run matches '{run_id}'. "
            "List them with: cc-devthrottle workflow runs"
        )
    return matches[0]


def show_run(run_id: str, json_output: bool) -> None:
    try:
        client = _client()
        run = client.get_run(_resolve_run_id(client, run_id))
    except GatewayError as ex:
        _fail(str(ex), ["cc-devthrottle workflow runs --json"])
        return

    if json_output:
        print(json.dumps(run, indent=2))
        return

    console.print(f"[bold]{run.get('name', '')}[/bold]  (run {run.get('id')})")
    console.print(
        f"Workflow: {run.get('workflowId')} v{run.get('workflowVersion')}  "
        f"Status: {run.get('status')}  Acceptance: {run.get('acceptanceStatus')}"
    )
    if run.get("acceptedBy"):
        console.print(f"Accepted by: {run['acceptedBy']} at {run.get('acceptedUtc')}")
    if run.get("outcome"):
        console.print(f"Outcome: {run['outcome']}")
    if run.get("missionId"):
        console.print(f"Mission: {run['missionId']}")
    criteria = run.get("criteriaResults") or []
    if criteria:
        console.print("Criteria:")
        for c in criteria:
            proof = f"  proof {c['proofUrl']}" if c.get("proofUrl") else ""
            console.print(f"  - {c.get('criterionId')}: {c.get('status')}{proof}")
    participants = run.get("participants") or []
    if participants:
        console.print("Participants:")
        for p in participants:
            left = f" (left {p['leftUtc']})" if p.get("leftUtc") else ""
            console.print(
                f"  - {p.get('role') or '?'} {p.get('sessionId')} "
                f"[{p.get('agentKind')}] on {p.get('machine')}{left}"
            )
    links = run.get("proofLinks") or []
    if links:
        console.print("Proof links:")
        for link in links:
            console.print(f"  - {link.get('label') or 'link'}: {link.get('url')}")


def materialize_workflow(workflow_id: str, version: Optional[int]) -> None:
    """Write a version's bundle to the per-machine cache and print the absolute paths, so an
    agent can run helper files with its own shell. Version-stamped and hash-keyed: a bundle
    already on disk with the right hash is not rewritten."""
    try:
        client = _client()
        if version is None:
            head = client.get_workflow(workflow_id)
            if not isinstance(head, dict):
                raise GatewayError("the Gateway's answer was not a workflow.")
            version = _version_number(head.get("version"), "described the workflow")
        detail = client.get_version_detail(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex), [_FIND_A_WORKFLOW, f"cc-devthrottle workflow versions {_ref(workflow_id)}"])
        return

    try:
        bundle = _checked_bundle(detail, workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex), [f"cc-devthrottle workflow show {_ref(workflow_id)} --version {version}"])
        return
    # Checked above: an omitted status is refused rather than read as "not a draft".
    if detail["status"].lower() == "draft":
        _fail(
            f"Version {version} of '{workflow_id}' is a draft. Only published history can be "
            "materialized - publish it first.",
            [
                f"cc-devthrottle workflow publish {_ref(workflow_id)}",
                f"cc-devthrottle workflow versions {_ref(workflow_id)}",
            ],
        )
        return

    local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
    root = Path(local_app_data) / "cc-director" / "workflows" / workflow_id / str(version)
    hash_file = root / HASH_SIDECAR
    expected = bundle["contentHash"]
    files = bundle["files"]

    try:
        # An interrupted refresh is put right before the cache is judged.
        bundle_swap.recover(root)
    except OSError as ex:
        _fail(
            f"could not repair the workflow cache at {root}: {ex}",
            [f"cc-devthrottle workflow instructions {_ref(workflow_id)} --version {version}"],
        )
        return
    # The sidecar alone is not proof the bundle is intact - every listed file must actually exist,
    # or a deleted/half-written cache would be reported as materialized forever.
    intact = (
        hash_file.is_file()
        and hash_file.read_text(encoding="utf-8").strip() == expected
        and (root / INSTRUCTIONS_MD).is_file()
        and all((root / HELPERS_DIR / f["fileName"]).is_file() for f in files)
    )
    lines: List[str] = []
    if intact:
        lines.append(f"Already materialized: {root}")
    else:
        try:
            bundle_swap.replace_directory(root, lambda staging: _write_bundle(staging, bundle), _OWNED_ENTRIES)
        except OSError as ex:
            _fail(
                f"could not write the workflow cache at {root}: {ex}",
                [f"cc-devthrottle workflow instructions {_ref(workflow_id)} --version {version}"],
            )
            return
        lines.append(f"Materialized '{workflow_id}' v{version} into {root}")

    lines.append(f"Instructions: {root / INSTRUCTIONS_MD}")
    helpers_dir = root / HELPERS_DIR
    if helpers_dir.is_dir():
        for path in sorted(helpers_dir.iterdir()):
            lines.append(f"Helper: {path}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"cc-devthrottle workflow instructions {_ref(workflow_id)} --version {version}"])
