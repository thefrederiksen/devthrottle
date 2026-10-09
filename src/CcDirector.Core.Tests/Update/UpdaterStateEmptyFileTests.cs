using System.Text.Json;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// An updater state file that is zero bytes long (issue #3666).
///
/// The launcher reads every updater state file once an hour. A zero-byte file failed to parse, the
/// load logged "Load FAILED (using empty state)" and returned an empty state - and nothing ever wrote a
/// valid file back, so one machine reported the same failure 165 times in a week, across six releases.
/// The file can only be zero bytes because a save was cut off between truncating the file and writing
/// it: the serializer never produces an empty string.
/// </summary>
public sealed class UpdaterStateEmptyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "updaterstate-empty-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_dir, "updater-state.json");

    public UpdaterStateEmptyFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void LoadFrom_ZeroByteFile_ReplacesItWithAValidState()
    {
        File.WriteAllText(StatePath, "");

        var state = UpdaterState.LoadFrom(StatePath);

        Assert.Null(state.StagedVersion);
        var text = File.ReadAllText(StatePath);
        Assert.NotNull(JsonSerializer.Deserialize<UpdaterState>(text));
    }

    [Fact]
    public void SaveTo_ExistingFile_ReplacesItAndLeavesNoTemporaryFile()
    {
        new UpdaterState { StagedVersion = "1.0.0" }.SaveTo(StatePath);

        new UpdaterState { StagedVersion = "2.0.0" }.SaveTo(StatePath);

        Assert.Equal("2.0.0", UpdaterState.LoadFrom(StatePath).StagedVersion);
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_dir));
    }

    /// <summary>
    /// The Director, the launcher and the update helper all write this file and nothing serialises
    /// them. With one shared temporary name, two saves at once collided on it and one threw; a load holding
    /// the file open can refuse a save's replace the same way.
    /// </summary>
    [Fact]
    public async Task SaveTo_ConcurrentSaves_AllSucceedAndLeaveOneValidFile()
    {
        var saves = Enumerable.Range(0, 200)
            .Select(i => Task.Run(() => new UpdaterState { StagedVersion = $"1.0.{i}" }.SaveTo(StatePath)))
            .Concat(Enumerable.Range(0, 200).Select(_ => Task.Run(() => { UpdaterState.LoadFrom(StatePath); })))
            .ToArray();

        await Task.WhenAll(saves);

        Assert.StartsWith("1.0.", UpdaterState.LoadFrom(StatePath).StagedVersion);
        Assert.Equal(new[] { StatePath }, Directory.GetFiles(_dir));
    }
}
