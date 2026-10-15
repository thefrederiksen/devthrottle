using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT'S REPOSITORY NAME, RESOLVED THROUGH THIS MACHINE'S OWN REGISTERED REPOSITORIES (Factory Definition mission,
/// phase 5, design section 2). Not registered is refused in the design's words; nothing falls back to a checkout.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryWorktreeRepositoryTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("dt-seat-repo-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private List<RepositoryConfig> Registered(params (string Name, string Path)[] repos)
        => repos.Select(r => new RepositoryConfig { Name = r.Name, Path = r.Path }).ToList();

    [Fact]
    public void Resolve_ARegisteredName_IsItsFolderWhateverItsCase()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("DevThrottle", _repo)), "outreach", "devthrottle", "SOREN_NORTH");

        Assert.True(result.Ok);
        Assert.Equal(_repo, result.Path);
    }

    [Fact]
    public void Resolve_ANameThisMachineHasNotRegistered_IsRefusedInTheDesignsWords()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("other", _repo)), "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Equal("The seat outreach works in the repository 'devthrottle', which is not registered on SOREN_NORTH, so no " +
                     "session was started. Register it there, or change the seat's repository in the definition.", result.Refusal);
    }

    [Fact]
    public void Resolve_TwoRegistrationsWithTheOneName_IsRefusedRatherThanGuessed()
    {
        var result = FactoryWorktreeRepository.Resolve(Registered(("devthrottle", _repo), ("devthrottle", @"D:\other")),
            "outreach", "devthrottle", "SOREN_NORTH");

        Assert.False(result.Ok);
        Assert.Contains("has 2 repositories registered under that name", result.Refusal);
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
