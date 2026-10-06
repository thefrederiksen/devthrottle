"""Break one rule of devthrottle_internal#2307 on purpose, run the tests that guard it, record them going red, restore.

One foreground command per rule, from the repository root:

    python docs/proof/teams-2307/red-proof.py <name>

Writes docs/proof/teams-2307/red-<name>.txt: the mutation, the command, and the test output. The source is restored in
a `finally` from the committed tree (`git checkout -- <file>`), and the run fails loudly if the tree is not clean
afterwards. Every run builds again (no --no-build), so a mutated binary can never stand in for the restored source.
The method is #2309's (docs/proof/teams-2309/red-proof.py).
"""

import os
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent

UNIT = ["dotnet", "test", "src/CcDirector.Gateway.UnitTests", "--filter",
        "FullyQualifiedName~Teams.Questions|FullyQualifiedName~Teams.Reports", "-nologo"]


def vitest(project, path):
    return {"cmd": ["npx", "vitest", "run", path], "cwd": REPO / project}


PAGE = vitest("apps/cockpit", "src/teams/collaborator/questionsPage.test.tsx")
REPORTS_PAGE = vitest("apps/cockpit", "src/teams/collaborator/reportsPage.test.tsx")

TQ = "src/CcDirector.Gateway/Api/TeamQuestionEndpoints.cs"
TR = "src/CcDirector.Gateway/Api/TeamReportEndpoints.cs"
STORE = "src/CcDirector.Gateway/DevReports/DevReportStore.cs"
FOLD = "src/CcDirector.Gateway/DevReports/DevReportPromptFold.cs"
DELIVERY = "src/CcDirector.Gateway/DevReports/DevReportDelivery.cs"
RULES = "src/CcDirector.Gateway/Teams/TeamEndpointRules.cs"
QPAGE = "apps/cockpit/src/teams/collaborator/QuestionsPage.tsx"
VIEWS = "apps/cockpit/src/teams/collaborator/TeamReportViews.tsx"

