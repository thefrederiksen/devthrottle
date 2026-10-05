"""Revert proofs for devthrottle_internal#2308: break one rule, run the guarding tests, restore - always."""
import subprocess, sys, re, os

REPO = r"D:\ReposFred\devthrottle-teams-2308"
os.chdir(REPO)

MUTATIONS = {
    "M1": ("src/CcDirector.Gateway/Teams/TeamPermissions.cs",
           'or mark it Done", Yes, Yes, No, No),', 'or mark it Done", Yes, Yes, Yes, No),',
           "The role table lets a Developer read and decide requests"),
    "M2": ("src/CcDirector.Gateway/Teams/TeamRequestStates.cs",
           '        if (decision != TeamRequestDecision.Decline)\n            return null;\n        if (trimmed.Length == 0)',
           '        if (decision != TeamRequestDecision.Decline)\n            return null;\n        if (trimmed.Length < 0)',
           "Not doing this no longer requires a reason"),
    "M3": ("src/CcDirector.Gateway/Api/TeamRequestEndpoints.cs",
           '        ArgumentNullException.ThrowIfNull(ctx);\n        if (AuthMiddleware.CallingSession(ctx) is { } session)',
           '        ArgumentNullException.ThrowIfNull(ctx);\n        if (ctx.Request is not null) return null;\n        if (AuthMiddleware.CallingSession(ctx) is { } session)',
           "The request routes serve any credential, not only a person's phone or browser"),
    "M4": ("src/CcDirector.Gateway/Teams/TeamRequestStore.cs",
           '            using var ctx = _db.CreateContext(new TenantId(teamId));\n            var request = ctx.TeamRequests.FirstOrDefault(r => r.Id == id);',
           '            using var ctx = _db.CreateUnscopedContext();\n            var request = ctx.TeamRequests.IgnoreQueryFilters().FirstOrDefault(r => r.Id == id);',
           "Deciding a request looks it up across every team, not only the team named"),
    "M5": ("src/CcDirector.Gateway/Teams/TeamRequestStore.cs",
           'var views = Read(teamId, caller, r => r.SenderSubject == caller);',
           'var views = Read(teamId, caller, null);',
           "A sender's own list shows every request in the team"),
    "M6": ("src/CcDirector.Gateway/Teams/TeamRequestStates.cs",
           '            Declined or Done => false,',
           '            Declined or Done => true,',
           "Not doing this and Done are no longer final"),
    "H1": ("src/CcDirector.Gateway/Api/TeamRequestEndpoints.cs",
           '        ArgumentNullException.ThrowIfNull(ctx);\n        if (AuthMiddleware.CallingSession(ctx) is { } session)',
           '        ArgumentNullException.ThrowIfNull(ctx);\n        if (ctx.Request is not null) return null;\n        if (AuthMiddleware.CallingSession(ctx) is { } session)',
           "The request routes serve a Director's device key (over the wire)"),
    "H2": ("src/CcDirector.Gateway/Teams/TeamRequestStore.cs",
           '        var now = _utcNow();\n        var request = new TeamRequestEntity',
           '        FileLog.Write($"[TeamRequestStore] Send: text={text}");\n        var now = _utcNow();\n        var request = new TeamRequestEntity',
           "The request's words reach the Gateway log"),
    "H3": ("src/CcDirector.Gateway/GatewayHost.cs",
           '            TeamRequestEndpoints.Map(_app, TeamRequests, _tenantBoundary, TenantRegistry);\n        }',
           '        }\n        TeamRequestEndpoints.Map(_app, TeamRequests, _tenantBoundary, TenantRegistry);',
           "The request routes are mapped with Teams switched off"),
    "T1": ("src/CcDirector.Gateway/Util/SessionKeyGuard.cs",
           '    private static bool IsAllowed(string verb, string[] s)\n    {\n',
           '    private static bool IsAllowed(string verb, string[] s)\n    {\n        if (s.Length >= 1 && s[0] == "teams") return true;\n',
           "A session key may call every team route"),
    "L1": ("src/CcDirector.Gateway/Teams/TeamRequestStore.cs",
           '            ctx.SaveChanges();\n        }\n\n        FileLog.Write($"[TeamRequestStore] Send: stored request',
           '            ctx.SaveChanges();\n        }\n        using (var leak = _db.CreateUnscopedContext())\n        {\n            foreach (var k in leak.SessionKeys.ToList())\n                leak.FleetMessages.Add(new FleetMessageEntity { TenantId = k.TenantId, MessageId = Guid.NewGuid().ToString("N"), RecipientSessionId = k.SessionId, Kind = "message", Text = request.Text, TextHash = "leak", CreatedAtUtc = now });\n            leak.SaveChanges();\n        }\n\n        FileLog.Write($"[TeamRequestStore] Send: stored request',
           "A request's words are filed into every session's fleet inbox"),
    "F4R": ("src/CcDirector.Gateway/Tenancy/EntitlementScopes.cs",
           '[EntitlementRegistry.TierFree] = Set(HostedGateway),',
           '[EntitlementRegistry.TierFree] = Set(),',
           "The free plan no longer carries the hosted Gateway"),
}


def run(cmd):
    print(">", " ".join(cmd), flush=True)
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, p.stdout + p.stderr


def main(names, mode):
    files = {MUTATIONS[n][0] for n in names}
    for n in names:
        path, old, new, _ = MUTATIONS[n]
        s = open(path, encoding="utf-8").read()
        if s.count(old) != 1:
            raise SystemExit(f"{n}: the text to mutate is not found exactly once in {path}")
        open(path, "w", encoding="utf-8").write(s.replace(old, new))
    try:
        for n in names:
            print(f"MUTATION {n}: {MUTATIONS[n][3]}  ({MUTATIONS[n][0]})", flush=True)
        if mode == "unit":
            code, out = run(["dotnet", "test", r"src\CcDirector.Gateway.UnitTests", "--filter", "FullyQualifiedName~Teams", "-nologo"])
        else:
            code, out = run(["powershell", "-NoProfile", "-File", r"scripts\test-local.ps1", "-Gateway", "-Filter",
                             "FullyQualifiedName~HostedTeamRequestEndpointsTests|FullyQualifiedName~HostedTeamsDarkTests"])
        failed = sorted(set(re.findall(r"Failed (CcDirector\.[\w.]+)", out)))
        summary = [l for l in out.splitlines() if "Passed!" in l or "Failed!" in l or "outcome=" in l or "error CS" in l]
        print("\n".join(summary))
        print(f"exit={code}; tests RED under this mutation ({len(failed)}):")
        for f in failed:
            print("   ", f)
        if mode == "hosted":
            m = re.search(r"TRX files: (\S+)", out)
            if m:
                for trx in [os.path.join(m.group(1), x) for x in os.listdir(m.group(1)) if x.endswith(".trx")]:
                    t = open(trx, encoding="utf-8", errors="replace").read()
                    for name in re.findall(r'testName="([^"]+)"[^>]*outcome="Failed"', t):
                        print("    RED (trx):", name)
    finally:
        for f in files:
            subprocess.run(["git", "checkout", "--", f], check=True)
        dirty = subprocess.run(["git", "status", "--porcelain", "--", "src"], capture_output=True, text=True).stdout
        print("restored; src changes after restore:", repr(dirty), flush=True)


if __name__ == "__main__":
    main(sys.argv[2].split(","), sys.argv[1])
