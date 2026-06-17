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
        ["src/CcDirector.ControlApi/ControlApiHost.cs"] = "Binds Kestrel to loopback (tailscale mode); same-machine ControlApiBaseUrl for in-session agents.",
        ["src/CcDirector.ControlApi/InstanceRegistration.cs"] = "FSW same-machine ControlEndpoint is http://127.0.0.1:{port} by design.",
        ["src/CcDirector.ControlApi/ControlEndpoints.cs"] = "Same-machine control endpoint helpers / local references.",
        ["src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs"] = "Maps the tailnet front door to local loopback backend.",
        ["src/CcDirector.ControlApi/GatewayConnectivitySelfTest.cs"] = "Probes the local loopback Control API as part of self-test.",
        ["src/CcDirector.ControlApi/DictationEndpoint.cs"] = "Doc comment: 'Localhost-only by default' (describes the loopback bind).",
        ["src/CcDirector.ControlApi/TerminalStreamEndpoint.cs"] = "Doc comment: 'Localhost-only by default' (describes the loopback bind).",
        ["src/CcDirector.Gateway/Cockpit/CockpitProxy.cs"] = "Proxies to the co-located Cockpit child on loopback (same machine).",
        ["src/CcDirector.Gateway/Cockpit/CockpitSupervisor.cs"] = "Supervises/health-checks the local Cockpit child on loopback.",
        ["src/CcDirector.Gateway/GatewayHost.cs"] = "Local loopback bind / same-machine wiring.",
        ["src/CcDirector.Gateway/GatewayWorker.cs"] = "Same-machine worker wiring.",
        ["src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs"] = "Maps the tailnet front door to local loopback backends.",
        ["src/CcDirector.Gateway/Api/RecordingEndpoints.cs"] = "Local recording paths.",
        ["src/CcDirector.Gateway/Api/MachineEndpoints.cs"] = "Same-machine relay/launcher wiring.",
        ["src/CcDirector.GatewayApp/Program.cs"] = "Local Gateway bootstrap.",
        ["src/CcDirector.GatewayApp/GatewayTrayController.cs"] = "Opens the local Cockpit/Gateway via loopback.",
        ["src/CcDirector.GatewayApp/SettingsWindow.axaml.cs"] = "Local settings UI references.",
        ["src/CcDirector.Launcher/DirectorSupervisor.cs"] = "Supervises a local Director over loopback.",
        ["src/CcDirector.Launcher/LauncherHost.cs"] = "Local launcher loopback bind.",
        ["src/CcDirector.Launcher/Program.cs"] = "Self-update helper POSTs /shutdown + probes /healthz on the launcher's own loopback (same machine).",
        ["src/CcDirector.Cockpit/Program.cs"] = "Cockpit child binds loopback; fronted by the Gateway.",

        // --- Loopback DETECTION / classification / labelling (the no-loopback policy itself) ---
        ["src/CcDirector.Core/Network/TailscaleIdentity.cs"] = "Formats a CLEARLY-LABELLED local-only fallback string; never advertised cross-machine.",
        ["src/CcDirector.Core/Network/LoopbackPeerResolver.cs"] = "Resolves/recognizes loopback peers (same-machine).",
        ["src/CcDirector.Core/Network/EndpointProbe.cs"] = "Endpoint probing helpers incl. loopback recognition.",
        ["src/CcDirector.Core/Utilities/LinkDetector.cs"] = "Detects localhost URLs in terminal text (display only).",
        ["src/CcDirector.Core/Configuration/AddressingMode.cs"] = "Doc comment states the no-cross-machine-loopback policy.",

        // --- Contracts / DTO docs that DESCRIBE endpoints ---
        ["src/CcDirector.Gateway.Contracts/DirectorDto.cs"] = "Doc comment example endpoint string.",
        ["src/CcDirector.Gateway.Contracts/CockpitWsUrls.cs"] = "Doc comment explains why URLs are same-origin, not loopback.",
        ["src/CcDirector.Gateway.Contracts/CockpitShotUrls.cs"] = "Doc comment explains same-origin routing vs loopback.",
        ["src/CcDirector.Gateway.Contracts/CockpitInfoDto.cs"] = "Doc comment example.",

        // --- Gateway routing that intentionally references same-origin / local ---
        ["src/CcDirector.Gateway/Api/GatewayEndpoints.cs"] = "Local/same-origin references in the Gateway router.",
        ["src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs"] = "Implements + documents the loopback guard (this issue).",

        // --- Desktop app: local Director/Cockpit access + local-only labels ---
        ["src/CcDirector.Avalonia/App.axaml.cs"] = "Local Control API bootstrap / loopback references.",
        ["src/CcDirector.Avalonia/CockpitUrlResolver.cs"] = "Resolves the local Cockpit URL (same machine).",
        ["src/CcDirector.Avalonia/MainWindow.axaml.cs"] = "Local-only labelled endpoint strings (handover/about).",
        ["src/CcDirector.Avalonia/Controls/ConnectionsView.axaml.cs"] = "Local connection references.",
        ["src/CcDirector.Avalonia/Controls/DirectorView/DirectorView.axaml.cs"] = "Embedded local Director view.",
        ["src/CcDirector.Avalonia/ExpandedEditorDialog.axaml.cs"] = "Local references.",
        ["src/CcDirector.Avalonia/WorkflowRecorderWindow.axaml.cs"] = "Local browser-automation references.",
        ["src/CcDirector.Avalonia/Voice/SpeakService.cs"] = "Local voice service references.",
        ["src/CcDirector.Avalonia/Voice/SpeakDialog.axaml.cs"] = "Local voice dialog references.",
        ["src/CcDirector.Core/Browser/WorkflowRunner.cs"] = "Drives a local browser via loopback CDP.",
        ["src/CcDirector.Core/Sessions/SessionManager.cs"] = "Stamps the same-machine CC_DIRECTOR_API loopback URL for in-session agents.",
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

    private static bool IsTestProject(string rel)
        => rel.Contains(".Tests/", StringComparison.OrdinalIgnoreCase);

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
