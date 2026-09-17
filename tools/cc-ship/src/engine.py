"""The cc-ship run: sync, local checks, review, fix, owner calls, verify, risk,
pull request, merge (issue 2935, "How a run works").

Each public function takes the run as far as it can, saves it, and returns it. The
run always ends in one honest state (runstore) with a next_step for the author.
"""

from __future__ import annotations

import os
import re
import subprocess
import time
from pathlib import Path

import briefs
import config
import contracts
import decisions
import evidence
import fleet
import github
import gitops
import prbody
import preview
import risk as risk_rules
import runstore
import trust
from errors import ShipError
from runstore import (COMPLETED, FAILED, MERGED, PENDING, SKIPPED, WAITING_ON_AUTHOR,
                      WAITING_ON_OWNER, WORKING)
from text import ascii_safe

POLL_SECONDS = 10
WAIT_SLICE_SECONDS = 8 * 60
SESSION_LIMIT_SECONDS = 45 * 60
PREVIEW_LIMIT_SECONDS = 20 * 60
CHECK_TIMEOUT_SECONDS = 30 * 60
MAX_FIX_ROUNDS = 3
MAX_CORRECTIONS = 2
MAIN_BRANCHES = ("main",)

AUTHOR_FAMILY_DEFAULT_REVIEWER = {"ClaudeCode": "Codex"}


# --------------------------------------------------------------------------- helpers

def _repo(run: dict) -> Path:
    return Path(run["repo"])


def _folder(run: dict) -> Path:
    return runstore.folder(run)


def _set(run: dict, state: str, next_step: str, failure: dict | None = None) -> dict:
    run["state"] = state
    run["next_step"] = ascii_safe(next_step)
    run["failure"] = failure
    runstore.save(run)
    return run


def _drop_verifier(run: dict, reason: str) -> None:
    """The bypass cookie exists only while a verifier works. Every end of a verifier,
    and every failure, comes through here."""
    session = run.get("session")
    if session and session["role"] == "Verifier":
        _stop_session_quietly(session["id"], reason)
        run["session"] = None
    preview.remove_bypass_state(_folder(run) / "browser-state.json")


def _fail(run: dict, err: ShipError) -> dict:
    _drop_verifier(run, "cc-ship: the run failed")
    return _set(run, FAILED, err.help, err.as_dict())


def internal_error(exc: Exception) -> ShipError:
    return ShipError("internal-error", f"{type(exc).__name__}: {exc}",
                     "This is a cc-ship defect; report it with the run folder. "
                     "After the cause is fixed, run: cc-ship continue")


HEAD_KEYS = (("checked", "checks_head"), ("reviewed", "review_head"), ("verified", "verify_head"))


def _reset_round(run: dict) -> None:
    run["phase"] = "sync"
    run["steps"] = {step: PENDING for step in runstore.STEPS}
    run["verify"] = None
    run["risk"] = None
    run["preview_wait"] = None
    for _, key in HEAD_KEYS:
        run[key] = None


def _worktree_dirty(run: dict) -> dict | None:
    """Uncommitted edits would be verified but never shipped: stop and hand back."""
    if gitops.is_clean(_repo(run)):
        return None
    session = run.get("session")
    if session:
        _stop_session_quietly(session["id"], "cc-ship: the worktree has uncommitted changes")
    _drop_verifier(run, "cc-ship: uncommitted changes")
    run["session"] = None
    run["phase"] = "dirty"
    return _set(run, WAITING_ON_AUTHOR,
                "The worktree has uncommitted or untracked files. What is reviewed and verified "
                "must be exactly what ships. Commit them (every step then runs again on the new "
                "commit) or remove them (keep notes such as intent.md outside the worktree), "
                "then run: cc-ship continue")


def _head_moved(run: dict) -> str | None:
    """What was checked, reviewed and verified must be exactly what ships."""
    head = gitops.head(_repo(run))
    for label, key in HEAD_KEYS:
        if run.get(key) and run[key] != head:
            return f"HEAD moved to {head[:12]} after {run[key][:12]} was {label}"
    return None


def _restart_round(run: dict, why: str) -> None:
    """New commits during a run: every step runs again on the new head."""
    session = run.get("session")
    if session:
        _stop_session_quietly(session["id"], "cc-ship: HEAD moved; the round restarts")
    _drop_verifier(run, "cc-ship: HEAD moved")
    run["session"] = None
    if run["history"]:
        run["history"][-1]["notes"].append(f"{why}; every step ran again.")
    _reset_round(run)
    runstore.save(run)


def _session_name(run: dict, role: str) -> str:
    mission = run["mission"] or "cc-ship"
    return f"{mission} - {role} - ship {run['branch']}"


def _base_model(model: str | None) -> str | None:
    """One spelling per model for the same-model check: claude-opus-5[1m] is
    claude-opus-5, and a dated id such as claude-haiku-4-5-20251001 (how a transcript
    records claude-haiku-4-5) is its undated form."""
    if not model:
        return None
    base = model.split("[", 1)[0].strip().lower()
    return re.sub(r"-\d{8}$", "", base)


def _author_model(run: dict) -> str | None:
    row = fleet.find_session(run["author_session"])
    return ((row or {}).get("modelDisplay") or {}).get("modelId")


