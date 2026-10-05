"""Break one rule of devthrottle_internal#2309 on purpose, run the tests that guard it, record them going red, restore.

One foreground command per rule, from the repository root:

    python docs/proof/teams-2309/red-proof.py <name>

Writes docs/proof/teams-2309/red-<name>.txt: the mutation, the command, and the test output. The source is restored in
a `finally` from the committed tree (`git checkout -- <file>`), and the run fails loudly if the tree is not clean
afterwards. The next run builds again (no --no-build), so a mutated binary can never stand in for the restored source.
"""

import os
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent

UNIT = ["dotnet", "test", "src/CcDirector.Gateway.UnitTests", "--filter", "FullyQualifiedName~Teams.Reports", "-nologo"]


def hosted(filter_):
    return ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts/test-local.ps1", "-Gateway",
            "-Filter", filter_]


HOSTED_REPORTS = hosted("FullyQualifiedName~HostedTeamReportsTests")
HOSTED_DARK = hosted("FullyQualifiedName~HostedTeamsDarkTests")


def vitest(project, path):
    return {"cmd": ["npx", "vitest", "run", path], "cwd": REPO / project}


TRP = "src/CcDirector.Gateway/Api/TeamReportEndpoints.cs"
COMMENTS = "src/CcDirector.Gateway/DevReports/DevReportPersonComments.cs"
SAVE = "        ctx.DevReportComments.Add(row);\n        ctx.SaveChanges();\n"

MUTATIONS = {
    "not-sent-is-served": {
        "rule": "A report not sent to the caller is refused on the list, the metadata and the html routes.",
        "file": TRP,
        "old": "if (!Guid.TryParse(reportId, out var id) || _recipients.RowFor(team, id, caller) is not { } row",
        "new": "if (!Guid.TryParse(reportId, out var id) || (_recipients.RowFor(team, id, caller) ?? new DevReportRecipientEntity { ReportId = id, RecipientSubject = caller, SentVersion = 1 }) is not { } row",
        "runs": [UNIT, HOSTED_REPORTS],
    },
    "comment-also-a-reply": {
        "rule": "A comment is never readable with a session key: it is not written into the agent's conversation.",
        "file": COMMENTS,
        "old": SAVE,
        "new": "        ctx.DevReportComments.Add(row);\n"
               "        ctx.DevReportReplies.Add(new DevReportReplyEntity { TenantId = team.Value, ReportId = reportId, Text = text, AtUtc = nowUtc });\n"
               "        ctx.SaveChanges();\n",
        "runs": [HOSTED_REPORTS],
    },
    "comment-also-an-item": {
        "rule": "A comment is never handed to DevReportDelivery and never folded into a prompt toward the session.",
        "file": COMMENTS,
        "old": SAVE,
        "new": "        ctx.DevReportComments.Add(row);\n"
               "        var sid = ctx.DevReports.Where(r => r.Id == reportId).Select(r => r.SessionId).First();\n"
               "        ctx.DevReportItems.Add(new DevReportItemEntity { TenantId = team.Value, ReportId = reportId, SessionId = sid,\n"
               "            ClientItemId = Guid.NewGuid().ToString(\"N\"), Kind = \"note\", Text = text, Status = DevReportItemStates.Queued,\n"
               "            StatusLabel = \"Queued\", Sequence = 1000, SenderKind = \"owner\", SentAtUtc = nowUtc });\n"
               "        ctx.SaveChanges();\n",
        "runs": [HOSTED_REPORTS],
    },
    "report-owner-always-the-caller": {
        "rule": "Sending a report you did not write is refused: the gate asks the one resolver whose report it is.",
        "file": "src/CcDirector.Gateway/Teams/TeamCallerOwnership.cs",
        "old": "var mine = string.Equals(author, callerSubject, StringComparison.Ordinal);",
        "new": "var mine = true;",
        "runs": [UNIT, HOSTED_REPORTS],
    },
    "non-member-skipped-silently": {
        "rule": "Sending to someone who is not a member of the team is refused, and nothing is sent.",
        "file": TRP,
        "old": "            if (member is null)\n            {",
        "new": "            if (member is null) continue;\n            if (member is null)\n            {",
        "runs": [UNIT, HOSTED_REPORTS],
    },
    "sent-to-me-rule-removed": {
        "rule": "The recipient's routes are declared in TeamEndpointRules, so the gate admits them; undeclared, it refuses.",
        "file": "src/CcDirector.Gateway/Teams/TeamEndpointRules.cs",
        "old": "        new TeamEndpointRule(Api.TeamReportEndpoints.SentToMePattern, TeamMethods.Any, TeamAction.AnswerQuestionsSendRequestsReadReports,\n"
               "            TeamTarget.Team, TeamFrom: TeamFrom.RouteTeamId),\n",
        "new": "",
        "runs": [UNIT, HOSTED_REPORTS],
    },
    "routes-mapped-while-dark": {
        "rule": "The team report routes are absent while Teams is dark (CC_GATEWAY_TEAMS unset).",
        "file": "src/CcDirector.Gateway/GatewayHost.cs",
        "old": "            TeamReportEndpoints.Map(_app, _devReports, _devReportRecipients, _devReportComments, TeamRegistry, TeamAccess,\n"
               "                _tenantBoundary, TenantRegistry);\n        }\n",
        "new": "        }\n        TeamReportEndpoints.Map(_app, _devReports, _devReportRecipients, _devReportComments, TeamRegistry, TeamAccess,\n"
               "            _tenantBoundary, TenantRegistry);\n",
        "runs": [HOSTED_DARK],
    },
    "author-not-recorded": {
        "rule": "The report's author person is recorded at publish, through the one resolver.",
        "file": "src/CcDirector.Gateway/DevReports/DevReportAuthor.cs",
        "old": "return store.Publish(tenant, sessionId, key, html, status, title, nowUtc, author.Subject);",
        "new": "return store.Publish(tenant, sessionId, key, html, status, title, nowUtc, null);",
        "runs": [UNIT],
    },
    "cockpit-ignores-show-your-reports": {
        "rule": "Whether the page shows the person's own reports is the Gateway's answer, never the page's.",
        "file": "apps/cockpit/src/teams/collaborator/ReportsPage.tsx",
        "old": "setOwn(next.showYourReports ? await getMyTeamReports(teamId, signal) : null);",
        "new": "setOwn(await getMyTeamReports(teamId, signal));",
        "runs": [vitest("apps/cockpit", "src/teams/collaborator/reportsPage.test.tsx")],
    },
    "viewer-ignores-the-api": {
        "rule": "A team report opens in the ONE shared viewer, fed from the team's routes - never the owner's.",
        "file": "packages/client-core/src/devreports/DevReportViewer.tsx",
        "old": "  const apiRef = useRef(api);",
        "new": "  const apiRef = useRef(gatewayDevReportApi);",
        "runs": [vitest("apps/cockpit", "src/teams/collaborator/reportsPage.test.tsx")],
    },
    "recipient-reads-the-latest-version": {
        "rule": "A recipient reads the version they were sent; a later one only when the author sends again (review F1).",
        "file": TRP,
        "old": "return DevReportEndpoints.ServeVersion(_store, Team(teamId), report, row.SentVersion, ctx);",
        "new": "return DevReportEndpoints.ServeVersion(_store, Team(teamId), report, null, ctx);",
        "runs": [UNIT],
    },
    "comments-open-whoever-the-author-is": {
        "rule": "A comment is refused when the author can no longer read comments here (review F6).",
        "file": TRP,
        "old": "        && _access.Decide(teamId, report.AuthorSubject, TeamAction.RunSessionsOnOwnComputers).Allowed;",
        "new": "        && true;",
        "runs": [UNIT],
    },
    "viewer-ignores-the-notes-flag": {
        "rule": "A team reader's frame gets no notes script, because the Gateway says notes are off (review F2).",
        "file": "packages/client-core/src/devreports/DevReportViewer.tsx",
        "old": "script: notesRef.current ? DEV_REPORT_NOTES_SCRIPT : NO_NOTES_SCRIPT,",
        "new": "script: DEV_REPORT_NOTES_SCRIPT,",
        "runs": [vitest("apps/cockpit", "src/teams/collaborator/reportsPage.test.tsx")],
    },
    "team-key-admitted-as-a-person": {
        "rule": "A key bound to the team's tenant is refused by the team routes' own admission, whatever the lease does (review F4).",
        "file": "src/CcDirector.Gateway/Api/TeamEndpoints.cs",
        "old": "        var subject = tenants.SubjectForTenant(tenant);\n        if (string.IsNullOrWhiteSpace(subject))",
        "new": "        var subject = tenants.SubjectForTenant(tenant) ?? \"someone\";\n        if (string.IsNullOrWhiteSpace(subject))",
        "runs": [hosted("FullyQualifiedName~Issue2309_F4_")],
    },
    "rail-without-team-pages": {
        "rule": "In a team where the person gets the whole app, the rail lists the team's pages from the Gateway's verdict.",
        "file": "apps/cockpit/src/AppShell.tsx",
        "old": "team.current !== null && team.current.app.full ? teamPagesNav(team.current.app) : []",
        "new": "[] as NavItem[]",
        "runs": [vitest("apps/cockpit", "src/teams/collaborator/collaboratorApp.test.tsx")],
    },
}


