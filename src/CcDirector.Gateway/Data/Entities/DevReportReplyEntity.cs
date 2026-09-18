namespace CcDirector.Gateway.Data.Entities;

/// <summary>One reply the agent posted on its report (<c>dev_report_replies</c>).</summary>
public sealed class DevReportReplyEntity : GatewayMintedKeyEntity
{
    public Guid ReportId { get; set; }

    public string Text { get; set; } = "";

    public DateTime AtUtc { get; set; }
}