def _reviewer_agent(run: dict, cfg: config.ShipConfig) -> tuple[str, str | None]:
    """The reviewer's agent and model. The author never certifies its own work: the
    reviewer is another agent family, or - only when the repository names a reviewer
    model on main - the same family running a DIFFERENT model (owner decision,
    2026-09-16, recorded on every pull request it reviews)."""
    agent = cfg.reviewer_agent or AUTHOR_FAMILY_DEFAULT_REVIEWER.get(run["author_agent"], "ClaudeCode")
    model = cfg.reviewer_model
    if agent != run["author_agent"]:
        return agent, model
    if model is None:
        raise ShipError(
            "same-family",
            f"The reviewer would be {agent}, the same agent family as the author, on the same "
            "model. The author never certifies its own work.",
            "Set reviewer_agent in .ship.yaml on main to a different family, or set "
            "reviewer_model to a different model than the author's.",
        )
    author_model = _author_model(run)
    if author_model is None:
        raise ShipError(
            "author-model-unknown",
            "The reviewer is the author's agent family, and the author's model is not reported "
            "yet, so cc-ship cannot prove the reviewer runs a different model.",
            "Finish one turn in the author session (the fleet reports its model at turn end), "
            "then run: cc-ship continue",
        )
    if _base_model(author_model) == _base_model(model):
        raise ShipError(
            "same-model",
            f"The reviewer would run {model}, the same model as the author ({author_model}).",
            "Set reviewer_model in .ship.yaml on main to a different model, then run: cc-ship continue",
        )
    run["author_model"] = author_model
    return agent, model


def _spawn(run: dict, role: str, agent: str, brief: Path, output: Path,
           model: str | None = None) -> None:
    root = gitops.main_repo_root(_repo(run))
    trust.require_trust(agent, root)
    name = _session_name(run, role)
    session_id = fleet.spawn_session(_repo(run), agent, run["author_session"], name, brief,
                                     model=model)
    record = {
        "role": role, "id": session_id, "name": name, "agent": agent, "model": model,
        "brief": str(brief), "output": str(output), "started": time.time(),
        "corrections": 0, "replaced": False, "written_after": None,
        "watch": {"started": time.time()},
    }
    run["session"] = record
    run["sessions"].append({k: record[k] for k in ("role", "id", "name", "agent", "model")})


def _stop_session_quietly(session_id: str, reason: str) -> None:
    try:
        fleet.stop_session(session_id, reason)
    except fleet.FleetError:
        pass  # already gone: nothing to stop


# --------------------------------------------------------------------------- start

def _intent_is_empty(text: str) -> bool:
    body = [line for line in text.splitlines()
            if line.strip() and not line.lstrip().startswith("#")]
    return not body


def start(cwd: Path, intent_file: Path, title: str | None) -> dict:
    author = os.environ.get("CC_SESSION_ID", "")
    if not author:
        raise ShipError("no-session", "CC_SESSION_ID is not set; cc-ship runs inside a fleet session.",
                        "Run cc-ship from the session that wrote the change.")
    repo = gitops.toplevel(cwd)
    branch = gitops.branch(repo)
    if branch in MAIN_BRANCHES:
        raise ShipError("on-main", f"You are on {branch}. cc-ship ships a branch, never main.",
                        "Commit your change on a branch: git switch -c <name>, then cc-ship start again.")
    if not gitops.is_clean(repo):
        raise ShipError("dirty-tree", "The worktree has uncommitted or untracked files.",
                        "Commit or remove every change, then run cc-ship start again. Keep "
                        "intent.md outside the worktree (for example in your scratch folder).")
    if not intent_file.is_file():
        raise ShipError("no-intent", f"The intent file {intent_file} does not exist.",
                        "Write intent.md in the owner's words: goal, constraints, what was ruled "
                        "out, decisions made. Then run cc-ship start --intent intent.md")
    intent = intent_file.read_text(encoding="utf-8")
    if _intent_is_empty(intent):
        raise ShipError("empty-intent", f"The intent file {intent_file} says nothing.",
                        "Write the owner's goal, constraints, what was ruled out and the decisions "
                        "made, in his words. cc-ship never guesses intent from the diff.")
    existing = runstore.find_active(repo, branch)
    if existing:
        raise ShipError("run-exists", f"Run {existing['id']} is already open for {branch} "
                                      f"(state: {existing['state']}).",
                        "Use: cc-ship status, cc-ship continue, or cc-ship abort")
    row = fleet.find_session(author)
    if row is None:
        raise ShipError("no-session", f"Session {author} is not in the fleet list.",
                        "Run cc-ship from a live fleet session.")
    gitops.fetch(repo)
    if gitops.ahead_of_main(repo) == 0:
        raise ShipError("nothing-to-ship", f"{branch} has no commits that are not on origin/main.",
                        "Commit your change first.")
    config.load_from_main(repo)  # refuse now, not halfway, if main has no valid gate
    run = runstore.new_run(repo, branch, gitops.remote_slug(repo), author, row["agent"],
                           row.get("missionName"), intent)
    run["title"] = title
    runstore.save(run)
    return advance(run)


# --------------------------------------------------------------------------- advance

