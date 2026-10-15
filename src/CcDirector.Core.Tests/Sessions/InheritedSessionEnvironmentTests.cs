using CcDirector.Core.ConPty;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnixPty;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A SESSION NEVER INHERITS THE VARIABLES OF THE SESSION ITS DIRECTOR WAS STARTED FROM.
///
/// On 8 October 2026 FactoryMemoryAtLaunchTests failed four times with the agent of a session in NO factory
/// reporting a real factory's notes folder from this machine. The gate had been run from inside a factory
/// session, the Director (the test host) inherited that session's <c>CC_FACTORY_MEMORY_DIR</c>, and every
/// backend passes the Director's whole environment to the agent. The SessionManager was right not to SET the
/// variable; the defect was that nothing REMOVED the inherited one. The rule now lives once, in
/// <see cref="InheritedSessionEnvironment"/>, and both process hosts build on it.
///
/// These set a process-wide variable, so they sit in the serial collection with the other tests that do.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class InheritedSessionEnvironmentTests : IDisposable
{
    private const string LeakedFolder = @"C:\some-other-session\factory-memory\7f0c";
    private readonly string? _before = Environment.GetEnvironmentVariable(FactoryMemoryFiles.DirectoryEnvVar);

    public InheritedSessionEnvironmentTests() =>
        Environment.SetEnvironmentVariable(FactoryMemoryFiles.DirectoryEnvVar, LeakedFolder);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(FactoryMemoryFiles.DirectoryEnvVar, _before);

    [Theory]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CODEX_THREAD_ID")]
    [InlineData("GIT_EDITOR")]
    [InlineData("CC_FACTORY_MEMORY_DIR")]
    [InlineData("cc_factory_memory_dir")]
    [InlineData("CC_FACTORY_KIT_DIR")]
    [InlineData("cc_factory_kit_dir")]
    [InlineData("CC_LICENSE_ALLOW_REAL_DEFAULT")]
    public void The_parent_agents_markers_and_the_parent_sessions_factory_are_stripped(string name) =>
        Assert.True(InheritedSessionEnvironment.IsStripped(name));

    [Theory]
    [InlineData("CODEX_HOME")]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("CC_DIRECTOR_ROOT")]
    public void Configuration_and_the_rest_of_the_environment_are_inherited(string name) =>
        Assert.False(InheritedSessionEnvironment.IsStripped(name));

    [Fact]
    public void Inherited_omits_the_stripped_variables_and_keeps_the_rest()
    {
        var inherited = InheritedSessionEnvironment.Inherited(StringComparer.OrdinalIgnoreCase);

        Assert.False(inherited.ContainsKey(FactoryMemoryFiles.DirectoryEnvVar),
            "a session would inherit the factory folder of the session that started its Director");
        Assert.True(inherited.ContainsKey("PATH") || inherited.ContainsKey("Path"), "PATH must reach the session");
    }

    // ---- the two hosts, which are where the environment is actually assembled. Building the set is pure
    // dictionary work on both, so both run on every platform. ----

    [Fact]
    public void Unix_host_A_session_in_no_factory_sees_no_factory_folder()
    {
        var env = Lines(UnixProcessHost.BuildEnvironment(null));

        Assert.DoesNotContain(env, e => e.StartsWith(FactoryMemoryFiles.DirectoryEnvVar + "=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unix_host_A_factory_session_sees_its_own_folder_not_the_inherited_one()
    {
        var env = Lines(UnixProcessHost.BuildEnvironment(new Dictionary<string, string>
        {
            [FactoryMemoryFiles.DirectoryEnvVar] = "/its/own/folder",
        }));

        Assert.Contains($"{FactoryMemoryFiles.DirectoryEnvVar}=/its/own/folder", env);
        Assert.DoesNotContain(env, e => e.Contains(LeakedFolder, StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_host_A_session_in_no_factory_sees_no_factory_folder()
    {
        var env = ProcessHost.BuildEnvironment(null);

        Assert.False(env.ContainsKey(FactoryMemoryFiles.DirectoryEnvVar),
            "a session would inherit the factory folder of the session that started its Director");
        Assert.True(env.ContainsKey("PATH"), "PATH must reach the session");
        Assert.Equal("xterm-256color", env["TERM"]);
    }

    [Fact]
    public void Windows_host_A_factory_session_sees_its_own_folder_not_the_inherited_one()
    {
        var env = ProcessHost.BuildEnvironment(new Dictionary<string, string>
        {
            [FactoryMemoryFiles.DirectoryEnvVar] = @"D:\its\own\folder",
        });

        Assert.Equal(@"D:\its\own\folder", env[FactoryMemoryFiles.DirectoryEnvVar]);
    }

    [Fact]
    public void Both_hosts_A_session_that_is_not_a_seat_never_inherits_a_parent_seats_kit_folder()
    {
        // Phase 5 of the factory definition work: a Director started from inside a seat carries that seat's
        // CC_FACTORY_KIT_DIR, and a session that is not a seat must not read the seat's brief, rules or files.
        var before = Environment.GetEnvironmentVariable(FactoryRunKit.DirectoryEnvVar);
        Environment.SetEnvironmentVariable(FactoryRunKit.DirectoryEnvVar, @"C:\some-seat\factory-run\a1e");
        try
        {
            Assert.False(InheritedSessionEnvironment.Inherited(StringComparer.OrdinalIgnoreCase).ContainsKey(FactoryRunKit.DirectoryEnvVar));
            Assert.False(ProcessHost.BuildEnvironment(null).ContainsKey(FactoryRunKit.DirectoryEnvVar));
            Assert.DoesNotContain(Lines(UnixProcessHost.BuildEnvironment(null)),
                e => e.StartsWith(FactoryRunKit.DirectoryEnvVar + "=", StringComparison.OrdinalIgnoreCase));
            // A seat is stamped its own folder after the strip, so it still gets one.
            Assert.Equal(@"D:\its\own\kit", ProcessHost.BuildEnvironment(new Dictionary<string, string>
            {
                [FactoryRunKit.DirectoryEnvVar] = @"D:\its\own\kit",
            })[FactoryRunKit.DirectoryEnvVar]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FactoryRunKit.DirectoryEnvVar, before);
        }
    }

    [Fact]
    public void Both_hosts_A_session_never_inherits_the_parent_sessions_identity_key_or_fleet_text()
    {
        // A Director started from inside a session carries that session's id, its Gateway key and address, its
        // Director's id and its fleet text. The SessionManager stamps each of them for the new session when the new
        // session should have it; when it does not (no Gateway, no Director id, the user's own injected text), an
        // inherited one would hand the new session the PARENT's identity and the parent's key to the fleet.
        string[] names = { "CC_GATEWAY_SESSION_KEY", "CC_SESSION_ID", "CC_GATEWAY_URL", "CC_DIRECTOR_ID", "CC_FLEET_TOOLS" };
        var before = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        foreach (var name in names)
            Environment.SetEnvironmentVariable(name, "parent-session-value");
        try
        {
            foreach (var name in names)
            {
                Assert.True(InheritedSessionEnvironment.IsStripped(name), name);
                Assert.True(InheritedSessionEnvironment.IsStripped(name.ToLowerInvariant()), name);
                Assert.False(ProcessHost.BuildEnvironment(null).ContainsKey(name), name);
                Assert.DoesNotContain(Lines(UnixProcessHost.BuildEnvironment(null)),
                    e => e.StartsWith(name + "=", StringComparison.Ordinal));
            }
            // The session's own stamped values go on after the strip, so a session still gets its own.
            var own = ProcessHost.BuildEnvironment(names.ToDictionary(n => n, n => "own-" + n));
            foreach (var name in names)
                Assert.Equal("own-" + name, own[name]);
        }
        finally
        {
            foreach (var (name, value) in before)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static List<string> Lines(string?[] env) => env.Where(e => e is not null).Select(e => e!).ToList();
}
