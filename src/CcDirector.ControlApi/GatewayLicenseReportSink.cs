using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>The License Switching mission: the usage reporter's way up - this Director's Gateway tunnel. Sends only
/// while the tunnel is connected and the Gateway has <c>PushLicenseUsage</c> (contract 6.5, inert alone).</summary>
internal sealed class GatewayLicenseReportSink : ILicenseReportSink
{
    private readonly GatewayStreamClient _client;

    public GatewayLicenseReportSink(GatewayStreamClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool CanSend => _client.CanPushLicenseUsage;

    public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken) =>
        _client.PushLicenseUsageAsync(report, cancellationToken);
}
