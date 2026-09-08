using CcDirector.Core.Instances;
using CcDirector.Gateway.Contracts;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Issue #2730's other half, and the join of two branches - asserted on the first tree carrying both the
/// guard (#2721) and the locator fix (#2730), which is the tree Phase 6 of #2719 is built on.
///
/// Two facts, each with its control, so a pass here cannot come from a guard that refuses everything:
///
///   * a LONE corrupt registration - no live process at all - is refused by onlyIfEmpty, while the same
///     instance home with the file removed is permitted as a start;
///   * the launcher DECLARES the only-if-empty condition it now honours, so the Gateway's capability
///     answer can say "Available" for it. The dispatch was built on one branch and the declaration list
///     on another, and on the join the list was still empty: a launcher that honours the guard and does
///     not declare it makes every guarded restart refuse to be sent.
/// </summary>
public sealed class OnlyIfEmptyAndTheUnreadableRegistrationTests
{
    [Fact]
    public async Task A_lone_corrupt_registration_is_refused_by_onlyIfEmpty_and_permitted_once_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-2730-lone-" + Guid.NewGuid().ToString("N"));
        var instanceHome = Path.Combine(root, "instances", "default");
        var registrations = Path.Combine(instanceHome, "config", "director", "instances");
        Directory.CreateDirectory(registrations);
        try
        {
            var corrupt = Path.Combine(registrations, "aaaa0001-0000-0000-0000-000000000001.json");
            File.WriteAllText(corrupt, "{\"DirectorId\": \"aaaa0001-0000-0000-0000-000000000001\", \"Pid\": 4");

            var locator = new DirectorInstanceLocator(instanceHome);
            Assert.Equal(DirectorResolution.Unknown, locator.Resolve().Outcome);
            var supervisor = new DirectorSupervisor(new InstallLayout(root), locator);

            // Refused, with the count unknown - never "0 sessions, go ahead".
            var refused = await supervisor.RestartAsync(onlyIfEmpty: true);
            Assert.Equal(DirectorRestartVerdict.Refused, refused.Verdict);
            Assert.Null(refused.Sessions);
            Assert.Contains("Unknown", refused.Reason);

            // THE CONTROL. Remove the file and the same home is genuinely empty: NotRunning, and the guard
            // permits the restart as a start - which on this rig reaches the uninstalled Director and
            // throws naming its path. Without this half, a guard refusing everything would pass above.
            File.Delete(corrupt);
            Assert.Equal(DirectorResolution.NotRunning, locator.Resolve().Outcome);
            var launch = await Assert.ThrowsAsync<FileNotFoundException>(() => supervisor.RestartAsync(onlyIfEmpty: true));
            Assert.Contains(supervisor.DirectorExePath, launch.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_launcher_declares_the_only_if_empty_condition_it_honours()
    {
        Assert.Contains(LauncherCapabilities.DirectorRestartOnlyIfEmpty, LauncherDeclaredCapabilities.Conditions);
        Assert.Contains(LauncherCapabilities.DirectorRestartOnlyIfEmpty, LauncherDeclaredCapabilities.Describe().Commands);

        // A condition is a promise about a verb, not a verb: it must never be dispatchable on its own.
        Assert.False(LauncherDeclaredCapabilities.Honours(LauncherCapabilities.DirectorRestartOnlyIfEmpty));
        Assert.True(LauncherDeclaredCapabilities.Honours(LauncherCapabilities.DirectorRestart));
    }
}