def advance(run: dict) -> dict:
    """Run steps until one needs a session, the author or the owner."""
    try:
        while True:
            phase = run["phase"]
            if phase == "sync":
                _step_sync(run)
            elif phase == "checks":
                if not _step_checks(run):
                    return run
            elif phase == "review":
                return _step_review(run)
            elif phase == "verify":
                if not _step_verify(run):
                    return run
            elif phase == "pr":
                if not _step_pr(run):
                    return run
            elif phase == "merge":
                return _step_merge(run)
            else:
                return run
            runstore.save(run)
    except ShipError as err:
        return _fail(run, err)
    except (fleet.FleetError, preview.PreviewError) as exc:
        return _fail(run, as_ship_error(exc))
    except Exception as exc:  # saved as failed so continue has a way forward
        return _fail(run, internal_error(exc))


def as_ship_error(exc: Exception) -> ShipError:
    if isinstance(exc, ShipError):
        return exc
    if isinstance(exc, preview.PreviewError):
        return ShipError("preview-failed", str(exc), "Fix the preview access, then run: cc-ship continue")
    return ShipError("fleet-failed", str(exc),
                     "Check the Gateway (cc-devthrottle session list), then run: cc-ship continue")


def _step_sync(run: dict) -> None:
    repo = _repo(run)
    if gitops.rebase_in_progress(repo):
        raise ShipError("rebase-in-progress", "A rebase is still in progress.",
                        "Finish it (git rebase --continue) or abort it, then run: cc-ship continue")
    if gitops.branch(repo) != run["branch"]:
        raise ShipError("wrong-branch", f"The worktree is not on {run['branch']}.",
                        f"git switch {run['branch']}, then run: cc-ship continue")
    if not gitops.is_clean(repo):
        raise ShipError("dirty-tree", "The worktree has uncommitted changes.",
                        "Commit every change, then run: cc-ship continue")
    gitops.fetch(repo)
    if gitops.behind_main(repo):
        gitops.rebase_onto_main(repo)
    run["steps"]["sync"] = COMPLETED
    run["phase"] = "checks"


def _step_checks(run: dict) -> bool:
    repo = _repo(run)
    cfg = config.load_from_main(repo)
    run["checks_head"] = gitops.head(repo)
    if not cfg.checks:
        run["steps"]["checks"] = SKIPPED
        run["phase"] = "review"
        return True
    log = _folder(run) / f"checks-r{run['review_round'] + 1}.log"
    with log.open("w", encoding="utf-8") as handle:
        for command in cfg.checks:
            handle.write(f"$ {command}\n")
            handle.flush()
            try:
                proc = subprocess.run(command, shell=True, cwd=repo, capture_output=True, text=True,
                                      timeout=CHECK_TIMEOUT_SECONDS, encoding="utf-8",
                                      errors="replace")
            except subprocess.TimeoutExpired:
                raise ShipError("check-timeout",
                                f"Local check timed out after {CHECK_TIMEOUT_SECONDS}s: {command}",
                                f"Make the check finish, then run: cc-ship continue (log: {log})")
            handle.write(proc.stdout + proc.stderr + f"\n(exit {proc.returncode})\n")
            if proc.returncode != 0:
                tail = "\n".join((proc.stdout + proc.stderr).strip().splitlines()[-25:])
                _set(run, WAITING_ON_AUTHOR,
                     f"Local check failed: {command}\n{tail}\n"
                     f"Fix it, commit, then run: cc-ship continue (full log: {log})",
                     {"error": f"Local check failed: {command}", "code": "check-failed",
                      "help": "Fix it, commit, then run: cc-ship continue"})
                return False
    run["steps"]["checks"] = COMPLETED
    run["phase"] = "review"
    return True


# --------------------------------------------------------------------------- review

def _step_review(run: dict) -> dict:
    repo = _repo(run)
    cfg = config.load_from_main(repo)
    agent, model = _reviewer_agent(run, cfg)
    dirty = _worktree_dirty(run)
    if dirty:
        return dirty
    moved = _head_moved(run)
    if moved:
        _restart_round(run, moved)
        return advance(run)
    rnd = run["review_round"] + 1
    head = gitops.head(repo)
    base = gitops.merge_base(repo)
    folder = _folder(run)
    diff = folder / f"diff-r{rnd}.patch"
    diff.write_text(gitops.diff_text(repo, base, head), encoding="utf-8")
    output = folder / f"review-r{rnd}.json"
    brief = folder / f"brief-review-r{rnd}.md"
    brief.write_text(briefs.reviewer_brief(
        repo=repo, base=base, head=head, intent=folder / "intent.md", diff=diff,
        decisions=decisions.listed(run["slug"], run["branch"]), output=output,
        repo_rules=cfg.rules, first_reviewed_head=run["first_reviewed_head"],
    ), encoding="utf-8")
    output.unlink(missing_ok=True)
    fleet.done_marker(output).unlink(missing_ok=True)
    _spawn(run, "Reviewer", agent, brief, output, model=model)
    run["review_round"] = rnd
    if run["first_reviewed_head"] is None:
        run["first_reviewed_head"] = head
    run["review_head"] = head
    return _set(run, WORKING, f"A reviewer is working ({run['session']['name']}). Run: cc-ship wait")


def _finding_id(rnd: int, finding: dict) -> str:
    return f"r{rnd}-{finding['id']}"


