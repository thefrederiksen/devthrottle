using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #457/#462: an architecture fitness function that pins the no-cross-machine-loopback
/// policy so it cannot silently regress.
///
/// It scans production C# under <c>src/</c> (excluding <c>*.Tests</c>, bin, obj) for the
/// literals <c>127.0.0.1</c> / <c>localhost</c>. Every file that currently contains one is on
/// the <see cref="Allowlist"/> below WITH A REASON - each is a legitimate SAME-machine use
/// (loopback bind, same-box hop, a clearly-labelled local-only UI string) or a comment that
/// documents the policy itself. A NEW production file that hardcodes loopback fails this test,
/// forcing a reviewer to either route through the Gateway / a mode-appropriate address, or add
/// the file here with an explicit justification.
///
/// Scope/limitation (stated, not hidden - no silent caps): this is FILE-level, so it catches a
/// brand-new loopback file, not a new loopback line added to an already-listed file. The
/// behavioral guards (ForwardDestination, the resolvers) cover the latter. The allowlist is
/// also kept honest: a stale entry (file gone, or no longer contains a literal) fails too, so
/// the list shrinks as loopback is removed.
/// </summary>
public sealed class NoCrossMachineLoopbackGuardTests
{
    /// <summary>
    /// Production files that legitimately contain a loopback literal, each with the reason.
    /// Keyed by repo-relative path (forward slashes).
    /// </summary>
    private static readonly Dictionary<string, string> Allowlist = new()
    {
        // --- Loopback BIND / same-machine control surface (the deliberate security boundary) ---
        // Remove-the-network-port mission, phase 5: SEVEN entries left this list at once, because the
        // Director's own listener was deleted. ControlApiHost.cs (no Kestrel bind), ControlApiGuard.cs
        // (deleted with the routes it guarded), InstanceRegistration.cs (registers no endpoint),
        // GatewayConnectivitySelfTest.cs (the ladder is outbound-only now), SessionManager.cs (no
        // CC_DIRECTOR_API stamp), SelectDirectorDialog.axaml.cs (liveness from registrations, not a
        // port probe) and App.axaml.cs (no listening log line) carry no loopback literal any more -
        // which is this list doing exactly what its stale-entry check promises: shrinking as loopback
        // is removed. Merging main at the landing added a fourth: main's own popup fix (pull request
        // #2447) had rewritten PortAllocator.cs and its allowlist entry, and this branch deletes that
        // file outright - the entry goes with it, which is the same fix arrived at permanently.
        ["src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs"] = "Maps the tailnet front door to local loopback backend.",
        ["src/CcDirector.ControlApi/GatewayEnrollmentClient.cs"] = "Epic #1069 A: doc comment on EnrollSignedInAsync states the same-machine caller MUST pass a LOOPBACK gatewayUrl (http://127.0.0.1:<local gateway port>) so the Gateway's guardrail-1 IsLoopback check passes. Documents the policy; the literal address is built by the panel's BuildLoopbackEnrollUrl.",
        // Gateway Cleanup mission (the cut): ControlEndpoints.cs (cut to the 6-item loopback floor),
        // DictationEndpoint.cs + TerminalStreamEndpoint.cs (deleted), and SessionWsProxyEndpoints.cs (the
        // loopback-guarded HTTP reverse-proxy was deleted, it is a tunnel-dispatch-only file now) were
        // removed from this allowlist because they no longer carry a loopback literal.
        ["src/CcDirector.Gateway/GatewayHost.cs"] = "Local loopback bind / same-machine wiring.",
        ["src/CcDirector.Gateway/GatewayService.cs"] = "Probes THIS process's own Gateway port on loopback to diagnose a failed start (is the port taken by our own gateway, another app, or nothing?). Same machine by definition - it is asking about its own bind - and it moved here unchanged from GatewayTrayController when the lifecycle left the tray app.",
        ["src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs"] = "Maps the tailnet front door to local loopback backends.",
        ["src/CcDirector.Gateway/Api/RecordingEndpoints.cs"] = "Local recording paths.",
        ["src/CcDirector.Gateway/Api/MobileQrEndpoint.cs"] = "devthrottle_internal #1508: RECOGNIZES a loopback host in order to REFUSE it, which is the inverse of this policy's concern - nothing here dials one. The Phone panel's scannable code encodes the address the Cockpit was reached on, so a Cockpit opened on localhost would produce a code that scans perfectly and then times out on the phone; the endpoint answers 409 with the reason instead. Only the NAME is a literal - the address families are left to IPAddress.IsLoopback.",
        // Remove-the-network-port mission, phase 6: FOUR more entries left this list at once, because
        // the LAUNCHER's listener was deleted. LauncherHost.cs is gone entirely (the Kestrel bind it
        // was listed for WAS the launcher's listener); LauncherLifecycleRelay.cs no longer dials
        // anything (the REST fallback arm was deleted - the stream the launcher opens is the only
        // path, so there is no address, loopback or otherwise, in the file); MachineEndpoints.cs
        // carries no dial-back wiring for the same reason; and the launcher's Program.cs self-update
        // helper reads the registration file instead of posting /shutdown and probing /healthz on
        // loopback. The list shrinking is this guard doing exactly what its stale-entry check
        // promises.
        ["src/CcDirector.Gateway/Data/GatewayDbContextDesignTimeFactory.cs"] = "Design-time-only EF tooling factory (dotnet ef migrations): the localhost Postgres connection string is a THROWAWAY design value - migrations add builds the model and writes source without ever opening the connection, and the running Gateway wires its context through GatewayDatabase instead.",
        ["src/CcDirector.Gateway.Migrations.Postgres/GatewayStatsDbContextPostgresDesignTimeFactory.cs"] = "Design-time-only EF tooling factory for the statistics context's POSTGRES migration chain, same shape as GatewayDbContextDesignTimeFactory.cs above: the localhost connection string is a THROWAWAY design value that migrations add never opens, and the running Gateway selects its statistics connection through StatsConnectionSelection instead.",
        // The Fleet Manager mission, step 9: LoopbackCarModeFleet.cs no longer appears here. It was the
        // Assistant's fleet brain calling this Gateway over loopback, and it went with the Assistant.
        ["src/CcDirector.GatewayApp/Program.cs"] = "Local Gateway bootstrap.",
        // Remove-the-network-port mission, phase 4: DirectorSupervisor.cs no longer appears here. It
        // supervised the Director by posting to its loopback Control API; it now reads the files the
        // Director maintains and raises a named signal, so it carries no address of any kind.
        ["src/CcDirector.Core/Account/LoopbackLoginListener.cs"] = "Binds an HttpListener on 127.0.0.1 only (operating-system-assigned ephemeral port) to receive the first-run browser sign-in hand-back; same-machine loopback trust boundary (security rule DT-07, issue #581).",
        // Remove-the-network-port mission, phase 4: LauncherRestartClient.cs no longer appears here
        // either. "Install it now" asked the launcher over http://127.0.0.1:{port}/director/restart,
        // reading a discovery file for the port and a token file for the credential; it raises a named
        // signal now. The same-machine scoping that entry argued for is stronger rather than weaker - the
        // signal is named for this machine's storage root, so there is no address to get wrong.
        // Browsers feature: an automation browser's Chrome remote-debugging port is bound by Chrome on
        // loopback, so machine-locality is the feature's designed security property - only an agent on
        // THIS machine can attach (handover 2026-07-23). These three carry the loopback literal on purpose.
        ["src/CcDirector.Core/Browsers/AutomationBrowser.cs"] = "Doc comment: BU_CDP_URL is http://127.0.0.1:<port> - the debug port is loopback by design (machine-local browsers).",
        ["src/CcDirector.Core/Browsers/AutomationBrowserRegistry.cs"] = "AttachInfoFor builds the same-machine BU_CDP_URL (http://127.0.0.1:{port}); Chrome binds the debug port on loopback, and the loopback port probe binds 127.0.0.1 to test freeness.",
        ["src/CcDirector.Core/Browsers/AutomationBrowserService.cs"] = "Probes and CDP-closes the browser over its own loopback debug port (http://127.0.0.1:{port}/json/version); same machine by construction.",

        // --- Loopback DETECTION / classification / labelling (the no-loopback policy itself) ---
        ["src/CcDirector.Core/Network/TailscaleIdentity.cs"] = "Formats a CLEARLY-LABELLED local-only fallback string; never advertised cross-machine.",
        ["src/CcDirector.Core/Network/LoopbackPeerResolver.cs"] = "Resolves/recognizes loopback peers (same-machine).",
        ["src/CcDirector.Core/Network/EndpointProbe.cs"] = "Endpoint probing helpers incl. loopback recognition.",
        ["src/CcDirector.Core/Utilities/LinkDetector.cs"] = "Detects localhost URLs in terminal text (display only).",
        ["src/CcDirector.Core/Configuration/AddressingMode.cs"] = "Doc comment states the no-cross-machine-loopback policy.",
        ["src/CcDirector.Core/Configuration/GatewayConfig.cs"] = "Classifies whether a URL addresses THIS machine's own Gateway (loopback / \"localhost\" host recognition); same-machine detection, never advertised cross-machine.",

        // --- Contracts / DTO docs that DESCRIBE endpoints ---
        ["src/CcDirector.Gateway.Contracts/DirectorDto.cs"] = "Doc comment example endpoint string.",
        ["src/CcDirector.Gateway.Contracts/CockpitInfoDto.cs"] = "Doc comment example.",

        // --- Gateway routing that intentionally references same-origin / local ---
        ["src/CcDirector.Gateway/Api/GatewayEndpoints.cs"] = "Local/same-origin references in the Gateway router.",

        // --- Desktop app: local Director/Cockpit access + local-only labels ---
        ["src/CcDirector.Avalonia/CockpitUrlResolver.cs"] = "Resolves the local Cockpit URL (same machine).",
        ["src/CcDirector.Avalonia/Controls/GatewayConnectionPanel.axaml.cs"] = "Epic #1069 A: BuildLoopbackEnrollUrl dials the co-located Gateway's /devices/enroll-signed-in at the literal 127.0.0.1 BY DESIGN - the Gateway's guardrail 1 requires the enroll caller to be a proven SAME-machine loopback connection (IPAddress.IsLoopback), so a machine-name or tailnet address would 403. Same-machine only; the enrolled key then registers/heartbeats over the pick's real address.",
        ["src/CcDirector.Avalonia/MainWindow.axaml.cs"] = "Local-only labelled endpoint strings (handover/about).",
        ["src/CcDirector.Avalonia/Controls/ConnectionsView.axaml.cs"] = "Local connection references.",
        ["src/CcDirector.Avalonia/ExpandedEditorDialog.axaml.cs"] = "Local references.",
        ["src/CcDirector.Avalonia/WorkflowRecorderWindow.axaml.cs"] = "Local browser-automation references.",
        ["src/CcDirector.Avalonia/HostedAi/DesktopHostedAiCta.cs"] = "Doc comments only: both loopback mentions state the desktop NEVER opens a localhost URL (it resolves the Cockpit front-door first), documenting the no-loopback policy.",
        ["src/CcDirector.Avalonia/Voice/SpeakDialog.axaml.cs"] = "Local voice dialog references.",
        ["src/CcDirector.Avalonia/Voice/BatchDictationRecorder.cs"] = "Doc comment notes the batch path has no localhost WebSocket roundtrip.",
        ["src/CcDirector.Avalonia/HostedAi/DesktopHostedAiCta.cs"] = "Doc comments only: describe that Settings resolves the Cockpit front door and never opens a localhost URL (states the no-loopback policy).",
        ["src/CcDirector.Core/Browser/WorkflowRunner.cs"] = "Drives a local browser via loopback CDP.",
    };

