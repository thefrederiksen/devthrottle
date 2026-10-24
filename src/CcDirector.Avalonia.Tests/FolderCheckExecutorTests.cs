using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #4421: the Director's <c>folder-check</c> verb, which a schedule's save asks. The disk is a fake, so the
/// answer is proven for a folder, a file and nothing at all without touching this machine.
/// </summary>
public sealed class FolderCheckExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DirectorCommand Command(string payload) => new()
    {
        CommandId = "c1",
        Verb = ScheduleFolderCheckContract.Verb,
        SessionId = "",
        PayloadJson = payload,
    };

    private static FolderCheckExecutor Executor(string? folder = null, string? file = null, string os = DirectorOs.MacOs)
        => new(p => p == folder, p => p == file, os);

    private static FolderCheckAnswer Answer(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        return JsonSerializer.Deserialize<FolderCheckAnswer>(result.BodyJson!, Json)!;
    }

    [Fact]
    public void AreaFor_TheFolderCheckVerb_IsOwnedByTheFolderCheckArea()
    {
        Assert.IsType<FolderCheckExecutor>(SessionCommandExecutor.AreaFor("folder-check"));
    }

    [Fact]
    public void Answer_AFolderThatIsThere_ExistsAndIsAFolder_AndNamesTheSystem()
    {
        var answer = Answer(Executor(folder: "/Users/pat/Documents/project").Answer(Command("""{"path": "/Users/pat/Documents/project"}""")));

        Assert.True(answer.Exists);
        Assert.True(answer.IsDirectory);
        Assert.Equal("macos", answer.Os);
    }

    [Fact]
    public void Answer_APathThatIsNotThere_DoesNotExist()
    {
        // The customer's path: a Windows-shaped path on a Mac is simply not a folder there.
        var answer = Answer(Executor(folder: "/Users/pat/Documents/project").Answer(Command("""{"path": "\\\\documents\\\\project"}""")));

        Assert.False(answer.Exists);
        Assert.False(answer.IsDirectory);
    }

    [Fact]
    public void Answer_AFile_ExistsButIsNotAFolder()
    {
        var answer = Answer(Executor(file: "/Users/pat/notes.txt").Answer(Command("""{"path": "/Users/pat/notes.txt"}""")));

        Assert.True(answer.Exists);
        Assert.False(answer.IsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"path": "  "}""")]
    public void Answer_NoReadablePath_IsABadRequest(string payload)
    {
        var result = Executor().Answer(Command(payload));

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("path", result.Error);
    }
}
