using CcDirector.Core.ApiMode;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

/// <summary>The real process's start-up decisions, made without starting anything.</summary>
public sealed class SystemApiModeProcessTests
{
    [Fact]
    public void Start_ExecutablePathThatDoesNotExist_ThrowsNamingTheExecutableWithoutStartingAnything()
    {
        // Arrange: a path that cannot exist, in a folder that does, so the failure is the executable's.
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-agent-{Guid.NewGuid():N}", "pi");
        using var process = new SystemApiModeProcess();

        // Act
        var ex = Assert.Throws<ApiModeStartException>(() => process.Start(new ApiModeProcessStartInfo
        {
            Executable = missing,
            WorkingDirectory = Path.GetTempPath(),
        }));

        // Assert
        Assert.Equal(missing, ex.Executable);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("does not exist", ex.Message);
        Assert.Equal(0, process.ProcessId);
    }

    [Fact]
    public void Start_WorkingFolderThatDoesNotExist_ThrowsNamingTheFolder()
    {
        // Arrange
        var folder = Path.Combine(Path.GetTempPath(), $"no-such-folder-{Guid.NewGuid():N}");
        using var process = new SystemApiModeProcess();

        // Act
        var ex = Assert.Throws<ApiModeStartException>(() => process.Start(new ApiModeProcessStartInfo
        {
            Executable = "pi",
            WorkingDirectory = folder,
        }));

        // Assert
        Assert.Contains(folder, ex.Message);
        Assert.Contains("'pi'", ex.Message);
    }
}

public sealed class ApiModeExecutableResolverTests
{
    [Fact]
    public void Resolve_BareNameNotOnTheSearchPath_ThrowsNamingTheExecutable()
    {
        // Act
        var ex = Assert.Throws<ApiModeStartException>(() =>
            ApiModeExecutableResolver.Resolve("pi", "/usr/bin:/opt/pi/bin", null, isWindows: false, fileExists: _ => false));

        // Assert
        Assert.Equal("pi", ex.Executable);
        Assert.Equal("Cannot start 'pi': it was not found on the search path (PATH).", ex.Message);
    }

    [Fact]
    public void Resolve_BareNameOnTheSearchPath_ReturnsTheFirstFolderThatHasIt()
    {
        // Arrange
        var present = new HashSet<string> { Path.Combine("/opt/pi/bin", "pi"), Path.Combine("/usr/local/bin", "pi") };

        // Act
        var resolved = ApiModeExecutableResolver.Resolve("pi", "/usr/bin:/opt/pi/bin:/usr/local/bin", null, isWindows: false, present.Contains);

        // Assert
        Assert.Equal(Path.Combine("/opt/pi/bin", "pi"), resolved);
    }

    [Fact]
    public void Resolve_OnWindowsBareNameBesideAnExtensionlessScript_ReturnsTheCmdFile()
    {
        // Arrange: npm installs both 'pi' (a shell script Windows cannot run) and 'pi.cmd'.
        var folder = @"C:\Users\fixture\AppData\Roaming\npm";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(folder, "pi"), Path.Combine(folder, "pi") + ".CMD" };

        // Act
        var resolved = ApiModeExecutableResolver.Resolve("pi", folder, ".COM;.EXE;.BAT;.CMD", isWindows: true, present.Contains);

        // Assert
        Assert.Equal(Path.Combine(folder, "pi") + ".CMD", resolved);
    }

    [Fact]
    public void Resolve_PathWithAFolder_IsNotLookedUpOnTheSearchPath()
    {
        // Arrange
        var looked = new List<string>();

        // Act
        Assert.Throws<ApiModeStartException>(() => ApiModeExecutableResolver.Resolve("/opt/agents/pi", "/usr/bin", null, isWindows: false, candidate =>
        {
            looked.Add(candidate);
            return false;
        }));

        // Assert
        Assert.Equal(["/opt/agents/pi"], looked);
    }

    [Fact]
    public void Resolve_EmptyExecutable_ThrowsSayingNoneWasGiven()
    {
        // Act
        var ex = Assert.Throws<ApiModeStartException>(() => ApiModeExecutableResolver.Resolve(" ", "/usr/bin", null, isWindows: false, _ => true));

        // Assert
        Assert.Contains("no executable was given", ex.Message);
    }
}

public sealed class ApiModeEnvironmentTests
{
    [Fact]
    public void Build_PrefixAndNamesRemovedThenSet_TheAgentSeesExactlyThat()
    {
        // Arrange
        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/fixture",
            ["CC_SESSION_ID"] = "parent",
            ["CC_GATEWAY_URL"] = "https://gateway.invalid",
            ["NODE_OPTIONS"] = "--inspect",
        };
        var startInfo = new ApiModeProcessStartInfo
        {
            Executable = "pi",
            WorkingDirectory = "/work",
            RemoveVariablePrefixes = ["CC_"],
            RemoveVariables = ["NODE_OPTIONS"],
            SetVariables = new Dictionary<string, string> { ["PI_OFFLINE"] = "1" },
        };

        // Act
        var environment = ApiModeEnvironment.Build(inherited, startInfo, StringComparer.Ordinal);

        // Assert
        Assert.Equal(["HOME", "PATH", "PI_OFFLINE"], environment.Keys.Order());
        Assert.Equal("1", environment["PI_OFFLINE"]);
    }

    [Fact]
    public void Build_SetVariableWithARemovedPrefix_IsStillSet()
    {
        // Arrange: a removal clears what was inherited; what the caller sets on purpose is applied after it.
        var inherited = new Dictionary<string, string> { ["CC_SESSION_ID"] = "parent" };
        var startInfo = new ApiModeProcessStartInfo
        {
            Executable = "pi",
            WorkingDirectory = "/work",
            RemoveVariablePrefixes = ["CC_"],
            SetVariables = new Dictionary<string, string> { ["CC_SESSION_ID"] = "this-session" },
        };

        // Act
        var environment = ApiModeEnvironment.Build(inherited, startInfo, StringComparer.Ordinal);

        // Assert
        Assert.Equal("this-session", Assert.Single(environment).Value);
    }

    [Fact]
    public void Build_OnWindowsComparer_RemovesWhateverTheCase()
    {
        // Arrange
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cc_session_id"] = "parent", ["Path"] = @"C:\Windows" };
        var startInfo = new ApiModeProcessStartInfo { Executable = "pi", WorkingDirectory = @"C:\work", RemoveVariablePrefixes = ["CC_"] };

        // Act
        var environment = ApiModeEnvironment.Build(inherited, startInfo, StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.Equal("Path", Assert.Single(environment).Key);
    }
}
