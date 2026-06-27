using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// Issue #760: <see cref="HistoryFilterConfig"/> reads the History tab's "Show:" toggles from
/// config.json's "history_filter" object. The headline guarantee under test: with no config (or a
/// missing key) the machinery is HIDDEN by default, so the tab opens as just the conversation. A
/// round-trip (Save then Get) and the no-fallback throw on a wrong-typed key are also locked in.
/// Each method runs against an isolated CC_DIRECTOR_ROOT.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class HistoryFilterConfigTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;

    public HistoryFilterConfigTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-historyfilter-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Get_NoConfig_HidesEverythingByDefault()
    {
        var cfg = HistoryFilterConfig.Get();

        Assert.False(cfg.ShowToolCalls);
        Assert.False(cfg.ShowToolResults);
        Assert.False(cfg.ShowThinking);
        Assert.Equal(HistoryFilterConfig.Default, cfg);
    }

    [Fact]
    public void Get_SectionPresentButMissingKey_DefaultsToHidden()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["history_filter"] = new JsonObject { ["show_tool_calls"] = true },
        });

        var cfg = HistoryFilterConfig.Get();

        Assert.True(cfg.ShowToolCalls);     // explicit
        Assert.False(cfg.ShowToolResults);  // unspecified -> hidden (new default)
        Assert.False(cfg.ShowThinking);     // unspecified -> hidden (new default)
    }

    [Fact]
    public void SaveThenGet_RoundTrips()
    {
        new HistoryFilterConfig(ShowToolCalls: false, ShowToolResults: false, ShowThinking: true).Save();

        var cfg = HistoryFilterConfig.Get();

        Assert.False(cfg.ShowToolCalls);
        Assert.False(cfg.ShowToolResults);
        Assert.True(cfg.ShowThinking);
    }

    [Fact]
    public void Save_PreservesOtherSections()
    {
        CcDirectorConfigService.MergePatch(new JsonObject { ["addressing_mode"] = "lan" });

        new HistoryFilterConfig(ShowToolCalls: false, ShowToolResults: true, ShowThinking: true).Save();

        // The unrelated section must survive the filter write (deep-merge, not whole-file rewrite).
        var addressingMode = CcDirectorConfigService.ReadRaw()["addressing_mode"];
        Assert.NotNull(addressingMode);
        Assert.Equal("lan", addressingMode.GetValue<string>());
        Assert.False(HistoryFilterConfig.Get().ShowToolCalls);
    }

    [Fact]
    public void Get_WrongTypedKey_ThrowsWithFixNamed()
    {
        CcDirectorConfigService.MergePatch(new JsonObject
        {
            ["history_filter"] = new JsonObject { ["show_thinking"] = "no" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => HistoryFilterConfig.Get());
        Assert.Contains("history_filter.show_thinking", ex.Message);
    }

    [Fact]
    public void Get_SectionWrongType_Throws()
    {
        CcDirectorConfigService.MergePatch(new JsonObject { ["history_filter"] = "all" });

        var ex = Assert.Throws<InvalidOperationException>(() => HistoryFilterConfig.Get());
        Assert.Contains("history_filter", ex.Message);
    }
}
