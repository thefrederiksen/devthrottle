using System.Globalization;
using Xunit;
using CcDirector.Core.Tests;   // WindowsOnlyFact, linked into this project

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the per-user-per-machine suite lock is actually HELD while this assembly's tests run.
///
/// This exists for the same reason <see cref="TestStorageRootRedirectTests"/> does: a module initializer
/// that silently does not run leaves no trace at all. The suite would go straight back to letting two runs
/// overlap and corrupt each other, and every test here would still pass. A guard whose failure is invisible
/// is not coverage, so the guard gets a guard.
///
/// NOTHING HERE MUTATES PROCESS-GLOBAL STATE. An earlier draft proved the environment-immunity property by
/// redirecting TEMP, TMP and TMPDIR and then recursively deleting the directory it had redirected them to.
/// Hundreds of tests in this assembly resolve paths through <see cref="Path.GetTempPath"/>, so that draft
/// could have deleted a tree another test had been redirected into - introducing cross-class nondeterminism
/// inside the fix for cross-process nondeterminism. Serializing those tests into a non-parallel collection
/// would have hidden the hazard behind a convention someone must remember on every future test that touches
/// those variables. Instead the derivation takes its environment as an ARGUMENT, so a hostile environment is
/// passed in and there is no global to mutate, nothing to remember, and nothing to delete.
/// </summary>
public sealed class GatewayTestSuiteLockTests
{
    /// <summary>The real ambient values, with every temporary-directory variable replaced by a hostile one.
    /// A run launched this way must still land on exactly the same lock.</summary>
    private static GatewayTestSuiteLock.AmbientEnvironment WithHostileTemporaryDirectories(string hostile)
    {
        var real = GatewayTestSuiteLock.ReadAmbient();
        return real with
        {
            Temp = hostile,
            Tmp = hostile,
            TmpDir = hostile,
            LocalAppDataVariable = hostile,
        };
    }

    [Fact]
    public void TheLockIsHeldWhileTestsRun()
    {
        if (GatewayTestSuiteLock.QualificationBypassActive)
        {
            // A qualification run (issue #1156 step 4) SUSPENDS the serialization property on purpose -
            // that is the experiment. The honest assertion here is that the suspension was explicit and
            // recorded, never that the lock is somehow both bypassed and held.
            Assert.False(
                GatewayTestSuiteLock.IsHeld,
                "The qualification bypass is active yet the lock is also held - the bypass and the "
                + "acquisition path both ran, which means Acquire's ruling is broken.");
            return;
        }

        Assert.True(
            GatewayTestSuiteLock.IsHeld,
            "This run does not hold the per-user Gateway test lock, so nothing is stopping a second "
            + "run of this suite from executing alongside it and corrupting both. See GatewayTestSuiteLock.");
    }

