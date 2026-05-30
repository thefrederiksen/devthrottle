using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// DirectorIdStore must mint a GUID once and reuse it forever. This guarantee is
/// what lets the Gateway show the same Director row across restarts.
///
/// The tests redirect <see cref="DirectorIdStore"/> at a temp dir via the
/// <c>CC_DIRECTOR_ROOT</c> env var so they don't touch the real user's file.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorIdStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;

    public DirectorIdStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cc-director-test-" + Guid.NewGuid().ToString("N"));
        _previousEnv = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempRoot);

        // DirectorIdStore caches the path in a static initializer, so we can only
        // assert behavior in a process where this test runs first or where the path
        // it uses (relative to CC_DIRECTOR_ROOT) actually resolves to our temp dir.
        // The static initializer reads CC_DIRECTOR_ROOT at first access; subsequent
        // tests in the same process will reuse the same path. That's fine: we accept
        // whatever path the store decided on as long as it's consistent.
    }

    public void Dispose()
    {
        if (_previousEnv is null)
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null);
        else
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousEnv);

        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void LoadOrCreate_returns_valid_guid()
    {
        var id = DirectorIdStore.LoadOrCreate();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.True(Guid.TryParse(id, out _), $"expected a GUID, got \"{id}\"");
    }

    [Fact]
    public void LoadOrCreate_returns_same_id_on_repeated_calls()
    {
        var a = DirectorIdStore.LoadOrCreate();
        var b = DirectorIdStore.LoadOrCreate();
        var c = DirectorIdStore.LoadOrCreate();
        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void LoadOrCreate_persists_to_disk()
    {
        var id = DirectorIdStore.LoadOrCreate();
        Assert.True(File.Exists(DirectorIdStore.FilePath), $"expected file at {DirectorIdStore.FilePath}");
        var contents = File.ReadAllText(DirectorIdStore.FilePath).Trim();
        Assert.Equal(id, contents);
    }

    [Fact]
    public void LoadOrCreate_regenerates_when_file_is_malformed()
    {
        // Pre-seed with garbage.
        Directory.CreateDirectory(DirectorIdStore.DirectoryPath);
        File.WriteAllText(DirectorIdStore.FilePath, "not-a-guid");

        var id = DirectorIdStore.LoadOrCreate();
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, File.ReadAllText(DirectorIdStore.FilePath).Trim());
    }

    [Fact]
    public void LoadOrCreate_returns_different_ids_for_different_slots()
    {
        // Regression: pre-fix all Directors on a machine shared one global id file
        // and overwrote each other's instances/{id}.json, so the Gateway only ever
        // saw the most-recently-started Director. Per-exe-path slots are what
        // restores fan-out across concurrent Directors.
        var a = DirectorIdStore.LoadOrCreate(@"D:\builds\cc-director-avalonia1.exe");
        var b = DirectorIdStore.LoadOrCreate(@"D:\builds\cc-director-avalonia4.exe");
        Assert.NotEqual(a, b);

        // And same slot must still return the same id on repeat calls.
        var aAgain = DirectorIdStore.LoadOrCreate(@"D:\builds\cc-director-avalonia1.exe");
        Assert.Equal(a, aAgain);
    }

    [Fact]
    public void Slot_is_case_and_separator_insensitive_on_windows()
    {
        // "D:\Foo\bar.exe" and "d:/foo/BAR.EXE" should resolve to the same slot
        // so a Director that flips between forward/backslash or uppercased PATH
        // entries does not look like a brand-new Director to the Gateway.
        var a = DirectorIdStore.LoadOrCreate(@"D:\Foo\bar.exe");
        var b = DirectorIdStore.LoadOrCreate(@"d:/foo/BAR.EXE");
        Assert.Equal(a, b);
    }
}
