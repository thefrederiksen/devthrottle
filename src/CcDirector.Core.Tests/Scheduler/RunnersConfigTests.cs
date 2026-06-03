using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

public sealed class RunnersConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly string _repoRoot;
    private readonly string _scriptPath;
    private readonly List<string> _log = new();

    public RunnersConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RunnersConfigTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "runners.json");

        // Mock repo layout: _tempDir/repo/scripts/some-runner.py
        _repoRoot = Path.Combine(_tempDir, "repo");
        var scriptDir = Path.Combine(_repoRoot, "scripts");
        Directory.CreateDirectory(scriptDir);
        _scriptPath = Path.Combine(scriptDir, "some-runner.py");
        File.WriteAllText(_scriptPath, "# placeholder\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LoadOrSeed_MissingFile_WritesEmptyDefaultSeed()
    {
        Assert.False(File.Exists(_configPath));

        // New installs seed an empty runners list (issue #164): a seeded runner would
        // point at a script that does not exist after install. The file must still be
        // written and parse as a valid, empty runners config.
        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        Assert.True(File.Exists(_configPath));
        Assert.Empty(runners);
        var json = File.ReadAllText(_configPath);
        Assert.DoesNotContain("linkedin-connect", json);
    }

    [Fact]
    public void LoadOrSeed_DisabledEntry_IsSkipped()
    {
        File.WriteAllText(_configPath, """
            {
              "runners": [
                {
                  "name": "should-skip",
                  "queueFilter": "status='approved'",
                  "command": "python",
                  "args": [],
                  "schedule": { "kind": "everyMinutes", "minutes": 10 },
                  "enabled": false
                }
              ]
            }
            """);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        Assert.Empty(runners);
        Assert.Contains(_log, l => l.Contains("Skipping disabled runner"));
    }

    [Fact]
    public void LoadOrSeed_ValidEntry_BuildsRegistration()
    {
        File.WriteAllText(_configPath, """
            {
              "runners": [
                {
                  "name": "drain-everything",
                  "queueFilter": "status='approved' AND platform='reddit'",
                  "command": "python",
                  "args": ["scripts/some-runner.py", "--all"],
                  "schedule": { "kind": "everyMinutes", "minutes": 10 },
                  "respectHumanCadence": false,
                  "minIntervalBetweenFiresMinutes": 5,
                  "enabled": true
                }
              ]
            }
            """);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        var r = Assert.Single(runners);
        Assert.Equal("drain-everything", r.Name);
        Assert.Equal("python", r.Command);
        Assert.Equal(2, r.Args.Length);
        Assert.Equal(_scriptPath, r.Args[0]); // Resolved relative -> absolute
        Assert.Equal("--all", r.Args[1]);     // Non-path arg unchanged
        Assert.Equal(TimeSpan.FromMinutes(5), r.MinIntervalBetweenFires);
        Assert.False(r.RespectHumanCadence);
    }

    [Fact]
    public void LoadOrSeed_AbsoluteArg_LeftUnchanged()
    {
        var absoluteScript = _scriptPath; // already absolute
        var json = $$"""
            {
              "runners": [
                {
                  "name": "abs",
                  "queueFilter": "1=1",
                  "command": "python",
                  "args": ["{{absoluteScript.Replace("\\", "\\\\")}}"],
                  "schedule": { "kind": "daily", "timeOfDay": "08:00" }
                }
              ]
            }
            """;
        File.WriteAllText(_configPath, json);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        var r = Assert.Single(runners);
        Assert.Equal(absoluteScript, r.Args[0]);
    }

    [Fact]
    public void LoadOrSeed_UnresolvableRelativeScript_SkipsRunnerWithLog()
    {
        File.WriteAllText(_configPath, """
            {
              "runners": [
                {
                  "name": "missing-script",
                  "queueFilter": "1=1",
                  "command": "python",
                  "args": ["scripts/does-not-exist.py"],
                  "schedule": { "kind": "everyMinutes", "minutes": 10 }
                }
              ]
            }
            """);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        Assert.Empty(runners);
        Assert.Contains(_log, l => l.Contains("does-not-exist.py"));
    }

    [Fact]
    public void LoadOrSeed_InvalidScheduleKind_SkipsRunner()
    {
        File.WriteAllText(_configPath, """
            {
              "runners": [
                {
                  "name": "bad",
                  "queueFilter": "1=1",
                  "command": "echo",
                  "args": [],
                  "schedule": { "kind": "annually", "timeOfDay": "08:00" }
                }
              ]
            }
            """);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        Assert.Empty(runners);
        Assert.Contains(_log, l => l.Contains("invalid schedule"));
    }

    [Fact]
    public void LoadOrSeed_MalformedJson_ReturnsEmptyAndLogs()
    {
        File.WriteAllText(_configPath, "{ this isn't json");

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        Assert.Empty(runners);
        Assert.Contains(_log, l => l.Contains("Failed to parse"));
    }

    [Fact]
    public void LoadOrSeed_EveryMinutesSchedule_BuildsCorrectly()
    {
        File.WriteAllText(_configPath, """
            {
              "runners": [
                {
                  "name": "every-10m",
                  "queueFilter": "1=1",
                  "command": "echo",
                  "args": [],
                  "schedule": { "kind": "everyMinutes", "minutes": 10 }
                }
              ]
            }
            """);

        var runners = RunnersConfig.LoadOrSeed(_configPath, _repoRoot, _log.Add);

        var r = Assert.Single(runners);
        // Verify the schedule fires only when interval has elapsed.
        var now = new DateTime(2026, 5, 22, 12, 0, 0);
        Assert.True(r.Schedule.ShouldFire(now.AddMinutes(-11), now));
        Assert.False(r.Schedule.ShouldFire(now.AddMinutes(-5), now));
    }
}
