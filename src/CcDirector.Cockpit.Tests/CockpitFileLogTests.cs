using CcDirector.Cockpit.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// The Cockpit file sink (issue #199): the Cockpit was the only product component with no persisted
/// logging, so a misbehaving web UI left no trace. These tests pin the three guarantees the issue
/// asks for: the dated cockpit-*.log file is created in the target directory, INFO lines land with
/// the FileLog-style timestamp + level + category, and an exception is written out with the message.
/// </summary>
public class CockpitFileLogTests : IDisposable
{
    private readonly string _dir;

    public CockpitFileLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cockpit-filelog-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup of a temp dir */ }
    }

    private string ReadOnlyLogFile()
    {
        var files = Directory.GetFiles(_dir, "cockpit-*.log");
        Assert.Single(files);
        // Open with shared read/write so the still-running writer's held handle does not block us.
        using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Information_WritesDatedCockpitFile_WithLevelAndCategory()
    {
        // Arrange
        using var provider = new CockpitFileLoggerProvider(_dir);
        var logger = provider.CreateLogger("CcDirector.Cockpit.Components.Pages.Cockpit");

        // Act
        logger.LogInformation("Cockpit action=select-session sid=abc dir=http://host:7887");
        provider.Dispose(); // flushes and stops the writer

        // Assert
        var fileName = Path.GetFileName(Directory.GetFiles(_dir, "cockpit-*.log").Single());
        Assert.Matches(@"^cockpit-\d{4}-\d{2}-\d{2}-\d+\.log$", fileName);

        var content = ReadOnlyLogFile();
        Assert.Contains("INFO ", content);
        Assert.Contains("[Cockpit]", content);                 // category shortened to last segment
        Assert.Contains("action=select-session sid=abc", content);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", content); // FileLog timestamp shape
    }

    [Fact]
    public void Warning_WithException_WritesLevelAndExceptionText()
    {
        // Arrange
        using var provider = new CockpitFileLoggerProvider(_dir);
        var logger = provider.CreateLogger("CcDirector.Cockpit.Components.TerminalPane");

        // Act
        logger.LogWarning(
            new InvalidOperationException("boom-xyz"),
            "TerminalPane empty DirectorBase for sid=abc (rowState=WaitingForInput)");
        provider.Dispose();

        // Assert
        var content = ReadOnlyLogFile();
        Assert.Contains("WARN ", content);
        Assert.Contains("[TerminalPane]", content);
        Assert.Contains("empty DirectorBase", content);
        Assert.Contains("boom-xyz", content); // exception rendered onto the line
    }

    [Fact]
    public void CurrentLogPath_PointsAtTheDatedFileInTheTargetDirectory()
    {
        // Arrange / Act
        using var provider = new CockpitFileLoggerProvider(_dir);

        // Assert
        var path = provider.CurrentLogPath;
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.Matches(@"^cockpit-\d{4}-\d{2}-\d{2}-\d+\.log$", Path.GetFileName(path));
    }

    [Fact]
    public void DefaultLogDirectory_HonorsCcDirectorRootOverride()
    {
        // Arrange
        var prev = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var fakeRoot = Path.Combine(Path.GetTempPath(), "cc-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", fakeRoot);
        try
        {
            // Act
            var dir = CockpitFileLoggerProvider.DefaultLogDirectory();

            // Assert
            Assert.Equal(Path.Combine(fakeRoot, "logs", "cockpit"), dir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", prev);
        }
    }
}
