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


DOCS_NO_SURFACE = {"verdict": "no-surface", "scenarios": [
    {"name": "notes.md", "result": "untested", "live": False, "evidence": "",
     "reason": "Nothing to run live: documents only"}]}


def _make_docs_only(world):
    git(world.work, "reset", "-q", "--hard", "HEAD~1")
    (world.work / "notes.md").write_text("hello\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "docs only")


def test_run_DocsOnlyChange_StillVerifiedBySeparateSessionThenMerges(world, capsys):
    # Review finding 3 (pull request 2949): the verifier always runs, even for documents.
    _make_docs_only(world)
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(DOCS_NO_SURFACE)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out
    assert "Nothing to run live: documents only" in world.prs[1]["body"]
    assert [s["role"] for s in world.spawned] == ["Reviewer", "Verifier"]
    verifier_brief = world.spawned[1]["brief"].read_text(encoding="utf-8")
    assert "touches only documents" in verifier_brief


def test_run_NoneSurfaceHonestNoSurface_Accepted(world, capsys):
    # Review finding 2: with surface 'none' nothing was supplied, so no-surface can be true.
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(DOCS_NO_SURFACE)
    code, out = ship_to_merge(world, capsys)
    run = runstore.load(out["run"])
    assert run["steps"]["verify"] == "completed", out
    assert world.prompts == []  # no correction turn was needed


def test_output_IsAsciiEvenWhenTheReviewerWritesUnicode(world, capsys):
    world.outputs["Reviewer"].append(review(finding("F1", "x \u2014 wrong \u201cquoted\u201d \u4e2d")))
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


# ------------------------------------------------------------------ review of pull request 2949

def test_run_CommitDuringReview_WholeRoundRunsAgainOnTheNewHead(world, capsys):
    # Finding 1: what ships must be exactly what was checked, reviewed and verified.
    real_wait = world.wait_for_output
    committed = []

    def wait_while_author_commits(sid, output, *args, **kwargs):
        if not committed:
            (world.work / "app.py").write_text("x = 9\n", encoding="ascii")
            git(world.work, "commit", "-am", "sneaky")
            committed.append(git(world.work, "rev-parse", "HEAD"))
        return real_wait(sid, output, *args, **kwargs)

    engine.fleet.wait_for_output = wait_while_author_commits
    world.outputs["Reviewer"] += [review(), review()]
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out
    assert [s["role"] for s in world.spawned] == ["Reviewer", "Reviewer", "Verifier"]
    assert world.spawned[0]["id"] in world.stopped
    assert world.prs[1]["head"] == committed[0]
    run = runstore.load(out["run"])
    assert run["review_head"] == run["verify_head"] == run["checks_head"] == committed[0]


def test_run_MergeSucceededButRunNotSaved_ContinueReconciles(world, capsys, monkeypatch):
    # Finding 4: the irreversible step is reconciled, never repeated.
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    real_merged = engine._merged
    monkeypatch.setattr(engine, "_merged", lambda run: (_ for _ in ()).throw(KeyboardInterrupt()))
    with pytest.raises(KeyboardInterrupt):
        cli.main(["start", "--intent", str(world.intent)])
        cli.main(["wait", "--seconds", "5"])
    capsys.readouterr()
    monkeypatch.setattr(engine, "_merged", real_merged)
    assert world.merged == [1]
    code, out = run_cli("continue", capsys=capsys)
    assert out["state"] == "merged", out
    assert world.merged == [1]  # not merged a second time


def test_run_VerifierReapedBeforeCorrection_CookieRemoved(world, capsys, monkeypatch):
    # Finding 5: a failure while correcting a verifier never leaves the cookie behind.
    _set_main_config(world, dict(SHIP_YAML, verify={"surface": "vercel-preview"}))
    monkeypatch.setattr(engine.preview, "find_preview_url", lambda slug, sha: "https://p.vercel.app")
    monkeypatch.setattr(engine.preview, "write_bypass_state", lambda url, path: path.write_text("{}"))
    monkeypatch.setattr(engine.gitops, "push_branch", lambda repo, branch: None)

    def reaped(sid):
        raise fleet.FleetError("session not found")

    monkeypatch.setattr(engine.fleet, "clear_done_flag", reaped)
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append({"verdict": "go"})
    code, out = ship_to_merge(world, capsys)
    assert code != 0 and out["state"] == "failed"
    assert not list((Path(out["folder"]) if "folder" in out else
                     runstore.runs_root()).rglob("browser-state.json"))


def test_run_SimilarButDifferentFinding_GoesToTheOwnerAgain(world, capsys):
    # Finding 6: a kept finding only suppresses the SAME finding.
    first = dict(finding("F1", "Missing error handling", action="ask-owner", severity="warning"),
                 sequence="path A")
    world.outputs["Reviewer"].append(review(first))
    ship_to_merge(world, capsys)
    world.outputs["Verifier"].append(GO)
    run_cli("respond", "r1-F1", "--keep", capsys=capsys)
    run_cli("wait", "--seconds", "5", capsys=capsys)

    (world.work / "app.py").write_text("x = 7\n", encoding="ascii")
    git(world.work, "commit", "-am", "more")
    world.prs[1]["state"] = "MERGED"
    second = dict(first, sequence="path B")
    world.outputs["Reviewer"].append(review(second))
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-owner", out
    assert "looks like" in out["next_step"] and "sequence differs" in out["next_step"]


def test_run_NonAsciiRepositoryPath_BriefsStillWritten(tmp_path, world, capsys, monkeypatch):
    # Finding 7: brief files carry paths, so they are UTF-8; only terminal output is ASCII.
    moved = world.tmp / "w\u00f8rk"
    world.work.rename(moved)
    world.work = moved
    monkeypatch.chdir(moved)
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out


def test_advance_UnexpectedError_SavedAsFailedAndContinueRecovers(world, capsys, monkeypatch):
    # Finding 7: an unexpected error never strands the run in 'working'.
    real = engine.briefs.reviewer_brief
    monkeypatch.setattr(engine.briefs, "reviewer_brief",
                        lambda **kw: (_ for _ in ()).throw(RuntimeError("boom")))
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert code != 0 and out["state"] == "failed"
    assert runstore.load(out["run"])["failure"]["code"] == "internal-error"
    monkeypatch.setattr(engine.briefs, "reviewer_brief", real)
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(GO)
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged", out


def test_risk_AuthoringDocument_NotHigh(world, capsys):
    # Finding 8: no built-in name guesses.
    git(world.work, "reset", "-q", "--hard", "HEAD~1")
    (world.work / "docs").mkdir()
    (world.work / "docs" / "authoring.md").write_text("how to write\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "docs")
    world.outputs["Reviewer"].append(review())
    world.outputs["Verifier"].append(DOCS_NO_SURFACE)
    code, out = ship_to_merge(world, capsys)
    assert out["risk"] == "low" and out["state"] == "merged", out


def _three_failed_rounds(world, capsys):
    bug = finding("F1", "x is wrong")
    world.outputs["Reviewer"] += [review(bug)] * 4
    ship_to_merge(world, capsys)
    for value in (10, 11, 12):
        (world.work / "app.py").write_text(f"x = {value}\n", encoding="ascii")
        git(world.work, "commit", "-am", f"try {value}")
        run_cli("continue", capsys=capsys)
        code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    return out


def test_run_FixLimit_ParksAndContinueCannotOverride(world, capsys):
    # Finding 9: only the owner's recorded call moves past the limit.
    out = _three_failed_rounds(world, capsys)
    assert out["state"] == "waiting-on-owner" and out["phase"] == "fix-limit", out
    code, out = run_cli("continue", capsys=capsys)
    assert code != 0 and out["code"] == "fix-limit"
    code, out = run_cli("continue", "--owner-allows-another-round", capsys=capsys)
    assert code == 2


def test_run_FixLimitOwnerSaysFix_ExactlyOneMoreRound(world, capsys):
    _three_failed_rounds(world, capsys)
    world.outputs["Reviewer"].append(review(finding("F1", "x is wrong")))
    code, out = run_cli("respond", "fix-limit", "--fix", "--note", "one more", capsys=capsys)
    assert out["state"] == "waiting-on-author", out
    (world.work / "app.py").write_text("x = 13\n", encoding="ascii")
    git(world.work, "commit", "-am", "try 13")
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["phase"] == "fix-limit", out  # the fourth failure parks again


def test_run_FixLimitOwnerKeeps_ShipsAsHighRisk(world, capsys):
    _three_failed_rounds(world, capsys)
    world.outputs["Verifier"].append(GO)
    run_cli("respond", "fix-limit", "--keep", "--note", "ship it", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "waiting-on-owner" and out["risk"] == "high", out
    assert "OWNER KEPT: Fix-round limit reached" in world.prs[1]["body"]


# ------------------------------------------------------------------ re-review of pull request 2949

def test_run_UncommittedEditDuringReview_HandedBackAndFullyRerunAfterCommit(world, capsys):
    # Round 2 finding 1: uncommitted edits would be verified but never shipped.
    real_wait = world.wait_for_output
    edited = []

    def wait_while_author_edits(sid, output, *args, **kwargs):
        if not edited:
            (world.work / "app.py").write_text("x = 42\n", encoding="ascii")
            edited.append(sid)
        return real_wait(sid, output, *args, **kwargs)

    engine.fleet.wait_for_output = wait_while_author_edits
    world.outputs["Reviewer"] += [review(), review()]
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "waiting-on-author" and "uncommitted" in out["next_step"], out
    assert edited[0] in world.stopped and world.prs == {}
    git(world.work, "commit", "-am", "the edit")
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "merged", out
    assert world.prs[1]["head"] == git(world.work, "rev-parse", "HEAD")
    assert [s["role"] for s in world.spawned] == ["Reviewer", "Reviewer", "Verifier"]


def test_start_UntrackedFileOnly_NotADirtyTree(world, capsys):
    (world.work / "intent-draft.md").write_text("scratch\n", encoding="ascii")
    world.outputs["Reviewer"].append(review())
    code, out = run_cli("start", "--intent", str(world.intent), capsys=capsys)
    assert out["state"] == "working", out


def test_risk_AuthenticationCodeNotListedByRepository_StillHigh(world, capsys):
    # Round 2 finding 2: a repository's incomplete list never makes auth code low risk.
    (world.work / "src" / "auth").mkdir(parents=True)
    (world.work / "src" / "auth" / "session.ts").write_text("x\n", encoding="ascii")
    git(world.work, "add", ".")
    git(world.work, "commit", "-m", "auth")
    world.outputs["Reviewer"].append(review(risk="low"))
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["risk"] == "high" and out["state"] == "waiting-on-owner" and world.merged == []
    assert "authentication, key or tenant code: src/auth/session.ts" in world.prs[1]["body"]


def test_run_ReviewerRewordsDecidedFinding_MatchesDecisionIdAndOwnerIsNotAskedAgain(world, capsys):
    # Round 2 finding 3: the same owner call in other words is not raised again.
    first = dict(finding("F1", "Missing error handling", action="ask-owner", severity="warning"),
                 sequence="Opening settings without an org returns 500")
    world.outputs["Reviewer"].append(review(first))
    ship_to_merge(world, capsys)
    world.outputs["Verifier"].append(GO)
    run_cli("respond", "r1-F1", "--keep", "--note", "fine for now", capsys=capsys)
    run_cli("wait", "--seconds", "5", capsys=capsys)

    (world.work / "app.py").write_text("x = 8\n", encoding="ascii")
    git(world.work, "commit", "-am", "unrelated")
    world.prs[1]["state"] = "MERGED"
    reworded = dict(first, sequence="GET settings with no org produces HTTP 500",
                    same_as_decision="D1")
    world.outputs["Reviewer"].append(review(reworded))
    world.outputs["Verifier"].append(GO)
    code, out = ship_to_merge(world, capsys)
    assert out["state"] == "merged", out
    brief = [s for s in world.spawned if s["role"] == "Reviewer"][-1]["brief"].read_text()
    assert 'D1: owner chose KEEP - "Missing error handling"' in brief
    assert "reviewer matched" in world.prs[2]["body"] and "decision D1" in world.prs[2]["body"]


def test_run_SameAsDecisionPointingAtAFixDecision_NotSuppressed(world, capsys):
    first = dict(finding("F1", "Missing error handling", action="ask-owner", severity="warning"))
    world.outputs["Reviewer"] += [review(first), review(dict(first, sequence="other words",
                                                             same_as_decision="D1"))]
    ship_to_merge(world, capsys)
    run_cli("respond", "r1-F1", "--fix", capsys=capsys)  # owner wants it fixed: not closed
    (world.work / "app.py").write_text("x = 6\n", encoding="ascii")
    git(world.work, "commit", "-am", "attempt")
    run_cli("continue", capsys=capsys)
    code, out = run_cli("wait", "--seconds", "5", capsys=capsys)
    assert out["state"] == "waiting-on-owner" and "r2-F1" in out["next_step"], out


def test_validate_review_BadSameAsDecision_Rejected():
    import contracts
    bad = review(dict(finding("F1", "t"), same_as_decision="the first one"))
    assert any("same_as_decision" in p for p in contracts.validate_review(bad))
