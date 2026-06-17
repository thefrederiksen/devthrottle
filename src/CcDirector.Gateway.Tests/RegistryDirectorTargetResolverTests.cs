using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="RegistryDirectorTargetResolver"/> (epic #479, #503): resolving a cron
/// job's target MACHINE to a runnable Director. Covers the three paths - a Director is already
/// running (no launch), none is running so the launcher starts one and it registers (launch + poll),
/// and the launcher cannot start one (clean error). A fake launcher and a mutable Director list stand
/// in for the live registry, with tiny wall-clock waits so the poll terminates instantly.
/// </summary>
public sealed class RegistryDirectorTargetResolverTests
{
    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private static DirectorDto Director(string id, string machine, string endpoint) => new()
    {
        DirectorId = id, MachineName = machine, ControlEndpoint = endpoint, Version = "0.9.10",
    };

    private sealed class FakeLauncher : IDirectorLauncher
    {
        private readonly Func<string, bool> _onStart;
        public int StartCount { get; private set; }
        public string? LastMachine { get; private set; }

        public FakeLauncher(Func<string, bool> onStart) => _onStart = onStart;

        public Task<bool> StartAsync(string machine, CancellationToken ct)
        {
            StartCount++;
            LastMachine = machine;
            return Task.FromResult(_onStart(machine));
        }
    }

    [Fact]
    public async Task Resolve_DirectorAlreadyRunning_ReturnsItsEndpoint_DoesNotLaunch()
    {
        var directors = new List<DirectorDto>
        {
            Director("d-7882", "SOREN_NORTH", "http://127.0.0.1:7882/"),
            Director("d-7879", "SOREN", "http://127.0.0.1:7879"),
        };
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch when one is running"));
        var resolver = new RegistryDirectorTargetResolver(() => directors, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("http://127.0.0.1:7882", result.Endpoint);   // trailing slash trimmed
        Assert.Equal("d-7882", result.DirectorId);
        Assert.Equal(0, launcher.StartCount);                      // a running Director means no launch
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherStartsOne_RegistersAfterLaunch_Resolves()
    {
        var directors = new List<DirectorDto> { Director("d-7879", "SOREN", "http://127.0.0.1:7879") };
        // The launcher "starts" a Director on SOREN_NORTH: it registers in the list and the next poll finds it.
        var launcher = new FakeLauncher(machine =>
        {
            directors.Add(Director("d-new", machine, "http://127.0.0.1:7900"));
            return true;
        });
        var resolver = new RegistryDirectorTargetResolver(() => directors, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("http://127.0.0.1:7900", result.Endpoint);
        Assert.Equal("d-new", result.DirectorId);
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal("SOREN_NORTH", launcher.LastMachine);
    }

    [Fact]
    public async Task Resolve_NoDirector_LauncherCannotStart_ReturnsError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => false);              // launcher fails to start one
        var resolver = new RegistryDirectorTargetResolver(() => directors, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", CancellationToken.None);

        Assert.Null(result.Endpoint);
        Assert.Null(result.DirectorId);
        Assert.NotNull(result.Error);
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_LaunchedButNeverRegisters_ReturnsTimeoutError()
    {
        var directors = new List<DirectorDto>();
        var launcher = new FakeLauncher(_ => true);               // claims success but nothing registers
        var resolver = new RegistryDirectorTargetResolver(
            () => directors, launcher, TimeSpan.FromMilliseconds(120), FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", CancellationToken.None);

        Assert.Null(result.Endpoint);
        Assert.NotNull(result.Error);
        Assert.Contains("registered", result.Error!);
    }

    [Fact]
    public async Task Resolve_EmptyMachine_ReturnsError_DoesNotLaunch()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("must not launch for empty machine"));
        var resolver = new RegistryDirectorTargetResolver(() => new List<DirectorDto>(), launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("   ", CancellationToken.None);

        Assert.Null(result.Endpoint);
        Assert.NotNull(result.Error);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task Resolve_OnlyUnreachableDirectorOnMachine_LaunchesAReplacement()
    {
        var directors = new List<DirectorDto>
        {
            new()
            {
                DirectorId = "d-dead", MachineName = "SOREN_NORTH", ControlEndpoint = "http://127.0.0.1:7882",
                Version = "0.9.10", AdvertisedEndpointState = DirectorDto.EndpointStateUnreachableByName,
            },
        };
        var launcher = new FakeLauncher(machine =>
        {
            directors.Add(Director("d-fresh", machine, "http://127.0.0.1:7901"));
            return true;
        });
        var resolver = new RegistryDirectorTargetResolver(() => directors, launcher, FastTimeout, FastPoll);

        var result = await resolver.ResolveAsync("SOREN_NORTH", CancellationToken.None);

        // The unreachable Director is skipped, so the launcher is asked to start a fresh one.
        Assert.Equal(1, launcher.StartCount);
        Assert.Equal("d-fresh", result.DirectorId);
        Assert.Equal("http://127.0.0.1:7901", result.Endpoint);
    }
}
