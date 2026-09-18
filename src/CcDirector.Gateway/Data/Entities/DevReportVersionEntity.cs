namespace CcDirector.Gateway.Data.Entities;

/// <summary>One published version of a dev report (<c>dev_report_versions</c>): the bytes exactly as sent,
/// their hash and length, and what the shape check read from them. Unique per (tenant, report, version).</summary>
public sealed class DevReportVersionEntity : GatewayMintedKeyEntity
{
    public Guid ReportId { get; set; }

    public int Version { get; set; }

    /// <summary>The report HTML exactly as published.</summary>
    public string Html { get; set; } = "";

    /// <summary>SHA-256 of the UTF-8 bytes, lower-case hex.</summary>
    public string ByteHash { get; set; } = "";

    /// <summary>The UTF-8 byte length of <see cref="Html"/>.</summary>
    public long ByteLength { get; set; }

    public DateTime PublishedAtUtc { get; set; }

    public string Status { get; set; } = "";

    public string Title { get; set; } = "";
}