def _review_done(run: dict, review: dict) -> dict:
    rnd = run["review_round"]
    closed_exact, closed_similar = decisions.closed(run["slug"], run["branch"])
    by_id = {d["id"]: d for d in decisions.listed(run["slug"], run["branch"])}
    open_findings, notes, suppressed = [], [], []
    for f in review["findings"]:
        key = decisions.finding_key(f)
        if decisions.exact_key(f) in closed_exact:
            suppressed.append(f["title"])
            continue
        matched = by_id.get(f.get("same_as_decision") or "")
        if matched and matched["decision"] in ("keep", "drop") and matched["file"] == f["file"]:
            # The reviewer itself says this is a problem the owner already decided.
            notes.append(f"reviewer matched \"{f['title']}\" to owner decision {matched['id']} "
                         f"({matched['decision']}: {matched['title']}); not asked again")
            continue
        action = contracts.finding_action(f)
        entry = {"id": _finding_id(rnd, f), "key": key, "title": f["title"], "file": f["file"],
                 "line": f.get("line"), "sequence": f["sequence"], "remedy": f["remedy"],
                 "severity": f["severity"], "action": action, "answer": None}
        similar = closed_similar.get(key)
        if similar:
            entry["similar_to"] = (f"looks like \"{similar['title']}\", which the owner chose to "
                                   f"{similar['decision']} on {similar['at']}, but the failing "
                                   "sequence differs")
        if action == "note":
            notes.append(f["title"])
        else:
            open_findings.append(entry)

    # What the author fixed last round is what the reviewer no longer raises.
    current_keys = {f["key"] for f in open_findings}
    if run["history"]:
        prev = run["history"][-1]
        prev["fixed_next_round"] = [t for k, t in prev["pending_fix"] if k not in current_keys]
    run["history"].append({
        "round": rnd, "session": run["session"]["id"],
        "agent": (f"{run['session']['agent']} ({run['session']['model']})"
                  if run["session"].get("model") else run["session"]["agent"]),
        "same_family": run["session"]["agent"] == run["author_agent"],
        "found": len(open_findings), "notes": notes, "suppressed": suppressed,
        "pending_fix": [], "fixed_next_round": [],
    })
    run["review"] = {"risk_level": review["risk_level"], "risk_reason": review["risk_reason"],
                     "checked": review["checked"], "not_covered": review["not_covered"]}
    run["findings"] = open_findings
    run["session"] = None

    if any(f["action"] == "ask-owner" for f in open_findings):
        run["phase"] = "owner"
        return _set(run, WAITING_ON_OWNER, owner_message(run))
    if open_findings:
        return _send_to_author(run, open_findings)
    run["steps"]["review"] = COMPLETED
    run["phase"] = "verify"
    return advance(run)


def owner_message(run: dict) -> str:
    pending = [f for f in run["findings"] if f["action"] == "ask-owner" and f["answer"] is None]
    lines = ["These findings are the OWNER's call. Relay them to him word for word, all in one "
             "message, then end your turn. Record each answer with: "
             "cc-ship respond <id> --fix|--keep|--drop [--note \"his words\"]", ""]
    for f in pending:
        where = f"{f['file']}:{f['line']}" if f.get("line") else f["file"]
        lines += [f"[{f['id']}] {f['title']} ({f['severity']}, {where})",
                  f"  What happens: {f['sequence']}",
                  f"  Reviewer suggests: {f['remedy']}"]
        if f.get("similar_to"):
            lines.append(f"  Note: this {f['similar_to']}.")
        lines.append("")
    return "\n".join(lines)


def _send_to_author(run: dict, to_fix: list[dict]) -> dict:
    run["to_fix"] = to_fix
    if run["fix_rounds"] >= run.get("fix_round_limit", MAX_FIX_ROUNDS):
        run["phase"] = "fix-limit"
        return _set(run, WAITING_ON_OWNER,
                    f"{run['fix_rounds']} fix rounds have not settled this change. Tell the owner, "
                    "list the findings below word for word, and end your turn. Record his call "
                    "with one of: cc-ship respond fix-limit --fix (one more round) | --keep (ship "
                    "the code as it is; the findings are recorded as kept) | --drop (the findings "
                    "are wrong) [--note \"his words\"]\n"
                    + "\n".join(f"- {f['title']} ({f['file']}): {f['sequence']}" for f in to_fix))
    run["history"][-1]["pending_fix"] = [[f["key"], f["title"]] for f in to_fix]
    run["phase"] = "fix"
    run["fix_head"] = gitops.head(_repo(run))
    lines = ["Fix these findings, commit, then run: cc-ship continue", ""]
    for f in to_fix:
        where = f"{f['file']}:{f['line']}" if f.get("line") else f["file"]
        lines += [f"[{f['id']}] {f['title']} ({where})",
                  f"  What happens: {f['sequence']}",
                  f"  Fix: {f['remedy']}", ""]
    lines.append("Fix only what each finding needs. Anything beyond that is reviewed as new code.")
    return _set(run, WAITING_ON_AUTHOR, "\n".join(lines))


