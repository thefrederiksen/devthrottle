using System.Text.Json;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>Loads the real captured screens in TestData/doorbell (see its README for where each came from).</summary>
internal static class DoorbellCaptures
{
    private sealed class Capture
    {
        public string[] Rows { get; set; } = [];
        public int CursorRow { get; set; }
        public int CursorCol { get; set; }
        public bool CursorVisible { get; set; }
    }

    public static ScreenFrame Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "doorbell", name + ".json");
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(capture.Rows.Length == 40, $"{name} should be a full 40-row capture");
        return new ScreenFrame(capture.Rows, capture.CursorRow, capture.CursorCol, capture.CursorVisible);
    }

    /// <summary>A copy of <paramref name="frame"/> with one row replaced.</summary>
    public static ScreenFrame WithRow(ScreenFrame frame, int index, string row)
    {
        var rows = frame.Rows.ToArray();
        rows[index] = row;
        return frame with { Rows = rows };
    }

    /// <summary>The index of the first row starting with <paramref name="prefix"/>, searching from the bottom.</summary>
    public static int LastRowStartingWith(ScreenFrame frame, string prefix)
    {
        for (var i = frame.Rows.Count - 1; i >= 0; i--)
            if (frame.Rows[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
        throw new InvalidOperationException($"no row starts with '{prefix}'");
    }
}
