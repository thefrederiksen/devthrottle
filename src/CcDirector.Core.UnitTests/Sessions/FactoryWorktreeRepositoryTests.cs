using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT'S REPOSITORY NAME, RESOLVED THROUGH THIS MACHINE'S ONE REPOSITORY LIST (Factory Definition mission, phase 5,
/// design section 2; devthrottle#4301). A name the machine does not know is refused; nothing falls back to a checkout.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryWorktreeRepositoryTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("dt-seat-repo-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private List<RepositoryConfig> Registered(params (string Name, string Path)[] repos)
        => repos.Select(r => new RepositoryConfig { Name = r.Name, Path = r.Path }).ToList();

    private static RepositoryConfig Entry(string name, string path, bool foundByScan)
        => new() { Name = name, Path = path, FoundByScan = foundByScan };

    [Fact]
    public void Resolve_ARegisteredName_IsItsFolderWhateverItsCase()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("DevThrottle", _repo)), "outreach", "devthrottle", "SOREN_NORTH");

        Assert.True(result.Ok);
        Assert.Equal(_repo, result.Path);
    }

    [Fact]
    public void Resolve_ANameThisMachineDoesNotKnow_IsRefusedAndSaysBrowsingAddsIt()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("other", _repo)), "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Equal("The seat outreach works in the repository 'devthrottle', but SOREN_NORTH does not know a repository of " +
                     "that name, so no session was started. Browsing to its folder once in the Director on SOREN_NORTH " +
                     "adds it; or change the seat's repository in the definition.", result.Refusal);
    }

    [Fact]
    public void Resolve_TwoEntriesWithTheOneName_NeitherAddedByHand_IsRefusedNamingBothFolders()
    {
        var result = FactoryWorktreeRepository.Resolve(
            new[] { Entry("devthrottle", _repo, foundByScan: true), Entry("devthrottle", @"D:\other", foundByScan: true) },
            "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Equal($"The seat outreach works in the repository 'devthrottle', and SOREN_NORTH knows 2 repositories by " +
                     $"that name ({_repo}, D:\\other), none of them added by hand, so no session was started. Rename all but " +
                     "one of them on that machine.", result.Refusal);
    }

    [Fact]
    public void Resolve_TwoEntriesWithTheOneName_BothAddedByHand_IsRefusedRatherThanGuessed()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("devthrottle", _repo), ("devthrottle", @"D:\other")),
            "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Contains("knows 2 repositories by that name", result.Refusal);
        Assert.Contains("2 of them added by hand", result.Refusal);
        Assert.Contains(_repo, result.Refusal);
        Assert.Contains(@"D:\other", result.Refusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_TwoEntriesWithTheOneName_ExactlyOneAddedByHand_IsTheOneAddedByHand(bool handAddedFirst)
    {
        var byHand = Entry("devthrottle", _repo, foundByScan: false);
        var found = Entry("devthrottle", @"D:\elsewhere\devthrottle", foundByScan: true);

        var result = FactoryWorktreeRepository.Resolve(handAddedFirst ? new[] { byHand, found } : new[] { found, byHand },
            "outreach", "devthrottle", "SOREN_NORTH");

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(_repo, result.Path);
    }

    [Fact]
    public void Resolve_ARepositoryOnlyTheScanFound_IsItsFolder()
    {
        var result = FactoryWorktreeRepository.Resolve(new[] { Entry("private", _repo, foundByScan: true) },
            "clerk", "private", "SOREN_NORTH");

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(_repo, result.Path);
    }

    [Fact]
    public void Resolve_ARegistrationWhoseFolderIsGone_IsRefused()
    {
        var gone = Path.Combine(_repo, "gone");

        var result = FactoryWorktreeRepository.Resolve(Registered(("devthrottle", gone)), "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Contains("but that folder does not exist", result.Refusal);
    }
}