def _respond_fix_limit(run: dict, decision: str, note: str) -> dict:
    limit_finding = {"title": "Fix-round limit reached", "file": "", "severity": "",
                     "sequence": f"after {run['fix_rounds']} fix rounds"}
    decisions.record(run["slug"], run["branch"], run["id"], limit_finding, decision, note)
    if decision == "fix":
        run["fix_round_limit"] = run["fix_rounds"] + 1
        return _send_to_author(run, run["to_fix"])
    for finding in run["to_fix"]:
        decisions.record(run["slug"], run["branch"], run["id"], finding, decision, note)
    run["findings"] = []
    run["steps"]["review"] = COMPLETED
    run["phase"] = "verify"
    _set(run, WORKING, "")
    return advance(run)


def respond(run: dict, finding_id: str, decision: str, note: str) -> dict:
    if run["phase"] == "fix-limit" and run["state"] == WAITING_ON_OWNER:
        if finding_id != "fix-limit":
            raise ShipError("unknown-finding", "The run is waiting on the owner's fix-limit call.",
                            "Use: cc-ship respond fix-limit --fix|--keep|--drop")
        return _respond_fix_limit(run, decision, note)
    if run["phase"] != "owner":
        raise ShipError("nothing-to-answer", "This run is not waiting on the owner for findings.",
                        "Run: cc-ship status")
    match = [f for f in run["findings"] if f["id"] == finding_id and f["action"] == "ask-owner"]
    if not match:
        ids = ", ".join(f["id"] for f in run["findings"] if f["action"] == "ask-owner")
        raise ShipError("unknown-finding", f"No owner finding {finding_id} in this run.",
                        f"The owner findings are: {ids}")
    finding = match[0]
    if finding["answer"] is not None:
        raise ShipError("already-answered", f"{finding_id} was already answered "
                                            f"({finding['answer']}).", "Run: cc-ship status")
    decisions.record(run["slug"], run["branch"], run["id"], finding, decision, note)
    finding["answer"] = decision
    runstore.save(run)
    if any(f["action"] == "ask-owner" and f["answer"] is None for f in run["findings"]):
        return _set(run, WAITING_ON_OWNER, owner_message(run))
    to_fix = [f for f in run["findings"]
              if f["action"] == "auto-fix" or (f["action"] == "ask-owner" and f["answer"] == "fix")]
    if to_fix:
        return _send_to_author(run, to_fix)
    run["steps"]["review"] = COMPLETED
    run["phase"] = "verify"
    _set(run, WORKING, "")
    return advance(run)


# --------------------------------------------------------------------------- verify

def _step_verify(run: dict) -> bool:
    repo = _repo(run)
    if _worktree_dirty(run):
        return False
    moved = _head_moved(run)
    if moved:
        _restart_round(run, moved)
        return True
    cfg = config.load_from_main(repo)
    head = gitops.head(repo)
    changed = gitops.changed_files(repo, gitops.merge_base(repo), head)
    if cfg.surface == "skip":
        run["steps"]["verify"] = SKIPPED
        run["phase"] = "pr"
        return True
    docs_only = config.is_docs_only(changed, cfg)
    run["verify_docs_only"] = docs_only

    folder = _folder(run)
    output = folder / "verify.json"
    output.unlink(missing_ok=True)
    fleet.done_marker(output).unlink(missing_ok=True)
    preview_url = None
    state_file = None
    if cfg.surface == "vercel-preview" and not docs_only:
        if run.get("preview_wait") is None:
            gitops.push_branch(repo, run["branch"])
            run["preview_wait"] = {"head": head, "since": time.time()}
        preview_url = preview.find_preview_url(run["slug"], head)
        if preview_url is None:
            waited = time.time() - run["preview_wait"]["since"]
            if waited > PREVIEW_LIMIT_SECONDS:
                raise ShipError("no-preview",
                                f"No successful Vercel preview for {head[:12]} after "
                                f"{int(waited / 60)} minutes (it may have failed to build).",
                                "Look at the deployment on GitHub or Vercel, fix it, commit, "
                                "then run: cc-ship continue")
            run["phase"] = "preview"
            _set(run, WORKING, f"Waiting for the Vercel preview of {head[:12]}. Run: cc-ship wait")
            return False
        state_file = folder / "browser-state.json"
        preview.write_bypass_state(preview_url, state_file)
    run["preview_wait"] = None
    # Only a supplied preview is a known surface; with none, 'no-surface' may be honest.
    run["verify_surface"] = preview_url is not None
    brief = folder / "brief-verify.md"
    brief.write_text(briefs.verifier_brief(
        repo=repo, intent=folder / "intent.md", preview_url=preview_url,
        browser_state=state_file, evidence_dir=folder / "evidence", output=output,
        docs_only=docs_only,
    ), encoding="utf-8")
    try:
        _spawn(run, "Verifier", cfg.verifier_agent, brief, output)
    except Exception:
        if state_file:
            preview.remove_bypass_state(state_file)
        raise
    run["verify_head"] = head
    _set(run, WORKING, f"A verifier is working ({run['session']['name']}). Run: cc-ship wait")
    return False


