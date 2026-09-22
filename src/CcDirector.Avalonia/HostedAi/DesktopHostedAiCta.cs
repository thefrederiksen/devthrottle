using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.HostedAi;

/// <summary>
/// The one place the desktop turns a shared <see cref="HostedAiCtaAction"/> into a destination
/// (issue #940, epic #937). "Add credits" opens the public billing page directly; "Add a key" opens
/// the Cockpit (where the AI settings live - the desktop has no local AI-settings screen), resolved
/// through the Gateway front-door the rest of the app already uses. Centralized so every desktop voice
/// surface routes the same call-to-action to the same place.
/// </summary>
public static class DesktopHostedAiCta
{
    /// <summary>
    /// Open the destination for <paramref name="action"/> in the browser. Billing is a direct public
    /// URL; Settings resolves the Cockpit front-door first (never a localhost URL, matching the rest of
    /// the app). Best-effort: a resolve/launch failure is logged, never thrown to the click handler.
    /// </summary>
    public static async Task InvokeAsync(HostedAiCtaAction action, CancellationToken ct = default)
    {
        FileLog.Write($"[DesktopHostedAiCta] InvokeAsync: action={action}");
        var url = action switch
        {
            HostedAiCtaAction.OpenBilling => HostedAiUrls.Billing,
            HostedAiCtaAction.OpenPricing => HostedAiUrls.Pricing,
            HostedAiCtaAction.OpenSettings => await ResolveCockpitUrlAsync(ct).ConfigureAwait(false),
            _ => null,
        };
        OpenUrl(url);
    }

    /// <summary>
    /// Resolve the Cockpit front-door URL by asking the configured Gateway (<c>GET {base}/cockpit</c>),
    /// the same probe the toolbar Cockpit button uses. Returns null when no tailnet URL is available
    /// (Tailscale down) - the desktop never opens a localhost URL.
    /// </summary>
    private static async Task<string?> ResolveCockpitUrlAsync(CancellationToken ct)
    {
        var baseUrl = CockpitUrlResolver.ResolveCockpitBase(GatewayConfig.Load());
        try
        {
            using var http = new HttpClient(CcDirector.Core.Network.GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(8) };
            var info = await http.GetFromJsonAsync<CockpitInfoDto>(baseUrl + "/cockpit", ct).ConfigureAwait(false);
            return info?.Url;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DesktopHostedAiCta] could not resolve the Cockpit URL from {baseUrl}: {ex.Message}");
            return null;
        }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            FileLog.Write("[DesktopHostedAiCta] OpenUrl: no URL to open (call-to-action target unavailable)");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DesktopHostedAiCta] OpenUrl FAILED for {url}: {ex.Message}");
        }
    }
}