    [WindowsOnlyFact("the suite lock is an exclusive FILE OPEN, and Unix does not enforce share modes - a second open of the same file simply succeeds, so there is no refusal here to assert. The lock therefore does NOT serialise concurrent Gateway runs off Windows, which is recorded as its own defect rather than hidden by this skip")]
    public void ASecondExclusiveOpenIsRefused_WhichIsWhatBlocksAConcurrentRun()
    {
        if (GatewayTestSuiteLock.QualificationBypassActive)
        {
            // Under the qualification bypass this process holds no handle, so the exclusivity this test
            // proves has no subject - and probing the real lock file here would race whatever ordinary
            // run may legitimately hold it on this machine during a soak. Nothing to prove in this mode;
            // the bypass itself is asserted by TheLockIsHeldWhileTestsRun.
            return;
        }

        // The exact open a second run would attempt. Share modes are per-handle, not per-process, so this
        // is refused inside the holding process too - which makes it a real proof of exclusivity rather
        // than a restatement of a flag we set ourselves.
        var refused = Assert.ThrowsAny<IOException>(() =>
        {
            using var _ = new FileStream(
                GatewayTestSuiteLock.LockFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        });

        Assert.NotNull(refused);
    }

    /// <summary>
    /// The regression test for a DEMONSTRATED defect, not a hypothesis. The lock path was originally derived
    /// from <see cref="Path.GetTempPath"/>, which reads TEMP and TMP from the process environment. Two runs
    /// launched with different TEMP values computed two different lock files, so neither could see the
    /// other: both reported acquiring the lock in the same second and both ran concurrently. The whole
    /// mechanism was inert one environment variable away from its intended use.
    ///
    /// This asserts the property that closes it - the lock's identity does not move when the environment
    /// moves - on whatever platform the tests run on, including platforms the author could not reach.
    /// </summary>
    [Fact]
    public void TheLockPathDoesNotMoveWhenTheTemporaryDirectoryEnvironmentMoves()
    {
        var hostile = WithHostileTemporaryDirectories("D:/somewhere-a-different-run-was-launched-from");

        Assert.Equal(
            GatewayTestSuiteLock.LockFilePath,
            GatewayTestSuiteLock.ComputeLockFilePath(hostile));
    }

    /// <summary>
    /// The end-to-end half of the same defect, against the REAL lock this run is really holding.
    ///
    /// A second run launched with a different TEMP would compute its lock path, then open it. This does
    /// exactly that - recomputes the path under a changed environment, then attempts the same exclusive
    /// open a second run would attempt - and requires it to be REFUSED. Under the defect the recomputed
    /// path was a different file and the open succeeded, which is precisely how two suites ran side by
    /// side.
    /// </summary>
    [WindowsOnlyFact("the suite lock is an exclusive FILE OPEN, and Unix does not enforce share modes - a second open of the same file simply succeeds, so there is no refusal here to assert. The lock therefore does NOT serialise concurrent Gateway runs off Windows, which is recorded as its own defect rather than hidden by this skip")]
    public void ARunWithADifferentTemporaryDirectoryStillCollidesWithThisHeldLock()
    {
        var asAnotherRunWouldComputeIt = GatewayTestSuiteLock.ComputeLockFilePath(
            WithHostileTemporaryDirectories("D:/another-runs-temp-" + Guid.NewGuid().ToString("N")));

        // Same file this run holds - that is the property being pinned.
        Assert.Equal(GatewayTestSuiteLock.LockFilePath, asAnotherRunWouldComputeIt);

        if (GatewayTestSuiteLock.QualificationBypassActive)
        {
            // The path-identity half above still holds and was asserted. The refused-open half needs
            // "this held lock" as its subject, and a qualification run (issue #1156 step 4) holds
            // nothing - whether the open is refused depends on whichever ordinary run happens to hold
            // the real file right now, which is not this test's property to pin.
            return;
        }

        // And therefore the open that run would attempt is refused by the operating system. Under the
        // defect the recomputed path was a DIFFERENT file, this open succeeded, and two suites ran side
        // by side.
        Assert.ThrowsAny<IOException>(() =>
        {
            using var _ = new FileStream(
                asAnotherRunWouldComputeIt, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        });
    }

    /// <summary>
    /// The invariant is IMMUNITY TO THE ENVIRONMENT, not a particular directory - and getting that wrong is
    /// how an earlier version of this test would have failed DETERMINISTICALLY on Linux, where the lock
    /// legitimately lives under /tmp and the assertion said it must not. Continuous integration runs
    /// ubuntu-latest, so that branch genuinely executes; the test written to protect a branch its author
    /// could not observe would have broken the platform it was written for.
    ///
    /// Both branches are exercised HERE, on whatever machine runs this, because the platform is an input
    /// rather than something read from the running process. A Windows developer's run now covers the Unix
    /// derivation, instead of leaving it to be discovered by a continuous-integration machine nobody is
    /// watching.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLockPathIsNotRelocatableByWhoeverLaunchesTheRun(bool isWindows)
    {
        const string hostile = "D:/relocated-by-the-launching-environment";
        var ambient = WithHostileTemporaryDirectories(hostile) with { IsWindows = isWindows };

        var path = GatewayTestSuiteLock.ComputeLockFilePath(ambient);

        Assert.DoesNotContain("relocated-by-the-launching-environment", path, StringComparison.Ordinal);

        if (isWindows)
        {
            // Anchored to the folder API's answer, which no environment variable moves.
            Assert.StartsWith(ambient.LocalApplicationDataFolder, path, StringComparison.Ordinal);
        }
        else
        {
            // A fixed, well-known location that TMPDIR does not move, with the per-user split in the file
            // name because /tmp is shared. Forward slashes because the separator belongs to the TARGET
            // platform - Path.Combine here would emit a backslash when this branch is evaluated on Windows.
            Assert.StartsWith("/tmp/cc-director-", path, StringComparison.Ordinal);
            Assert.Contains(ambient.UserName, path, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', path);
        }
    }

    /// <summary>
    /// The Windows branch refuses to invent a home rather than falling back to somewhere the environment
    /// controls. A fallback here would look like robustness and would silently restore the defect: every
    /// alternative location is environment-settable, so the run would serialize nothing while appearing to
    /// work.
    /// </summary>
    [Fact]
    public void WithNoLocalApplicationDataFolder_ItRefusesRatherThanFallingBack()
    {
        var ambient = GatewayTestSuiteLock.ReadAmbient() with
        {
            IsWindows = true,
            LocalApplicationDataFolder = "",
        };

        Assert.Throws<InvalidOperationException>(() => GatewayTestSuiteLock.ComputeLockFilePath(ambient));
    }

    [Fact]
    public void TheLockFileNamesThisProcess_SoABlockedRunCanSayWhoIsBlockingIt()
    {
        if (GatewayTestSuiteLock.QualificationBypassActive)
        {
            // A qualification run (issue #1156 step 4) never writes the lock file, so "names THIS
            // process" has no subject: the file holds whichever ordinary run last held the real lock.
            // The diagnostics-writing property is still proven by every ordinary run of this suite.
            return;
        }

        // Read exactly the way a blocked run reads it: read-only, sharing everything, while the holder
        // still has the file open for writing.
        using var stream = new FileStream(
            GatewayTestSuiteLock.LockFilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        Assert.Contains(
            "processId=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.Contains("acquiredUtc=", text, StringComparison.Ordinal);
        Assert.Contains("session=", text, StringComparison.Ordinal);
    }

    // ---- The qualification ruling (issue #1156 step 4) ----
    // Pure-value tests, the same way the lock path is tested: every branch of the ruling is provable
    // without touching process environment, so nothing here can be perturbed by a real soak running
    // on the machine at the same time.

    private static (string Name, string? Value)[] NoLiveProof() => new (string, string?)[]
    {
        ("CC_GATEWAY_TEST_PG_CONNECTION", null),
        ("CC_GATEWAY_TEST_PG_STATS_CONNECTION", null),
        ("CC_GATEWAY_DB_CONNECTION", null),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Qualification_AbsentOrBlank_TakesTheLockAsAlways(string? value)
    {
        Assert.Equal(
            GatewayTestSuiteLock.QualificationRuling.NotRequested,
            GatewayTestSuiteLock.RuleOnQualification(value, NoLiveProof()));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("isolated-worktree-soak ")] // trailing space: close is not exact
    [InlineData("ISOLATED-WORKTREE-SOAK")]
    public void Qualification_WrongToken_RefusesRatherThanSilentlySerializing(string value)
    {
        // The dangerous fallback would be taking the locked path: a soak whose runs never actually
        // overlapped produces thousands of clean host starts that certify nothing.
        Assert.Equal(
            GatewayTestSuiteLock.QualificationRuling.RefuseWrongToken,
            GatewayTestSuiteLock.RuleOnQualification(value, NoLiveProof()));
    }

    [Fact]
    public void Qualification_ExactTokenAndNoLiveProof_Bypasses()
    {
        Assert.Equal(
            GatewayTestSuiteLock.QualificationRuling.Bypass,
            GatewayTestSuiteLock.RuleOnQualification(GatewayTestSuiteLock.QualificationToken, NoLiveProof()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Qualification_AnyLiveProofVariable_FailsClosed(int position)
    {
        var vars = NoLiveProof();
        vars[position] = (vars[position].Name, "Host=localhost;Database=ccpgsomething");

        Assert.Equal(
            GatewayTestSuiteLock.QualificationRuling.RefuseLiveProof,
            GatewayTestSuiteLock.RuleOnQualification(GatewayTestSuiteLock.QualificationToken, vars));
    }

    [Fact]
    public void Qualification_WhitespaceLiveProofValue_CountsAsUnset()
    {
        // The skip logic throughout the proof classes treats whitespace as unset; the bypass ruling must
        // agree, or a stray space would refuse a soak the proofs themselves would have skipped cleanly.
        var vars = NoLiveProof();
        vars[1] = (vars[1].Name, "   ");

        Assert.Equal(
            GatewayTestSuiteLock.QualificationRuling.Bypass,
            GatewayTestSuiteLock.RuleOnQualification(GatewayTestSuiteLock.QualificationToken, vars));
    }

    [Fact]
    public void Qualification_TheLiveProofList_NamesTheVariablesTheProofClassesActuallyRead()
    {
        // The fail-closed list is only as good as its names. Two of the three are declared by the classes
        // that read them; pin all three so a rename there breaks here instead of silently un-guarding.
        Assert.Contains("CC_GATEWAY_TEST_PG_CONNECTION", GatewayTestSuiteLock.LiveProofEnvVars);
        Assert.Contains("CC_GATEWAY_TEST_PG_STATS_CONNECTION", GatewayTestSuiteLock.LiveProofEnvVars);
        Assert.Contains(
            CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar,
            GatewayTestSuiteLock.LiveProofEnvVars);
    }
}