def _verify_done(run: dict, verify: dict) -> dict:
    _drop_verifier(run, "cc-ship: verifier finished")
    if run.get("verify_docs_only") and verify["verdict"] == "no-surface":
        verify["docs_only"] = True
    run["verify"] = verify
    if verify["verdict"] == "no-go":
        failing = [s for s in verify["scenarios"] if s["result"] == "fail"]
        to_fix = [{"id": f"verify-{i + 1}", "key": f"verify::{s['name']}", "title":
                   f"Verification failed: {s['name']}", "file": "(live product)", "line": None,
                   "sequence": s["evidence"], "remedy": "Make this scenario work, then continue.",
                   "severity": "error", "action": "auto-fix", "answer": None}
                  for i, s in enumerate(failing)]
        run["history"].append({"round": run["review_round"], "agent": "verifier",
                               "session": run["sessions"][-1]["id"], "found": 0, "notes": [],
                               "suppressed": [], "pending_fix": [], "fixed_next_round": []})
        run["findings"] = to_fix
        return _send_to_author(run, to_fix)
    run["steps"]["verify"] = COMPLETED
    run["phase"] = "pr"
    return advance(run)


# --------------------------------------------------------------------------- wait

def _handle_session_result(run: dict, result: fleet.WaitResult) -> dict:
    session = run["session"]
    role = session["role"]
    output = Path(session["output"])
    dirty = _worktree_dirty(run)
    if dirty:
        return dirty
    moved = _head_moved(run)
    if moved:
        _restart_round(run, moved)
        _set(run, WORKING, f"{moved}; the round restarts from the beginning.")
        return advance(run)
    if result.outcome in (fleet.CRASHED, fleet.STALLED):
        if session["replaced"]:
            run["session"] = None
            return _fail(run, ShipError(
                "session-failed",
                f"The {role.lower()} failed twice ({result.reason}).",
                "Look at the run folder, then run: cc-ship continue (it starts a fresh session)",
            ))
        if result.outcome == fleet.STALLED:
            _stop_session_quietly(session["id"], "cc-ship: stalled without output")
        _drop_verifier(run, "cc-ship: verifier failed")
        # One replacement (issue 2935, "Run state"). The partial output of the failed
        # session is removed so only the replacement's file can count.
        output.unlink(missing_ok=True)
        fleet.done_marker(output).unlink(missing_ok=True)
        if role == "Verifier":
            run["phase"] = "verify"
            run["session"] = None
            advance(run)
            if run["session"]:
                run["session"]["replaced"] = True
                runstore.save(run)
            return run
        _spawn(run, role, session["agent"], Path(session["brief"]), output,
               model=session.get("model"))
        run["session"]["replaced"] = True
        return _set(run, WORKING, f"The {role.lower()} failed ({result.reason}); a replacement "
                                  f"is working ({run['session']['name']}). Run: cc-ship wait")

    data, problems = contracts.load_json(output)
    if data is not None:
        problems = (contracts.validate_review(data) if role == "Reviewer"
                    else contracts.validate_verify(data, bool(run.get("verify_surface"))))
    if problems:
        if session["corrections"] >= MAX_CORRECTIONS:
            _drop_verifier(run, "cc-ship: invalid output")
            run["session"] = None
            err = ShipError(
                "invalid-output",
                f"The {role.lower()} wrote an invalid {output.name} after {MAX_CORRECTIONS} "
                "corrections: " + "; ".join(problems),
                "An unreadable result never counts as a pass. Run: cc-ship continue (it starts "
                "a fresh session)",
            )
            _fail(run, err)
            raise err
        session["corrections"] += 1
        if fleet.find_session(session["id"]) is None:
            # Already reaped (it finished while nobody was polling): nobody is left to
            # correct, so a fresh session redoes the work, within the same limit.
            return _redo_in_fresh_session(run, session, output)
        try:
            fleet.clear_done_flag(session["id"])
        except fleet.FleetError:
            if fleet.find_session(session["id"]) is not None:
                raise  # the session is there: this failure is real, show it
            return _redo_in_fresh_session(run, session, output)  # reaped just now
        fleet.done_marker(output).unlink(missing_ok=True)
        fix = _folder(run) / f"correction-{role.lower()}-r{run['review_round']}-{session['corrections']}.md"
        fix.write_text(briefs.correction_brief(output, problems, session["corrections"],
                                               role.lower()),
                       encoding="utf-8")
        session["written_after"] = time.time()
        # A correction is a new turn: the clock for "never got going" starts again.
        session["watch"] = {"seen_working": session["watch"].get("seen_working", False),
                            "started": time.time()}
        fleet.prompt_session(session["id"], f"Read the file {fix} and follow it exactly.")
        return _set(run, WORKING, f"The {role.lower()}'s file was invalid; it is correcting it "
                                  f"({session['corrections']} of {MAX_CORRECTIONS}). Run: cc-ship wait")
    if role == "Reviewer":
        return _review_done(run, data)
    return _verify_done(run, data)


def _redo_in_fresh_session(run: dict, old: dict, output: Path) -> dict:
    role = old["role"]
    output.unlink(missing_ok=True)
    fleet.done_marker(output).unlink(missing_ok=True)
    if role == "Verifier":
        _drop_verifier(run, "cc-ship: verifier gone before its correction")
        run["phase"] = "verify"
        run["session"] = None
        advance(run)  # a fresh cookie and brief for the fresh verifier
    else:
        _spawn(run, role, old["agent"], Path(old["brief"]), output, model=old.get("model"))
    if run.get("session"):
        run["session"]["corrections"] = old["corrections"]
        run["session"]["replaced"] = old["replaced"]
        return _set(run, WORKING,
                    f"The {role.lower()}'s file was invalid and the session was already gone; a "
                    f"fresh session is redoing it ({old['corrections']} of {MAX_CORRECTIONS}). "
                    "Run: cc-ship wait")
    return run


