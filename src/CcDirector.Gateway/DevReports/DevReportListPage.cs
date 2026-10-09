using System.Text;
using System.Text.Json;
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

    private sealed record Wire(long T, string S, string K);

    /// <summary>The longest marker read back. A key may be <see cref="Api.DevReportEndpoints.MaxKeyLength"/> characters,
    /// and JSON writes a character it escapes as up to six, so the longest real marker is about 4,200 characters of
    /// base64; this leaves room above that and still refuses an absurd one.</summary>
    public const int MaxMarkerLength = 16 * 1024;

    /// <summary>The marker the client hands back: update time in ticks, session and key as JSON - so a key may hold any
    /// character, a line break included - then URL-safe base64.</summary>
    public string ToMarker()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Wire(UpdatedAtUtc.Ticks, SessionId, Key));
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a marker <see cref="ToMarker"/> made. Null for anything else: the caller answers that with a
    /// plain refusal, never a guess at where the list was.</summary>
    public static DevReportListPosition? FromMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length > MaxMarkerLength) return null;
        var b64 = marker.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        if (wire is null || string.IsNullOrEmpty(wire.S) || string.IsNullOrEmpty(wire.K)) return null;
        if (wire.T < DateTime.MinValue.Ticks || wire.T > DateTime.MaxValue.Ticks) return null;
        return new DevReportListPosition(new DateTime(wire.T, DateTimeKind.Utc), wire.S, wire.K);
    }
}