MUTATIONS = {
    "comment-in-the-choice": {
        "rule": "The member's comment is never part of the item delivered to the session: the choice carries no words.",
        "file": TQ,
        "old": "            var item = ChoiceItem(question, option);",
        "new": "            var item = ChoiceItem(question, option) with { Comment = words };",
        "runs": [UNIT],
    },
    "store-takes-member-words": {
        "rule": "The store refuses a member's item that carries any words of their own.",
        "file": STORE,
        "old": "            if (items.Any(i => i.Kind != DevReportItem.Answer || i.Comment.Length > 0 || i.Text.Length > 0))",
        "new": "            if (false)",
        "runs": [UNIT],
    },
    "fold-takes-member-words": {
        "rule": "The prompt refuses a member's item that carries any words of their own.",
        "file": FOLD,
        "old": "        if (memberWords is not null)\n            throw",
        "new": "        if (memberWords is not null && false)\n            throw",
        "runs": [UNIT],
    },
    "comment-to-the-session": {
        "rule": "The comment goes to the person who asked, never into the agent's conversation (here: written as a reply on the report).",
        "file": TQ,
        "old": "            if (words.Length > 0)\n                _comments.Add(",
        "new": "            if (words.Length > 0)\n                _store.AddReply(team, report.Id, words, _utcNow());\n            if (words.Length > 0)\n                _comments.Add(",
        "runs": [UNIT],
    },
    "comment-to-the-team-session-over-the-wire": {
        "rule": "Over the wire, a live TEAM session never reads the member's comment (here: written as a reply on the report).",
        "file": TQ,
        "old": "            if (words.Length > 0)\n                _comments.Add(",
        "new": "            if (words.Length > 0)\n                _store.AddReply(team, report.Id, words, _utcNow());\n            if (words.Length > 0)\n                _comments.Add(",
        "runs": [["dotnet", "test", "src/CcDirector.Gateway.Tests", "--filter",
                  "FullyQualifiedName~HostedTeamQuestionsTests.Issue2307_TeamKeyVariant", "-nologo"]],
    },
    "comment-not-to-the-person": {
        "rule": "The comment reaches the person who asked (their own report's page).",
        "file": TQ,
        "old": "            if (words.Length > 0)\n                _comments.Add(",
        "new": "            if (words.Length < 0)\n                _comments.Add(",
        "runs": [UNIT],
    },
    "not-addressed-is-answered": {
        "rule": "A question is answered only by a person the report was sent to; anyone else gets not-found and nothing is stored.",
        "file": TQ,
        "old": "        if (!Guid.TryParse(reportId, out var id) || _recipients.RowFor(team, id, caller) is not { } row",
        "new": "        if (!Guid.TryParse(reportId, out var id) || (_recipients.RowFor(team, id, caller) ?? new DevReportRecipientEntity { ReportId = id, RecipientSubject = caller, SentVersion = 1 }) is not { } row",
        "runs": [UNIT],
    },
    "version-not-checked": {
        "rule": "An answer names the version shown; a version the member no longer holds is refused.",
        "file": TQ,
        "old": "        if (version != row.SentVersion)\n        {",
        "new": "        if (version != row.SentVersion && version < 0)\n        {",
        "runs": [UNIT],
    },
    "answered-twice": {
        "rule": "A second answer from the same person to the same question is refused and not sent again.",
        "file": TQ,
        "old": "            if (_store.AnswersBy(team, caller, [report.Id]).ContainsKey((report.Id, question.Id)))",
        "new": "            if (_store.AnswersBy(team, caller, [report.Id]).ContainsKey((report.Id, question.Id)) && caller.Length < 0)",
        "runs": [UNIT],
    },
    "ended-session-not-checked": {
        "rule": "An answer to a session that has ended is refused with the Gateway's sentence, and nothing is stored.",
        "file": TQ,
        "old": "            if (_delivery.Liveness(team, report.SessionId).Reach == DevReportSessionReach.Ended)\n            {",
        "new": "            if (_delivery.Liveness(team, report.SessionId).Reach == DevReportSessionReach.Ended && caller.Length < 0)\n            {",
        "runs": [UNIT],
    },
    "comment-to-an-author-who-cannot-read": {
        "rule": "A worded answer is refused when the person who asked can no longer read comments in the team.",
        "file": TQ,
        "old": "        if (words.Length > 0 && !TeamReports.AuthorCanReceive(_access, teamId, report))",
        "new": "        if (words.Length > 0 && !TeamReports.AuthorCanReceive(_access, teamId, report) && words.Length < 0)",
        "runs": [UNIT],
    },
    "answers-replace-across-people": {
        "rule": "A later answer replaces only the same person's waiting answer; two people's answers both reach the session.",
        "file": STORE,
        "old": "           && string.Equals(row.AnswererSubject, answererSubject, StringComparison.Ordinal);",
        "new": "           && true;",
        "runs": [UNIT],
    },
    "member-answer-said-to-be-the-owners": {
        "rule": "The session is told the answer came from a person the report was sent to, not from the owner.",
        "file": DELIVERY,
        "old": "                    row.AnswererSubject is not null))",
        "new": "                    false))",
        "runs": [UNIT],
    },
    "viewer-answers-on": {
        "rule": "The Reports viewer's answer controls stay off: questions are answered on the Questions page (Tech Lead ruling).",
        "file": TR,
        "old": "            // control here would be the R1 defect again. The page follows this flag and nothing else.\n            answersOpen = false,",
        "new": "            // control here would be the R1 defect again. The page follows this flag and nothing else.\n            answersOpen = true,",
        "runs": [UNIT],
    },
    "questions-route-undeclared": {
        "rule": "The questions routes state the row 'answer questions' for every role; an undeclared route is refused in a team.",
        "file": RULES,
        "old": "        new TeamEndpointRule(Api.TeamQuestionEndpoints.GroupPath, TeamMethods.Any, TeamAction.AnswerQuestionsSendRequestsReadReports,\n            TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),",
        "new": "",
        "runs": [UNIT],
    },
    "answered-reads-not-read": {
        "rule": "The author's Sent to list says 'Answered a question' for a person who answered without opening the report (Tech Lead ruling).",
        "file": TR,
        "old": "                    : answered.Contains(r.RecipientSubject) ? TeamReportEndpoints.AnsweredLabel",
        "new": "                    : answered.Contains(r.RecipientSubject) && r.SentVersion < 0 ? TeamReportEndpoints.AnsweredLabel",
        "runs": [UNIT],
    },
    "page-trims-the-words": {
        "rule": "The page sends the person's words exactly as typed.",
        "file": QPAGE,
        "old": "      setShown(await answerQuestion(teamId, q, chosen, comment));",
        "new": "      setShown(await answerQuestion(teamId, q, chosen, comment.trim()));",
        "runs": [PAGE],
    },
    "page-ignores-can-comment": {
        "rule": "The page offers a comment box only where the Gateway says so.",
        "file": QPAGE,
        "old": "          {q.canComment && (\n            <textarea",
        "new": "          {(q.canComment || true) && (\n            <textarea",
        "runs": [PAGE],
    },
    "page-no-recommended-preselect": {
        "rule": "The recommended option is picked when the card is drawn (CONTRACT.md section 2).",
        "file": QPAGE,
        "old": "  const [chosen, setChosen] = useState(recommended);",
        "new": "  const [chosen, setChosen] = useState(\"\");",
        "runs": [PAGE],
    },
    "reports-page-hides-the-questions-line": {
        "rule": "A report sent to the reader says, in the Gateway's words, that questions wait on them and links to Questions.",
        "file": VIEWS,
        "old": "              {detail.report.questionsLabel !== null && (",
        "new": "              {false && (",
        "runs": [REPORTS_PAGE],
    },
}


