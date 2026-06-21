using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #497: <see cref="TranscriptionModeConfig"/> persists the mode to config.json so it
/// survives a restart. Each method runs against an isolated CC_DIRECTOR_ROOT; the
/// CcStorageRoot collection serializes classes that mutate the process-wide root.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class TranscriptionModeConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public TranscriptionModeConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-txmode-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_DefaultsToLocal()   // issue #541: the default changed from Byo to Local
        => Assert.Equal(TranscriptionMode.Local, TranscriptionModeConfig.Get());

    [Fact]
    public void Default_IsLocal()                 // issue #541: the constant itself is now Local
        => Assert.Equal(TranscriptionMode.Local, TranscriptionModeConfig.Default);

    [Fact]
    public void SetThenGet_Local_PersistsAcrossReread()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        TranscriptionModeConfig.Set(TranscriptionMode.Local);

        Assert.Equal(TranscriptionMode.Local, TranscriptionModeConfig.Get());
    }

    [Fact]
    public void SetThenGet_DevThrottle_PersistsAcrossReread()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);

        // A fresh Get() re-reads config.json from disk - the same path a restarted process takes.
        Assert.Equal(TranscriptionMode.DevThrottle, TranscriptionModeConfig.Get());
        Assert.True(File.Exists(CcStorage.ConfigJson()));
    }

    [Fact]
    public void SetThenGet_Byo_PersistsAcrossReread()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);

        Assert.Equal(TranscriptionMode.Byo, TranscriptionModeConfig.Get());
    }

    [Fact]
    public void Set_DoesNotDropSiblingConfigKeys()
    {
        // The mode write must merge-patch, never clobber an unrelated top-level key.
        CcDirectorConfigService.MergePatch(new System.Text.Json.Nodes.JsonObject { ["wingman_enabled"] = true });
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);

        var raw = CcDirectorConfigService.ReadRaw();
        Assert.Equal("devthrottle", raw["transcription_mode"]!.GetValue<string>());
        Assert.True(raw["wingman_enabled"]!.GetValue<bool>());
    }
}
