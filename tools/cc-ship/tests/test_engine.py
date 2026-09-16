"""The cc-ship run, end to end, against a real git repository and a scripted fleet and
GitHub (issue 2935, acceptance criteria). The live run is proven separately."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import cli  # noqa: E402
import engine  # noqa: E402
import fleet  # noqa: E402
import runstore  # noqa: E402

AUTHOR = "author-0000"

PY = f'"{sys.executable}"'

SHIP_YAML = {
    "checks": [f"{PY} -c \"import sys; sys.exit(0)\""],
    "rules": ["No secrets."],
    "reviewer_agent": "Codex",
    "verifier_agent": "ClaudeCode",
    "verify": {"surface": "none"},
    "docs_only_paths": ["*.md"],
    "risk": {"high_paths": ["website/api/**"]},
}


def git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                          text=True).stdout.strip()


def review(*findings: dict, risk: str = "low") -> dict:
    return {"risk_level": risk, "risk_reason": "small change", "checked": ["traced it"],
            "not_covered": ["nothing run"], "simplification": [], "findings": list(findings)}


def finding(fid: str, title: str, action: str = "auto-fix", severity: str = "error") -> dict:
    return {"id": fid, "title": title, "file": "app.py", "line": 1,
            "sequence": "call it with 3 -> wrong", "severity": severity, "action": action,
            "remedy": "do it right"}


GO = {"verdict": "go", "scenarios": [
    {"name": "runs", "result": "pass", "live": True, "evidence": "shot.png", "reason": ""}]}


class World:
    """A scripted fleet and GitHub. Each spawned session writes the next queued output."""

    def __init__(self, tmp: Path):
        self.tmp = tmp
        self.outputs: dict[str, list] = {"Reviewer": [], "Verifier": []}
        self.spawned: list[dict] = []
        self.prompts: list[tuple[str, str]] = []
        self.stopped: list[str] = []
        self.failed_checks: list[str] = []
        self.prs: dict[int, dict] = {}
        self.merged: list[int] = []

    # fleet
    def spawn_session(self, repo, agent, controlled_by, name, brief):
        sid = f"s{len(self.spawned) + 1}"
        role = name.split(" - ")[1]
        self.spawned.append({"id": sid, "agent": agent, "name": name, "role": role,
                             "brief": Path(brief), "controlled_by": controlled_by})
        return sid

    def _session(self, sid):
        return next(s for s in self.spawned if s["id"] == sid)

    def wait_for_output(self, sid, output, timeout, poll, written_after=None, watch=None):
        role = self._session(sid)["role"]
        payload = self.outputs[role].pop(0)
        if payload == "CRASH":
            return fleet.WaitResult(fleet.CRASHED, "left the fleet list", [])
        output.write_text(payload if isinstance(payload, str) else json.dumps(payload),
                          encoding="utf-8")
        return fleet.WaitResult(fleet.FINISHED, "output written and flagged done", [])

    def find_session(self, sid):
        if sid == AUTHOR:
            return {"sessionId": AUTHOR, "agent": "ClaudeCode", "missionName": "Test Mission"}
        return None

    # github
    def open_pr_for_branch(self, slug, branch):
        for number, pr in self.prs.items():
            if pr["branch"] == branch and pr["state"] == "OPEN":
                return {"number": number, "url": pr["url"]}
        return None

    def create_pr(self, slug, branch, title, body_file):
        number = len(self.prs) + 1
        self.prs[number] = {"branch": branch, "title": title, "state": "OPEN",
                            "url": f"https://github.com/{slug}/pull/{number}",
                            "body": Path(body_file).read_text(encoding="ascii")}
        return {"number": number, "url": self.prs[number]["url"]}

    def edit_pr_body(self, slug, number, body_file):
        self.prs[number]["body"] = Path(body_file).read_text(encoding="ascii")

    def merge_pr(self, slug, number, head, subject, body):
        self.prs[number].update(state="MERGED", head=head, subject=subject)
        self.merged.append(number)

    def pr_state(self, slug, number):
        pr = self.prs[number]
        return {"state": pr["state"], "headRefOid": pr.get("head", ""),
                "mergeCommit": {"oid": "abc"}, "url": pr["url"]}


@pytest.fixture
def world(tmp_path, monkeypatch):
    origin = tmp_path / "origin.git"
    subprocess.run(["git", "init", "--bare", "-b", "main", str(origin)], check=True,
                   capture_output=True)
    work = tmp_path / "work"
    subprocess.run(["git", "clone", str(origin), str(work)], check=True, capture_output=True)
    git(work, "config", "user.email", "t@example.com")
    git(work, "config", "user.name", "t")
    git(work, "switch", "-c", "main")
    (work / ".ship.yaml").write_text(json.dumps(SHIP_YAML), encoding="ascii")
    (work / "app.py").write_text("x = 1\n", encoding="ascii")
    git(work, "add", ".")
    git(work, "commit", "-m", "base")
    git(work, "push", "-u", "origin", "main")
    git(work, "switch", "-c", "feature")
    (work / "app.py").write_text("x = 2\n", encoding="ascii")
    git(work, "commit", "-am", "change x")
    (tmp_path / "intent.md").write_text("# Intent\n\nGoal: x should be 2.\n", encoding="ascii")

    w = World(tmp_path)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path / "data"))
    monkeypatch.setenv("CC_SESSION_ID", AUTHOR)
    monkeypatch.chdir(work)
    for name in ("spawn_session", "wait_for_output", "find_session"):
        monkeypatch.setattr(engine.fleet, name, getattr(w, name))
    monkeypatch.setattr(engine.fleet, "clear_done_flag", lambda sid: "cleared")
    monkeypatch.setattr(engine.fleet, "prompt_session", lambda sid, text: w.prompts.append((sid, text)))
    monkeypatch.setattr(engine.fleet, "stop_session", lambda sid, reason: w.stopped.append(sid))
    monkeypatch.setattr(engine.trust, "require_trust", lambda agent, root: None)
    monkeypatch.setattr(engine.gitops, "remote_slug", lambda repo: "o/r")
    for name in ("open_pr_for_branch", "create_pr", "edit_pr_body", "merge_pr", "pr_state"):
        monkeypatch.setattr(engine.github, name, getattr(w, name))
    monkeypatch.setattr(engine.github, "failed_checks", lambda slug, n: list(w.failed_checks))
    monkeypatch.setattr(engine.github, "delete_remote_branch", lambda slug, b: None)
    w.work = work
    w.intent = tmp_path / "intent.md"
    return w


def run_cli(*args: str, capsys) -> tuple[int, dict]:
    code = cli.main([*args, "--json"])
    out = capsys.readouterr().out
    return code, json.loads(out)


def ship_to_merge(world, capsys):
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert out["state"] == "working", out
    return run_cli("wait", "--seconds", "5", capsys=capsys)


# ------------------------------------------------------------------ the happy path

def test_run_CleanReviewAndGoVerify_MergesWithAttestedHead(world, capsys):
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert code == 0 and out["state"] == "merged", out
    pr = world.prs[1]
    head = git(world.work, "rev-parse", "HEAD")
    attestation = json.loads(pr["body"].split("<!-- ship-attestation:v1 ")[1].split(" -->")[0])
    assert attestation["head"] == head == pr["head"]
    assert attestation["steps"] == {s: "completed" for s in ("sync", "checks", "review", "verify")}
    assert "(#1)" in pr["subject"]


def test_run_ReviewerAndVerifier_AreTrackedOtherFamilySessionsNamedForTheMission(world, capsys):
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    ship_to_merge(world, capsys)
    reviewer, verifier = world.spawned
    assert reviewer["agent"] == "Codex"
    assert reviewer["name"] == "Test Mission - Reviewer - ship feature"
    assert verifier["name"] == "Test Mission - Verifier - ship feature"
    assert all(s["controlled_by"] == AUTHOR for s in world.spawned)


# ------------------------------------------------------------------ refusals

def test_start_DirtyTree_Refused(world, capsys):
    (world.work / "app.py").write_text("dirty\n", encoding="ascii")
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert code != 0 and out["code"] == "dirty-tree" and out["help"]


def test_start_OnMain_Refused(world, capsys):
    git(world.work, "switch", "main")
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert code != 0 and out["code"] == "on-main"


def test_start_EmptyIntent_Refused(world, capsys):
    world.intent.write_text("# Intent\n\n", encoding="ascii")
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert code != 0 and out["code"] == "empty-intent"


def test_cli_UnknownFlag_FailsLoudly(world, capsys):
    code, out = run_cli("status", "--bogus", capsys=capsys)
    assert code == 2 and out["code"] == "usage" and "--bogus" in out["error"]


def test_run_ReviewerSameFamilyAsAuthor_Refused(world, capsys):
    cfg = dict(SHIP_YAML, reviewer_agent="ClaudeCode")
    git(world.work, "switch", "main")
    (world.work / ".ship.yaml").write_text(json.dumps(cfg), encoding="ascii")
    git(world.work, "commit", "-am", "same family")
    git(world.work, "push")
    git(world.work, "switch", "feature")
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert code != 0 and out["failure"]["code"] == "same-family"
    assert world.spawned == []


# ------------------------------------------------------------------ negative control

def test_run_PlantedDefect_FixedAndReReviewedInANewSession(world, capsys):
    world.outputs["Reviewer"] += [review(finding("F1", "x is wrong")), review()]
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-author" and "x is wrong" in out["next_step"]

    code, out = run_cli("continue", capsys=capsys)
    assert code != 0 and out["code"] == "nothing-new"

    (world.work / "app.py").write_text("x = 3\n", encoding="ascii")
    git(world.work, "commit", "-am", "fix x")
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged", out
    reviewers = [s for s in world.spawned if s["role"] == "Reviewer"]
    assert len(reviewers) == 2 and reviewers[0]["id"] != reviewers[1]["id"]
    assert "RE-REVIEW" in reviewers[1]["brief"].read_text(encoding="ascii")
    assert "r1 fixed: x is wrong" in world.prs[1]["body"]


# ------------------------------------------------------------------ owner calls

def test_run_AskOwner_ParksThenKeepIsRecordedAndNeverRaisedAgain(world, capsys):
    ask = finding("F1", "Should x be 2 at all", action="ask-owner", severity="warning")
    world.outputs["Reviewer"].append(review(ask))
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-owner"
    assert "[r1-F1] Should x be 2 at all" in out["next_step"]

    world.outputs["Verifier"].append(GO)
    code, out = run_cli("respond", "r1-F1", "--keep", "--note", "yes, 2", capsys=capsys)
    assert out["state"] == "working"
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged"
    assert 'OWNER KEPT: Should x be 2 at all (owner: "yes, 2")' in world.prs[1]["body"]

    # A later run on the same branch: the reviewer raises it again, cc-ship does not.
    (world.work / "app.py").write_text("x = 4\n", encoding="ascii")
    git(world.work, "commit", "-am", "more")
    world.outputs["Reviewer"].append(review(ask))
    world.outputs["Verifier"].append(GO)
    world.prs[1]["state"] = "MERGED"
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out


# ------------------------------------------------------------------ skipped steps and checks

def _set_main_config(world, cfg):
    git(world.work, "switch", "main")
    (world.work / ".ship.yaml").write_text(json.dumps(cfg), encoding="ascii")
    git(world.work, "commit", "-am", "config")
    git(world.work, "push")
    git(world.work, "switch", "feature")


def test_run_SkippedChecks_ParksAndShowsSkipped(world, capsys):
    _set_main_config(world, dict(SHIP_YAML, checks=[]))
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-owner" and world.merged == []
    assert "SKIPPED" in world.prs[1]["body"] and "SKIPPED" in out["next_step"]


def test_run_PendingChecksOnly_MergesWithoutWaiting(world, capsys):
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    world.failed_checks = []  # pending and absent checks are not failures
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged"


def test_run_FailedCheck_RefusesMerge(world, capsys):
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    world.failed_checks = ["Build & Test"]
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-author" and world.merged == []
    assert "Build & Test" in out["next_step"]


def test_run_LocalCheckFails_BackToAuthorWithOutput(world, capsys):
    _set_main_config(world, dict(SHIP_YAML, checks=[f"{PY} -c \"print('lint broke'); raise SystemExit(3)\""]))
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert out["state"] == "waiting-on-author" and "lint broke" in out["next_step"]
    assert world.spawned == []


def test_run_ShipYamlEditedOnBranch_ChecksStillComeFromMain(world, capsys):
    weak = dict(SHIP_YAML, checks=[], risk={"high_paths": []})
    (world.work / ".ship.yaml").write_text(json.dumps(weak), encoding="ascii")
    (world.work / "website" / "api").mkdir(parents=True)
    (world.work / "website" / "api" / "x.js").write_text("x\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "weaken the gate")
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    run = runstore.load(out["run"])
    assert run["steps"]["checks"] == "completed"   # main's check ran, not the branch's none
    assert out["risk"] == "high"                   # main's high path still applies


# ------------------------------------------------------------------ risk

def test_run_WebsiteApiChange_RatedHighAndParked(world, capsys):
    (world.work / "website" / "api").mkdir(parents=True)
    (world.work / "website" / "api" / "pay.js").write_text("x\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "api")
    world.outputs["Reviewer"].append(review(risk="low"))
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-owner" and out["risk"] == "high" and world.merged == []
    assert "Raised by rule: touches a high-risk path: website/api/pay.js" in world.prs[1]["body"]


def test_resume_ParkedAndOwnerMerged_Merged(world, capsys):
    (world.work / "website" / "api").mkdir(parents=True)
    (world.work / "website" / "api" / "pay.js").write_text("x\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "api")
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    ship_to_merge(world, capsys)
    code, out = run_cli("continue", capsys=capsys)
    assert code != 0 and out["code"] == "still-parked"
    world.prs[1].update(state="MERGED", head=git(world.work, "rev-parse", "HEAD"))
    code, out = run_cli("continue", capsys=capsys)
    assert out["state"] == "merged"


# ------------------------------------------------------------------ invalid output

def test_run_InvalidReviewThreeTimes_FailsLoudlyAfterTwoCorrections(world, capsys):
    bad = {"risk_level": "severe"}
    world.outputs["Reviewer"] += [bad, bad, bad]
    code, out = ship_to_merge(world, capsys)
    assert code != 0
    assert out["code"] == "invalid-output" and out["error"] and out["help"]
    assert len(world.prompts) == 2
    assert len([s for s in world.spawned if s["role"] == "Reviewer"]) == 1


def test_run_InvalidReviewOnce_CorrectedBySameSession(world, capsys):
    world.outputs["Reviewer"] += ["not json", review()]
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged"
    assert len(world.prompts) == 1 and world.prompts[0][0] == "s1"


# ------------------------------------------------------------------ crashes and verify

def test_run_ReviewerCrashesTwice_FailsAfterOneReplacement(world, capsys):
    world.outputs["Reviewer"] += ["CRASH", "CRASH"]
    code, out = ship_to_merge(world, capsys)
    assert code != 0 and out["state"] == "failed"
    run = runstore.load(out["run"])
    assert run["failure"]["code"] == "session-failed"
    assert len(world.spawned) == 2


def test_run_VerifyNoGo_BackToAuthorThenFullReReview(world, capsys):
    no_go = {"verdict": "no-go", "scenarios": [
        {"name": "page loads", "result": "fail", "live": True, "evidence": "500 error", "reason": ""}]}
    world.outputs["Reviewer"] += [review(), review()]
    world.outputs["Verifier"] += [no_go, GO]
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-author" and "page loads" in out["next_step"]
    (world.work / "app.py").write_text("x = 5\n", encoding="ascii")
    git(world.work, "commit", "-am", "fix page")
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged"
    assert [s["role"] for s in world.spawned] == ["Reviewer", "Verifier", "Reviewer", "Verifier"]


def test_run_DocsOnlyChange_ProceedsWithNoSurface(world, capsys):
    (world.work / "notes.md").write_text("hello\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "reset", "-q", "--soft", "HEAD~1")
    git(world.work, "restore", "--staged", "app.py")
    git(world.work, "checkout", "app.py")
    git(world.work, "commit", "-m", "docs only")
    world.outputs["Reviewer"].append(review())
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out
    assert "Nothing to run live: documents only" in world.prs[1]["body"]
    assert [s["role"] for s in world.spawned] == ["Reviewer"]


def test_output_IsAsciiEvenWhenTheReviewerWritesUnicode(world, capsys):
    world.outputs["Reviewer"].append(review(finding("F1", "x — wrong “quoted” 中")))
    cli.main(["start", "--intent", str(world.intent)])
    cli.main(["wait", "--seconds", "5"])
    out = capsys.readouterr().out
    out.encode("ascii")  # raises if not ASCII
    assert 'x - wrong "quoted" ?' in out


# ------------------------------------------------------------------ evidence and preview

def test_run_VerifierEvidence_PublishedToShipEvidenceBranchAndLinked(world, capsys):
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    real_wait = world.wait_for_output

    def wait_and_screenshot(sid, output, *args, **kwargs):
        if world._session(sid)["role"] == "Verifier":
            (output.parent / "evidence" / "shot.png").write_bytes(b"\x89PNG fake")
        return real_wait(sid, output, *args, **kwargs)

    engine.fleet.wait_for_output = wait_and_screenshot
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged"
    origin = world.tmp / "origin.git"
    listed = git(origin, "ls-tree", "-r", "--name-only", "ship-evidence")
    assert listed == f"runs/{out['run']}/shot.png"
    commit = git(origin, "rev-parse", "ship-evidence")
    assert f"[shot.png](https://github.com/o/r/blob/{commit}/runs/{out['run']}/shot.png)" in world.prs[1]["body"]
    # The evidence branch shares no history with main: it never merges.
    assert subprocess.run(["git", "-C", str(origin), "merge-base", "main", "ship-evidence"],
                          capture_output=True).returncode != 0


def test_run_VercelPreview_WaitsForItThenRemovesTheCookieFile(world, capsys, monkeypatch):
    _set_main_config(world, dict(SHIP_YAML, verify={"surface": "vercel-preview"}))
    answers = [None, "https://feature.vercel.app"]
    looked = []

    def find_preview(slug, sha):
        looked.append(sha)
        return answers[min(len(looked), len(answers)) - 1]

    monkeypatch.setattr(engine.preview, "find_preview_url", find_preview)
    written = []
    monkeypatch.setattr(engine.preview, "write_bypass_state",
                        lambda url, path: (path.write_text("{}"), written.append(path)))
    monkeypatch.setattr(engine.gitops, "push_branch", lambda repo, branch: None)
    monkeypatch.setattr(engine, "POLL_SECONDS", 0)
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged", out
    assert len(looked) == 2  # no preview yet, then the preview
    brief = next(s for s in world.spawned if s["role"] == "Verifier")["brief"].read_text()
    assert "https://feature.vercel.app" in brief
    assert written and not written[0].exists()


def test_run_NoPreviewInTime_FailsLoudly(world, capsys, monkeypatch):
    _set_main_config(world, dict(SHIP_YAML, verify={"surface": "vercel-preview"}))
    monkeypatch.setattr(engine.preview, "find_preview_url", lambda slug, sha: None)
    monkeypatch.setattr(engine.gitops, "push_branch", lambda repo, branch: None)
    monkeypatch.setattr(engine, "POLL_SECONDS", 0)
    monkeypatch.setattr(engine, "PREVIEW_LIMIT_SECONDS", -1)
    world.outputs["Reviewer"].append(review())
    run_cli("start", "--intent", str(world.intent), capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert code != 0 and out["state"] == "failed"
    assert runstore.load(out["run"])["failure"]["code"] == "no-preview"


def test_cli_Help_ListsEveryCommand(capsys):
    with pytest.raises(SystemExit) as exit_info:
        cli.main(["--help"])
    assert exit_info.value.code == 0
    text = capsys.readouterr().out
    text.encode("ascii")
    for command in ("start", "wait", "continue", "respond", "status", "abort"):
        assert f"cc-ship {command}" in text