    [Fact]
    public void No_new_production_file_hardcodes_cross_machine_loopback()
    {
        var root = GetRepoRoot();
        var srcDir = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Relative(root, file);
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            if (IsTestProject(rel)) continue;

            var text = File.ReadAllText(file);
            if (!ContainsLoopback(text)) continue;
            if (Allowlist.ContainsKey(rel)) continue;

            offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "New production file(s) contain a loopback literal (127.0.0.1/localhost). Cross-machine code must "
            + "route through the Gateway or a mode-appropriate address (issue #457). If this is a legitimate "
            + "SAME-machine use, add it to the allowlist in NoCrossMachineLoopbackGuardTests with a reason:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Allowlist_has_no_stale_entries()
    {
        var root = GetRepoRoot();
        var stale = new List<string>();

        foreach (var (rel, _) in Allowlist)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { stale.Add($"{rel} (file no longer exists)"); continue; }
            if (!ContainsLoopback(File.ReadAllText(full))) stale.Add($"{rel} (no longer contains a loopback literal - remove it)");
        }

        Assert.True(stale.Count == 0,
            "The loopback allowlist has stale entries; remove them so the list shrinks as loopback is removed:\n  "
            + string.Join("\n  ", stale));
    }

    private static bool ContainsLoopback(string text)
        => text.Contains("127.0.0.1", StringComparison.Ordinal)
           || text.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    // One shared predicate (see TestProjectPath): four guards each carried their own copy of this
    // decision, and all four were wrong at once when the Gateway suite split moved 2,750 files into a
    // project spelled ".UnitTests", which does not contain ".Tests/".
    private static bool IsTestProject(string rel) => TestProjectPath.IsTestProject(rel);

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
