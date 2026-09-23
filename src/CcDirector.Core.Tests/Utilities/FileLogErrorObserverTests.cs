using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// Issue #3311: FileLog hands every ERROR line to the error reporter, and only error lines. This is the one
/// seam that makes "every error the Director logs reaches the Gateway" true without any call site having to
/// remember to report. It lives in this sequential assembly because it swaps FileLog's process-wide state.
/// </summary>
public sealed class FileLogErrorObserverTests
{
    [Fact]
    public void Write_AnErrorLine_ReachesTheObserver_AndAnOrdinaryLineDoesNot()
    {
        var seen = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = seen.Add;

            FileLog.Write("[SessionManager] CreateSession FAILED: access denied");
            FileLog.Write("[SessionManager] CreateSession: repo=x");
            FileLog.Write("[App] UNHANDLED UI-THREAD EXCEPTION: System.NullReferenceException");

            Assert.Equal(new[]
            {
                "[SessionManager] CreateSession FAILED: access denied",
                "[App] UNHANDLED UI-THREAD EXCEPTION: System.NullReferenceException",
            }, seen);
            // The line still reaches the file: the observer is a second reader, not a detour.
            Assert.Contains(scope.DrainAndReadLines(), l => l.EndsWith("[SessionManager] CreateSession FAILED: access denied"));
        }
        finally
        {
            FileLog.ErrorObserver = previous;
        }
    }
}
