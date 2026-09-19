using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The other side of the unseen-gap line: what the volume says about itself. These run against the
/// real volume the tests are running on, because that is the only thing that can answer.
/// </summary>
public class VolumeReaderTests
{
    [Fact]
    public void Read_TheVolumeTheseTestsRunOn_AnswersWithASizeAndAUsedFigure()
    {
        using var tree = new FixtureTree(nameof(Read_TheVolumeTheseTestsRunOn_AnswersWithASizeAndAUsedFigure));

        var volume = VolumeReader.Read(tree.Root);

        Assert.True(volume.Available, volume.UnavailableReason ?? "no reason was given");
        Assert.Null(volume.UnavailableReason);
        Assert.True(volume.TotalBytes > 0);
        Assert.Equal(volume.TotalBytes, volume.UsedBytes + volume.FreeBytes);
    }

    [Fact]
    public void Read_AFolderAndTheVolumeItSitsOn_GiveTheSameAnswer()
    {
        using var tree = new FixtureTree(nameof(Read_AFolderAndTheVolumeItSitsOn_GiveTheSameAnswer));
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(tree.Root));
        Assert.False(string.IsNullOrEmpty(volumeRoot));

        Assert.Equal(VolumeReader.Read(volumeRoot).Name, VolumeReader.Read(tree.Root).Name);
    }

    [Fact]
    public void Read_ABlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => VolumeReader.Read("  "));
    }

    [Fact]
    public void IsVolumeRoot_TheRootOfTheVolumeTheseTestsRunOn_IsTrue()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.False(string.IsNullOrEmpty(volumeRoot));

        Assert.True(VolumeReader.IsVolumeRoot(volumeRoot));
    }

    [Fact]
    public void IsVolumeRoot_AFolderOnTheVolume_IsFalse()
    {
        using var tree = new FixtureTree(nameof(IsVolumeRoot_AFolderOnTheVolume_IsFalse));

        Assert.False(VolumeReader.IsVolumeRoot(tree.Root));
    }

    [Fact]
    public void IsVolumeRoot_ABlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => VolumeReader.IsVolumeRoot(string.Empty));
    }
}
