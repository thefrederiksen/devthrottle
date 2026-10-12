using System.Text;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// <see cref="DirectoryJunction"/> makes the junction cmd.exe's <c>mklink /J</c> used to make, in this process.
/// The real-junction tests only mean something on Windows, where every gate run happens; elsewhere they return
/// at once, as junctions do not exist there. The reparse-data layout is checked on every platform.
/// </summary>
public sealed class DirectoryJunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "junction-tests-" + Guid.NewGuid().ToString("N"));

    public DirectoryJunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // A recursive delete is refused on a junction - one made by mklink /J as well - so each junction is
        // removed as a link first, which leaves its target alone.
        foreach (var entry in Directory.EnumerateDirectories(_root))
            if (new DirectoryInfo(entry).LinkTarget is not null) Directory.Delete(entry);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Create_MakesAJunctionThatShowsTheTargetsFiles_AndNamesTheTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "SKILL.md"), "body");
        var link = Path.Combine(_root, "link");

        // Act
        DirectoryJunction.Create(link, target);

        // Assert
        var info = new DirectoryInfo(link);
        Assert.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint), "the link is not a reparse point");
        Assert.Equal(target, info.LinkTarget);
        Assert.Equal("body", File.ReadAllText(Path.Combine(link, "SKILL.md")));
    }

    [Fact]
    public void Create_DeletingTheJunction_LeavesTheTargetAndItsFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "kept");
        var link = Path.Combine(_root, "link");
        DirectoryJunction.Create(link, target);

        // Act
        Directory.Delete(link);

        // Assert
        Assert.False(Directory.Exists(link));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Create_ATargetWithATrailingSeparatorOrRelativeParts_PointsAtTheFullPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "link");

        // Act
        DirectoryJunction.Create(link, Path.Combine(_root, "other", "..", "target") + Path.DirectorySeparatorChar);

        // Assert
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void Create_ATargetThatDoesNotExist_StillMakesTheJunction_LikeMklink()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Path.Combine(_root, "not-yet");
        var link = Path.Combine(_root, "link");

        // Act
        DirectoryJunction.Create(link, target);

        // Assert
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "the link is not a reparse point");
        Assert.False(Directory.Exists(target), "making the junction created its target");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // the lambda below hides the guard from the analyzer
    public void Create_SomethingAlreadyAtTheLinkPath_ThrowsAndLeavesItAlone()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var existing = Directory.CreateDirectory(Path.Combine(_root, "link")).FullName;
        File.WriteAllText(Path.Combine(existing, "mine.txt"), "mine");

        // Act + Assert
        Assert.Throws<IOException>(() => DirectoryJunction.Create(existing, target));
        Assert.Null(new DirectoryInfo(existing).LinkTarget);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(existing, "mine.txt")));
    }

    [Fact]
    public void MountPointReparseData_LaysOutTagLengthsAndBothNames()
    {
        // Act
        var buffer = DirectoryJunction.MountPointReparseData(@"\??\C:\t", @"C:\t");

        // Assert: tag, data length, reserved, then the four offsets and lengths, then the two names.
        var substitute = Encoding.Unicode.GetBytes(@"\??\C:\t");
        var print = Encoding.Unicode.GetBytes(@"C:\t");
        Assert.Equal(0xA0000003u, BitConverter.ToUInt32(buffer, 0));
        Assert.Equal(8 + substitute.Length + 2 + print.Length + 2, BitConverter.ToUInt16(buffer, 4));
        Assert.Equal(0, BitConverter.ToUInt16(buffer, 6));
        Assert.Equal(0, BitConverter.ToUInt16(buffer, 8));
        Assert.Equal(substitute.Length, BitConverter.ToUInt16(buffer, 10));
        Assert.Equal(substitute.Length + 2, BitConverter.ToUInt16(buffer, 12));
        Assert.Equal(print.Length, BitConverter.ToUInt16(buffer, 14));
        Assert.Equal(@"\??\C:\t", Encoding.Unicode.GetString(buffer, 16, substitute.Length));
        Assert.Equal(@"C:\t", Encoding.Unicode.GetString(buffer, 16 + substitute.Length + 2, print.Length));
        Assert.Equal(buffer.Length, 8 + BitConverter.ToUInt16(buffer, 4));
    }

    [Fact]
    public void Create_ALinkPathPast260Characters_IsMade_LikeMklink()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange: a parent folder deep enough that the link path is well past the old 260-character limit.
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(_root, new string('d', 120), new string('e', 120))).FullName;
        var link = Path.Combine(deep, "link-" + new string('f', 40));
        Assert.True(link.Length > 260, $"the link path is only {link.Length} characters");

        // Act
        DirectoryJunction.Create(link, target);

        // Assert
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
        Directory.Delete(link);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // the lambda below hides the guard from the analyzer
    public void Create_TheLinksParentFolderIsMissing_ThrowsAndCreatesNothing_LikeMklink()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var missingParent = Path.Combine(_root, "missing");

        // Act + Assert
        Assert.Throws<System.ComponentModel.Win32Exception>(() => DirectoryJunction.Create(Path.Combine(missingParent, "link"), target));
        Assert.False(Directory.Exists(missingParent), "the missing parent folder was created");
    }

    [Fact]
    public void MountPointReparseData_ATargetPastWhatWindowsAccepts_IsRefusedWithAClearError()
    {
        // Arrange: a target whose reparse data would pass the 16 KB Windows accepts.
        var target = @"C:\" + new string('x', DirectoryJunction.MaximumReparseDataBufferSize / 4);

        // Act + Assert
        Assert.Throws<PathTooLongException>(() => DirectoryJunction.MountPointReparseData(@"\??\" + target, target));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"\\?\C:\a\b")]
    [InlineData(@"\\server\share\a", @"\\?\UNC\server\share\a")]
    [InlineData(@"\\?\C:\a", @"\\?\C:\a")]
    public void ExtendedLengthPath_PrefixesAFullPathOnce(string fullPath, string expected)
    {
        Assert.Equal(expected, DirectoryJunction.ExtendedLengthPath(fullPath));
    }
}