def wait(run: dict, slice_seconds: float = WAIT_SLICE_SECONDS) -> dict:
    deadline = time.monotonic() + slice_seconds
    while run["state"] == WORKING and time.monotonic() < deadline:
        remaining = max(1.0, deadline - time.monotonic())
        if run["phase"] == "preview":
            time.sleep(min(POLL_SECONDS * 3, remaining))
            run["phase"] = "verify"
            advance(run)
            continue
        session = run["session"]
        if session is None:
            return advance(run)
        age = time.time() - session["started"]
        if age > SESSION_LIMIT_SECONDS:
            _stop_session_quietly(session["id"], "cc-ship: took too long")
            _drop_verifier(run, "cc-ship: took too long")
            run["session"] = None
            return _fail(run, ShipError(
                "session-timeout",
                f"The {session['role'].lower()} worked for over {SESSION_LIMIT_SECONDS // 60} minutes.",
                "Look at it in the fleet, then run: cc-ship continue (it starts a fresh session)"))
        try:
            result = fleet.wait_for_output(
                session["id"], Path(session["output"]), remaining, POLL_SECONDS,
                written_after=session["written_after"], watch=session["watch"])
        except fleet.FleetError as exc:
            runstore.save(run)
            raise ShipError("fleet-failed", f"Could not read the fleet: {exc}",
                            "The session may still be working. Run: cc-ship wait") from exc
        runstore.save(run)
        if result.outcome == fleet.TIMED_OUT:
            break
        try:
            _handle_session_result(run, result)
        except Exception as exc:
            err = (as_ship_error(exc) if isinstance(exc, (ShipError, fleet.FleetError,
                                                          preview.PreviewError))
                   else internal_error(exc))
            if run["state"] != FAILED:
                _fail(run, err)
            raise err from exc
    return run


# --------------------------------------------------------------------------- pull request

def _owner_kept_errors(run: dict) -> list[str]:
    return [d["title"] for d in decisions.read(run["slug"], run["branch"])
            if d["decision"] == "keep" and d["severity"] == "error"]


def _step_pr(run: dict) -> bool:
    repo = _repo(run)
    if _worktree_dirty(run):
        return False
    moved = _head_moved(run)
    if moved:
        _restart_round(run, moved)
        return True
    cfg = config.load_from_main(repo)
    head = gitops.head(repo)
    base = gitops.merge_base(repo)
    changed = gitops.changed_files(repo, base, head)
    added, deleted = gitops.numstat(repo, base, head)
    verify = run["verify"] or {"verdict": None, "scenarios": []}
    run["risk"] = risk_rules.compute(risk_rules.RiskInputs(
        reviewer_level=run["review"]["risk_level"],
        reviewer_reason=run["review"]["risk_reason"],
        changed_files=changed, added=added, deleted=deleted,
        fix_rounds=run["fix_rounds"],
        verdict=verify["verdict"] or "skipped",
        any_untested=any(s["result"] == "untested" for s in verify["scenarios"]),
        has_surface=not verify.get("docs_only", False),
        owner_kept_errors=_owner_kept_errors(run),
    ), cfg)

    gitops.push_branch(repo, run["branch"])
    files = sorted(p for p in (_folder(run) / "evidence").iterdir() if p.is_file())
    links = evidence.publish(repo, run["slug"], run["id"], files)
    subjects = gitops.commit_subjects(repo, base, head)
    body = prbody.build(
        run=run, intent=(_folder(run) / "intent.md").read_text(encoding="utf-8"),
        changed=changed, added=added, deleted=deleted, subjects=subjects, checks=cfg.checks,
        owner_decisions=decisions.read(run["slug"], run["branch"]), links=links, head=head)
    body_file = _folder(run) / "pr-body.md"
    body_file.write_text(body, encoding="ascii")
    existing = github.open_pr_for_branch(run["slug"], run["branch"])
    if existing:
        github.edit_pr_body(run["slug"], existing["number"], body_file)
        run["pr"] = existing
    else:
        title = ascii_safe(run.get("title") or subjects[0])
        run["pr"] = github.create_pr(run["slug"], run["branch"], title, body_file)
    run["pr"]["head"] = head
    run["phase"] = "merge"
    return True


def _park_reasons(run: dict) -> list[str]:
    reasons = []
    if run["risk"]["level"] == "high":
        reasons.append("risk is HIGH: " + "; ".join(run["risk"]["raised_by"]
                                                    or [run["risk"]["reviewer_reason"]]))
    skipped = [s for s, state in run["steps"].items() if state != COMPLETED]
    if skipped:
        reasons.append("SKIPPED steps: " + ", ".join(skipped))
    verdict = (run["verify"] or {}).get("verdict")
    if run["steps"]["verify"] == COMPLETED and verdict not in ("go", "no-surface"):
        reasons.append(f"verify verdict is {verdict}")
    return reasons


