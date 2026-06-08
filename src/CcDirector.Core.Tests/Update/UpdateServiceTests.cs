using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

public class UpdateServiceTests
{
    // ---- AssetNameFor -----------------------------------------------------

    [Fact]
    public void AssetNameFor_WindowsX64_ReturnsExe()
    {
        var name = UpdateService.AssetNameFor(OSPlatform.Windows, Architecture.X64);
        Assert.Equal("cc-director-win-x64.exe", name);
    }

    [Fact]
    public void AssetNameFor_MacArm64_ReturnsZip()
    {
        var name = UpdateService.AssetNameFor(OSPlatform.OSX, Architecture.Arm64);
        Assert.Equal("cc-director-mac-arm64.zip", name);
    }

    [Theory]
    [InlineData("OSX", "X64")]      // Intel Mac not built
    [InlineData("Windows", "Arm64")] // Windows ARM not built
    public void AssetNameFor_UnsupportedCombo_ReturnsNull(string osName, string archName)
    {
        var os = osName == "OSX" ? OSPlatform.OSX : OSPlatform.Windows;
        var arch = Enum.Parse<Architecture>(archName);
        Assert.Null(UpdateService.AssetNameFor(os, arch));
    }

    [Fact]
    public void AssetNameFor_Linux_ReturnsNull()
    {
        Assert.Null(UpdateService.AssetNameFor(OSPlatform.Linux, Architecture.X64));
    }

    // ---- TryParseTag ------------------------------------------------------

    [Theory]
    [InlineData("v0.3.3", 0, 3, 3)]
    [InlineData("V0.3.3", 0, 3, 3)]
    [InlineData("0.3.3", 0, 3, 3)]
    [InlineData("v1.2.0-rc1", 1, 2, 0)]  // pre-release suffix dropped
    [InlineData("v2.0", 2, 0, 0)]
    public void TryParseTag_ValidTags_ParseToNormalizedVersion(string tag, int major, int minor, int build)
    {
        var v = UpdateService.TryParseTag(tag);
        Assert.NotNull(v);
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("vX.Y.Z")]
    public void TryParseTag_Invalid_ReturnsNull(string tag)
    {
        Assert.Null(UpdateService.TryParseTag(tag));
    }

    // ---- ShouldStage ------------------------------------------------------

    [Fact]
    public void ShouldStage_NewerVersion_True()
    {
        Assert.True(UpdateService.ShouldStage(new Version(0, 3, 2), new Version(0, 3, 3), new UpdaterState()));
    }

    [Fact]
    public void ShouldStage_SameVersion_False()
    {
        // Current is a 4-part assembly version; latest is normalized 3-part.
        Assert.False(UpdateService.ShouldStage(new Version(0, 3, 3, 0), new Version(0, 3, 3), new UpdaterState()));
    }

    [Fact]
    public void ShouldStage_OlderVersion_False()
    {
        Assert.False(UpdateService.ShouldStage(new Version(0, 4, 0), new Version(0, 3, 3), new UpdaterState()));
    }

    [Fact]
    public void ShouldStage_DismissedSameVersion_False()
    {
        var state = new UpdaterState { DismissedVersion = "0.3.3" };
        Assert.False(UpdateService.ShouldStage(new Version(0, 3, 2), new Version(0, 3, 3), state));
    }

    [Fact]
    public void ShouldStage_DismissedDifferentVersion_True()
    {
        var state = new UpdaterState { DismissedVersion = "0.3.2" };
        Assert.True(UpdateService.ShouldStage(new Version(0, 3, 1), new Version(0, 3, 3), state));
    }

    // ---- Sha256Matches ----------------------------------------------------

    [Fact]
    public void Sha256Matches_CorrectHash_True()
    {
        var (path, hex) = WriteTempWithHash("hello cc-director");
        try
        {
            Assert.True(UpdateService.Sha256Matches(path, hex));
            Assert.True(UpdateService.Sha256Matches(path, hex.ToLowerInvariant())); // case-insensitive
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Sha256Matches_WrongHash_False()
    {
        var (path, _) = WriteTempWithHash("hello cc-director");
        try
        {
            Assert.False(UpdateService.Sha256Matches(path, new string('a', 64)));
        }
        finally { File.Delete(path); }
    }

    // ---- Gating -----------------------------------------------------------

    [Fact]
    public async Task CheckAndStageAsync_Disabled_MakesNoNetworkCall()
    {
        var handler = new ThrowingHandler();
        var svc = new UpdateService(
            new UpdateOptions
            {
                Enabled = false,
                CurrentVersion = new Version(0, 3, 2),
                InstallTarget = "/tmp/cc-director",
            },
            handler);

        // Must complete without touching the network (handler throws if called).
        await svc.CheckAndStageAsync();
        Assert.False(handler.WasCalled);
    }

    // ---- UpdateProgress.Fraction -----------------------------------------

    [Fact]
    public void Fraction_KnownTotal_IsRatio()
    {
        var p = new UpdateProgress(UpdatePhase.Downloading, "0.7.0", Downloaded: 25, Total: 100);
        Assert.Equal(0.25, p.Fraction);
    }

    [Fact]
    public void Fraction_ZeroTotal_IsNull()
    {
        var p = new UpdateProgress(UpdatePhase.Downloading, "0.7.0", Downloaded: 25, Total: 0);
        Assert.Null(p.Fraction);
    }

    // ---- Progress events --------------------------------------------------

    [Fact]
    public async Task CheckAndStageAsync_Disabled_RaisesNoProgress()
    {
        var svc = new UpdateService(
            new UpdateOptions
            {
                Enabled = false,
                CurrentVersion = new Version(0, 3, 2),
                InstallTarget = "/tmp/cc-director",
            },
            new ThrowingHandler());

        var phases = new List<UpdatePhase>();
        svc.ProgressChanged += p => phases.Add(p.Phase);

        // An inert (dev/slot) build must stay completely silent -- no "checking" flash.
        await svc.CheckAndStageAsync();
        Assert.Empty(phases);
    }

    // ---- helpers ----------------------------------------------------------

    private static (string path, string sha256Hex) WriteTempWithHash(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cc-update-test-{Guid.NewGuid():N}.bin");
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        var hex = Convert.ToHexString(SHA256.HashData(bytes));
        return (path, hex);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("Network must not be called when the updater is disabled.");
        }
    }
}