def failed_in_trx(out):
    return [l for l in out.splitlines() if l.strip().startswith("Failed CcDirector")][:40]


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in MUTATIONS:
        sys.exit("usage: red-proof.py <" + "|".join(MUTATIONS) + ">")
    name = sys.argv[1]
    m = MUTATIONS[name]
    if subprocess.run(["git", "diff", "--quiet", "--", m["file"]], cwd=REPO).returncode != 0:
        sys.exit(f"ERROR: {m['file']} has uncommitted changes; commit them first, the restore would discard them")
    path = REPO / m["file"]
    source = path.read_text(encoding="utf-8")
    old = m["old"].replace("\n", "\r\n") if "\r\n" in source and "\r\n" not in m["old"] else m["old"]
    new = m["new"].replace("\n", "\r\n") if "\r\n" in source and "\r\n" not in m["new"] else m["new"]
    if source.count(old) != 1:
        sys.exit(f"ERROR: the mutation text for {name} is found {source.count(old)} times in {m['file']}, not once")

    lines = [f"RED PROOF: {name}", f"Rule: {m['rule']}", f"File: {m['file']}", "", "Mutation - replaced:", m["old"],
             "with:", m["new"] or "(nothing)", ""]
    try:
        path.write_text(source.replace(old, new), encoding="utf-8", newline="")
        for run in m["runs"]:
            cmd, cwd = (run["cmd"], run["cwd"]) if isinstance(run, dict) else (run, REPO)
            print(f"[run] {' '.join(cmd)}", flush=True)
            result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                    shell=(cmd[0] == "npx"), env={**os.environ, "NO_COLOR": "1", "FORCE_COLOR": "0"})
            out = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout + result.stderr)
            out = out.encode("ascii", "replace").decode("ascii")
            keep = [l for l in out.splitlines()
                    if any(k in l for k in ("Failed ", "FAIL ", "Passed!", "Failed!", "Tests ", "Test Files", "AssertionError",
                                            "Assert.", "Expected", "Actual", "error CS", "error TS"))]
            lines += [f"Command: {' '.join(cmd)}", f"Exit code: {result.returncode}", "Output (the lines that say what failed):"]
            lines += keep[:120]
            lines.append("")
            print(f"[exit] {result.returncode}", flush=True)
    finally:
        subprocess.run(["git", "checkout", "--", m["file"]], cwd=REPO, check=True)
    if subprocess.run(["git", "diff", "--quiet", "--", m["file"]], cwd=REPO).returncode != 0:
        sys.exit(f"ERROR: {m['file']} was not restored")
    lines.append("Restored from the committed tree afterwards; git diff on the file is empty.")
    (OUT / f"red-{name}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines[-30:]))


if __name__ == "__main__":
    main()