def _step_merge(run: dict) -> dict:
    repo = _repo(run)
    pr = run["pr"]
    state = github.pr_state(run["slug"], pr["number"])
    if state["state"] == "MERGED":  # merged by an earlier attempt that did not finish
        return _merged(run)
    if state["state"] == "CLOSED":
        raise ShipError("pr-closed", f"{pr['url']} was closed without merging.",
                        "Ask the owner; then cc-ship abort, or reopen it and cc-ship continue")
    dirty = _worktree_dirty(run)
    if dirty:
        return dirty
    moved = _head_moved(run)
    if moved:
        _restart_round(run, moved)
        return advance(run)
    failed = github.failed_checks(run["slug"], pr["number"])
    if failed:
        run["phase"] = "fix"
        run["fix_head"] = gitops.head(repo)
        return _set(run, WAITING_ON_AUTHOR,
                    f"Pull request {pr['url']} has FAILED checks: {', '.join(failed)}. "
                    "Fix them, commit, then run: cc-ship continue",
                    {"error": "failed checks: " + ", ".join(failed), "code": "check-failed",
                     "help": "Fix them, commit, then run: cc-ship continue"})
    reasons = _park_reasons(run)
    if reasons:
        run["phase"] = "parked"
        return _set(run, WAITING_ON_OWNER,
                    f"Pull request {pr['url']} is parked for the owner to merge because: "
                    + " / ".join(reasons)
                    + ". Tell him in one message, then end your turn. After he has merged it, "
                      "run: cc-ship continue")
    subjects = gitops.commit_subjects(repo, gitops.merge_base(repo), pr["head"])
    subject = f"{ascii_safe(run.get('title') or subjects[0])} (#{pr['number']})"
    body = "\n".join(f"- {ascii_safe(line)}" for line in subjects)
    github.merge_pr(run["slug"], pr["number"], pr["head"], subject, body)
    return _merged(run)


def _merged(run: dict) -> dict:
    pr = run["pr"]
    state = github.pr_state(run["slug"], pr["number"])
    if state["state"] != "MERGED":
        raise ShipError("merge-not-confirmed", f"GitHub does not show {pr['url']} as merged.",
                        "Look at the pull request, then run: cc-ship continue")
    if state["headRefOid"] != pr["head"]:
        raise ShipError("head-mismatch",
                        f"The merged head {state['headRefOid'][:12]} is not the attested head "
                        f"{pr['head'][:12]}.", "Tell the owner; do not ship again on this branch.")
    github.delete_remote_branch(run["slug"], run["branch"])
    run["merged_commit"] = (state.get("mergeCommit") or {}).get("oid")
    run["phase"] = "done"
    return _set(run, MERGED,
                f"Merged {pr['url']}. Park your worktree on main (git switch --detach origin/main, "
                "or remove the worktree), then end your session.")


# --------------------------------------------------------------------------- continue / abort

def resume(run: dict) -> dict:
    state, phase = run["state"], run["phase"]
    if run.get("session") is None:
        preview.remove_bypass_state(_folder(run) / "browser-state.json")
    if state == WORKING:
        if run.get("session") or phase == "preview":
            raise ShipError("still-working", "A session is still working on this run.",
                            "Run: cc-ship wait")
        return advance(run)  # an earlier cc-ship stopped between two steps
    if state == WAITING_ON_OWNER:
        if phase == "owner":
            raise ShipError("owner-pending", "The owner has not answered every finding yet.",
                            run["next_step"])
        if phase == "parked":
            pr_state = github.pr_state(run["slug"], run["pr"]["number"])
            if pr_state["state"] == "MERGED":
                return _merged(run)
            if pr_state["state"] == "CLOSED":
                run["phase"] = "done"
                return _set(run, runstore.ABORTED, "The owner closed the pull request. End your session.")
            raise ShipError("still-parked", f"{run['pr']['url']} is still waiting for the owner.",
                            "End your turn; run cc-ship continue after he has merged it.")
        if phase == "fix-limit":
            raise ShipError("fix-limit", "The fix-round limit was reached; the owner decides.",
                            run["next_step"])
    if state == WAITING_ON_AUTHOR:
        if phase == "fix":
            if gitops.head(_repo(run)) == run.get("fix_head"):
                raise ShipError("nothing-new", "No new commit since the findings were handed over.",
                                "Commit your fixes, then run: cc-ship continue")
            run["fix_rounds"] += 1
        _reset_round(run)
        run["findings"] = []
        _set(run, WORKING, "")
        return advance(run)
    if state == FAILED:
        if run.get("session"):
            _stop_session_quietly(run["session"]["id"], "cc-ship: step restarted")
            run["session"] = None
        _set(run, WORKING, "")
        return advance(run)
    raise ShipError("run-finished", f"This run is {state}.", "Start a new run with cc-ship start")


def abort(run: dict) -> dict:
    for session in run["sessions"]:
        if fleet.find_session(session["id"]) is not None:
            _stop_session_quietly(session["id"], "cc-ship run aborted")
    preview.remove_bypass_state(_folder(run) / "browser-state.json")
    run["session"] = None
    run["phase"] = "done"
    return _set(run, runstore.ABORTED, "The run is aborted; the branch is untouched.")
