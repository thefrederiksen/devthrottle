using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// Bounded apply-attempts for staged updates (issue #242). A staged update whose swap
/// never completes must not make the app relaunch-and-exit forever; after
/// <see cref="UpdateInstaller.MaxApplyAttempts"/> failures we give up and boot the
/// current build. These cover the pure decision; the launch/exit side effects are in
/// Program.cs.
/// </summary>
public class UpdateInstallerApplyBoundTests
{
    [Fact]
    public void HasExhausted_AtMaxForSameVersion_True()
    {
        var state = new UpdaterState
        {
            StagedVersion = "0.6.11",
            ApplyAttemptVersion = "0.6.11",
            ApplyAttempts = 2,
        };
        Assert.True(UpdateInstaller.HasExhaustedApplyAttempts(state, maxAttempts: 2));
    }

    [Fact]
    public void HasExhausted_BelowMax_False()
    {
        var state = new UpdaterState
        {
            StagedVersion = "0.6.11",
            ApplyAttemptVersion = "0.6.11",
            ApplyAttempts = 1,
        };
        Assert.False(UpdateInstaller.HasExhaustedApplyAttempts(state, maxAttempts: 2));
    }

    [Fact]
    public void HasExhausted_AttemptsCountedForDifferentVersion_False()
    {
        // The counter belongs to a previously-staged version; a freshly-staged version
        // starts fresh (must not inherit the old version's exhausted count).
        var state = new UpdaterState
        {
            StagedVersion = "0.6.12",
            ApplyAttemptVersion = "0.6.11",
            ApplyAttempts = 5,
        };
        Assert.False(UpdateInstaller.HasExhaustedApplyAttempts(state, maxAttempts: 2));
    }

    [Fact]
    public void HasExhausted_NoAttemptsRecorded_False()
    {
        var state = new UpdaterState { StagedVersion = "0.6.11" };
        Assert.False(UpdateInstaller.HasExhaustedApplyAttempts(state, maxAttempts: 2));
    }

    [Fact]
    public void UpdaterState_RoundTripsApplyAttemptFields()
    {
        var original = new UpdaterState
        {
            StagedVersion = "0.6.11",
            ApplyAttempts = 2,
            ApplyAttemptVersion = "0.6.11",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<UpdaterState>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.ApplyAttempts);
        Assert.Equal("0.6.11", restored.ApplyAttemptVersion);
    }
}