def failed_in_trx(out):
    """The failed test names from the result files test-local.ps1 wrote (it prints the folder, not the names)."""
    import glob
    import xml.etree.ElementTree as ET
    names = []
    for folder in set(re.findall(r"TRX files: (\S+)", out)):
        for trx in glob.glob(os.path.join(folder, "*.trx")):
            for r in ET.parse(trx).getroot().iter():
                if r.tag.endswith("UnitTestResult") and r.get("outcome") == "Failed":
                    names.append(f"  Failed (from {os.path.basename(trx)}): {r.get('testName')}")
    return names


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
            out = re.sub(r"\[[0-9;]*m", "", result.stdout + result.stderr)
            out = out.encode("ascii", "replace").decode("ascii")
            keep = [l for l in out.splitlines()
                    if any(k in l for k in ("Failed ", "[FAIL]", "FAIL ", "x ", "Passed!", "Failed!", "Total tests", "Passed:",
                                            "Failed:", "executed=", "FAIL", "> ", "outcome", "Tests ", "Test Files", "AssertionError",
                                            "Assert.", "Expected", "Actual", "error CS", "RED", "FAILED"))]
            lines += [f"Command: {' '.join(cmd)}", f"Exit code: {result.returncode}", "Output (the lines that say what failed):"]
            lines += keep[:120]
            lines += failed_in_trx(out)
            lines.append("")
            print(f"[exit] {result.returncode}", flush=True)
    finally:
        subprocess.run(["git", "checkout", "--", m["file"]], cwd=REPO, check=True)
    if subprocess.run(["git", "diff", "--quiet", "--", m["file"]], cwd=REPO).returncode != 0:
        sys.exit(f"ERROR: {m['file']} was not restored")
    lines.append("Restored from the committed tree afterwards; git diff on the file is empty.")
    (OUT / f"red-{name}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines[-40:]))


if __name__ == "__main__":
    main()
