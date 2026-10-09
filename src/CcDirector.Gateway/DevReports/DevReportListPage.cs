using System.Globalization;
using System.Text;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.DevReports;

/// <summary>One page of the owner's Reports list (<see cref="DevReportStore.ListPage"/>): the reports, newest update
/// first, and whether any report is older than the last of them.</summary>
public sealed record DevReportListPage(IReadOnlyList<DevReportEntity> Reports, bool More);

/// <summary>
/// Where a page of the Reports list ended: the last report's update time, session and key - the three fields the list
/// is ordered by. It travels to the client as an opaque marker (<see cref="ToMarker"/>) and comes back as
/// <c>?after=</c>; the client never reads it, it only hands it back for the next page.
/// </summary>
public readonly record struct DevReportListPosition(DateTime UpdatedAtUtc, string SessionId, string Key)
{
    /// <summary>The position just past this report.</summary>
    public static DevReportListPosition Of(DevReportEntity report) => new(report.UpdatedAtUtc, report.SessionId, report.Key);

    /// <summary>The marker the client hands back: update time in ticks, then session, then key, as URL-safe base64.</summary>
    public string ToMarker()
    {
        var text = UpdatedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + SessionId + "\n" + Key;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a marker <see cref="ToMarker"/> made. Null for anything else: the caller answers that with a
    /// plain refusal, never a guess at where the list was.</summary>
    public static DevReportListPosition? FromMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length > 2048) return null;
        var b64 = marker.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        var parts = Encoding.UTF8.GetString(bytes).Split('\n');
        if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0) return null;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;
        return new DevReportListPosition(new DateTime(ticks, DateTimeKind.Utc), parts[1], parts[2]);
    }
}
