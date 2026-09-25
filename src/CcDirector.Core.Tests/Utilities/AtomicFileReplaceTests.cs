using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// The replace must go through while another reader holds the old file open - that is the whole reason
/// it exists (issue #3393). The classic Windows rename behind File.Move throws here.
/// </summary>
public sealed class AtomicFileReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atomic-replace-" + Guid.NewGuid().ToString("N"));

    public AtomicFileReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Replace_WhileAReaderHoldsTheDestination_ReplacesItAndTheReaderKeepsTheOldText()
    {
        var destination = Path.Combine(_dir, "list.json");
        var source = Path.Combine(_dir, "list.tmp");
        File.WriteAllText(destination, "old");
        File.WriteAllText(source, "new");

        using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            AtomicFileReplace.Replace(source, destination);

            using var reader = new StreamReader(held);
            Assert.Equal("old", reader.ReadToEnd());
        }

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Replace_NoDestinationYet_MovesTheFileIntoPlace()
    {
        var destination = Path.Combine(_dir, "list.json");
        var source = Path.Combine(_dir, "list.tmp");
        File.WriteAllText(source, "first");

        AtomicFileReplace.Replace(source, destination);

        Assert.Equal("first", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }
}
