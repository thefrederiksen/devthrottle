using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

public sealed class AuthEventLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public AuthEventLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "auth-events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ReadAll_NoLog_ReturnsEmpty()
    {
        var log = new AuthEventLog(_logPath);

        Assert.Empty(log.ReadAll());
    }

    [Fact]
    public void RecordLoggedInThenLoggedOut_ReadAllReturnsBothInOrder()
    {
        var log = new AuthEventLog(_logPath);

        log.RecordLoggedIn();
        log.RecordLoggedOut();

        var events = log.ReadAll();
        Assert.Equal(2, events.Count);
        Assert.Equal(AuthEventLog.LoggedIn, events[0].Kind);
        Assert.Equal(AuthEventLog.LoggedOut, events[1].Kind);
    }
}
