using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// CC Director's own settings page (distinct from the Claude Code config dialog). Edits
/// config.json - screenshots directory and gateway connection - via the single
/// round-trip-preserving writer <see cref="CcDirectorConfigService"/>, so untouched
/// sections are never dropped.
///
/// Gateway changes are applied live via the <c>reapplyGateway</c> delegate (the running
/// ControlApiHost re-registers with the gateway), so the user doesn't have to restart.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly Func<Task>? _reapplyGateway;
    private readonly Func<Task>? _reloadScreenshots;
    private readonly int _directorPort;

    // Short-timeout client shared by the gateway Test/Detect probes.
    private static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    // Loaded values, so Save only writes fields the user actually changed.
    private string _loadedScreenshots = "";
    private string _loadedGatewayUrl = "";
    private string _loadedGatewayAdvertised = "";
    private string _loadedGatewayToken = "";

    public SettingsDialog() : this(null, 0, null) { }

    /// <param name="reapplyGateway">Re-registers the running Director with the gateway after a gateway change.</param>
    /// <param name="directorPort">This Director's live control port, used to build the advertised "public URL" on Detect.</param>
    /// <param name="reloadScreenshots">Re-points the main window's screenshots tab after the folder changes.</param>
    public SettingsDialog(Func<Task>? reapplyGateway, int directorPort, Func<Task>? reloadScreenshots)
    {
        FileLog.Write($"[SettingsDialog] Constructor: initializing (directorPort={directorPort})");
        _reapplyGateway = reapplyGateway;
        _directorPort = directorPort;
        _reloadScreenshots = reloadScreenshots;
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        FileLog.Write("[SettingsDialog] LoadAsync: reading config.json");
        try
        {
            var (screenshots, url, advertised, token, raw) = await Task.Run(ReadConfigSnapshot);

            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;

            ScreenshotsDirBox.Text = screenshots;
            GatewayUrlBox.Text = url;
            GatewayAdvertisedBox.Text = advertised;
            GatewayTokenBox.Text = token;
            RawConfigBox.Text = raw;

            LoadingText.IsVisible = false;
            ContentPanel.IsVisible = true;
            FileLog.Write("[SettingsDialog] LoadAsync: loaded");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] LoadAsync FAILED: {ex.Message}");
            LoadingText.Text = $"Failed to read config.json: {ex.Message}";
            LoadingText.Foreground = global::Avalonia.Media.Brushes.IndianRed;
        }
    }

    /// <summary>Read config off the UI thread. Returns the current field values + pretty raw JSON.</summary>
    private static (string Screenshots, string Url, string Advertised, string Token, string Raw) ReadConfigSnapshot()
    {
        var root = CcDirectorConfigService.ReadRaw();
        var gateway = root["gateway"] as JsonObject;

        string Get(JsonObject? obj, string key) =>
            obj?[key] is JsonNode n && n is JsonValue ? n.GetValue<string>() : "";

        var screenshots = Get(root["screenshots"] as JsonObject, "source_directory");
        var url = Get(gateway, "url");
        var advertised = Get(gateway, "tailnetEndpoint");
        var token = Get(gateway, "token");
        var raw = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return (screenshots, url, advertised, token, raw);
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnBrowse_Click");
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select screenshots folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                ScreenshotsDirBox.Text = folders[0].Path.LocalPath;
                FileLog.Write($"[SettingsDialog] BtnBrowse_Click: selected {ScreenshotsDirBox.Text}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnBrowse_Click FAILED: {ex.Message}");
        }
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnSave_Click");
        SaveButton.IsEnabled = false;
        try
        {
            var screenshots = ScreenshotsDirBox.Text?.Trim() ?? "";
            var url = GatewayUrlBox.Text?.Trim() ?? "";
            var advertised = GatewayAdvertisedBox.Text?.Trim() ?? "";
            var token = GatewayTokenBox.Text?.Trim() ?? "";

            // Build a patch with ONLY the sections the user changed, so we touch nothing else.
            var patch = new JsonObject();

            var screenshotsChanged = screenshots != _loadedScreenshots;
            if (screenshotsChanged)
                patch["screenshots"] = new JsonObject { ["source_directory"] = screenshots };

            var gatewayChanged = url != _loadedGatewayUrl
                || advertised != _loadedGatewayAdvertised
                || token != _loadedGatewayToken;
            if (gatewayChanged)
            {
                patch["gateway"] = new JsonObject
                {
                    ["url"] = url,
                    ["token"] = token,
                    ["tailnetEndpoint"] = advertised,
                };
            }

            if (patch.Count == 0)
            {
                StatusText.Text = "No changes to save.";
                SaveButton.IsEnabled = true;
                return;
            }

            await Task.Run(() => CcDirectorConfigService.MergePatch(patch));
            FileLog.Write($"[SettingsDialog] BtnSave_Click: saved sections={patch.Count}, gatewayChanged={gatewayChanged}");

            // Re-register with the gateway live so a URL/endpoint/token change takes effect now.
            if (gatewayChanged && _reapplyGateway is not null)
            {
                await _reapplyGateway();
                FileLog.Write("[SettingsDialog] BtnSave_Click: gateway re-applied");
            }

            // Re-point the screenshots tab so a new folder takes effect without restarting.
            if (screenshotsChanged && _reloadScreenshots is not null)
            {
                await _reloadScreenshots();
                FileLog.Write("[SettingsDialog] BtnSave_Click: screenshots panel reloaded");
            }

            // Update the loaded baseline + raw view to reflect what's now on disk.
            _loadedScreenshots = screenshots;
            _loadedGatewayUrl = url;
            _loadedGatewayAdvertised = advertised;
            _loadedGatewayToken = token;
            RawConfigBox.Text = await Task.Run(() =>
                CcDirectorConfigService.ReadRaw().ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            StatusText.Text = (gatewayChanged, screenshotsChanged) switch
            {
                (true, true) => "Saved. Gateway re-applied and screenshots tab reloaded.",
                (true, false) => "Saved. Gateway re-applied.",
                (false, true) => "Saved. Screenshots tab reloaded.",
                _ => "Saved.",
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnSave_Click FAILED: {ex.Message}");
            StatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Probe the entered gateway URL's /healthz and report what answered, so the user can
    /// confirm the URL is right before saving. Reachability only - it does not prove the
    /// gateway can call back to this Director (that is what the public URL is for).
    /// </summary>
    private async void BtnTestGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnTestGateway_Click");
        var url = GatewayUrlBox.Text?.Trim() ?? "";
        if (url.Length == 0)
        {
            ShowGatewayStatus("Enter a gateway URL first.", error: true);
            return;
        }

        TestGatewayButton.IsEnabled = false;
        ShowGatewayStatus($"Testing {url} ...", error: false);
        try
        {
            var (ok, message) = await ProbeGatewayAsync(url);
            ShowGatewayStatus(message, error: !ok);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnTestGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Cannot reach {url}: {ex.Message}", error: true);
        }
        finally
        {
            TestGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Find the gateway by probing /healthz: first on this machine (loopback), then across the
    /// tailnet - every online, non-mobile Tailscale device at the conventional gateway port.
    /// Fills the URL box with the first one that answers like a gateway.
    /// </summary>
    private async void BtnDetectGateway_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectGateway_Click");
        DetectGatewayButton.IsEnabled = false;
        try
        {
            // 1. Tailscale first: scan every online, non-mobile device (Self included) by its
            //    MagicDNS name, in parallel. Self-first ordering means a co-located gateway is
            //    found by its Tailscale NAME (e.g. http://soren-north...:7878), not localhost -
            //    a name-based URL works identically from this machine and from a Mac/phone.
            var hosts = await Task.Run(() => TailscaleIdentity.ListGatewayHostCandidates());
            if (hosts.Count > 0)
            {
                ShowGatewayStatus($"Scanning {hosts.Count} Tailscale device(s) for a gateway ...", error: false);
                var urls = hosts.Select(h => $"http://{h}:{EndpointProbe.DefaultGatewayPort}").ToList();
                var found = await ProbeFirstReachableAsync(urls);
                if (found is not null)
                {
                    GatewayUrlBox.Text = found;
                    ShowGatewayStatus($"Found a gateway at {found}. Click Save to connect.", error: false);
                    FileLog.Write($"[SettingsDialog] BtnDetectGateway_Click: found tailnet {found}");
                    return;
                }
            }

            // 2. Fallback - no Tailscale identity, or no device answered: probe this machine's
            //    loopback for a gateway that only binds locally.
            ShowGatewayStatus("No gateway on the tailnet; checking this machine ...", error: false);
            foreach (var candidate in EndpointProbe.LocalGatewayCandidates())
            {
                var (ok, _) = await ProbeGatewayAsync(candidate);
                if (ok)
                {
                    GatewayUrlBox.Text = candidate;
                    ShowGatewayStatus($"Found a gateway at {candidate}. Click Save to connect.", error: false);
                    FileLog.Write($"[SettingsDialog] BtnDetectGateway_Click: found local {candidate}");
                    return;
                }
            }

            ShowGatewayStatus("No gateway found on the tailnet or this machine. Enter the gateway URL above.", error: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectGateway_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectGatewayButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Probe all base URLs concurrently and return the first (in input order) that answers like
    /// a gateway, or null if none do. WhenAll preserves order so the result mirrors the device
    /// priority (Self first).
    /// </summary>
    private static async Task<string?> ProbeFirstReachableAsync(IReadOnlyList<string> baseUrls)
    {
        var results = await Task.WhenAll(baseUrls.Select(async url =>
        {
            try { return (url, (await ProbeGatewayAsync(url)).Ok); }
            catch { return (url, false); }
        }));
        return results.FirstOrDefault(r => r.Item2).url;
    }

    /// <summary>
    /// Fill the Director public URL from this machine's best reachable network address plus
    /// the live control port. This is the field a remote gateway calls back to; the default
    /// is loopback, which a gateway on another machine cannot reach - the reason a Mac
    /// Director never shows up until this is set.
    /// </summary>
    private async void BtnDetectPublicUrl_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnDetectPublicUrl_Click");
        DetectPublicUrlButton.IsEnabled = false;
        try
        {
            if (_directorPort <= 0)
            {
                ShowGatewayStatus("This Director's control port is not known yet; cannot detect the public URL.", error: true);
                return;
            }

            ShowGatewayStatus("Detecting this machine's address ...", error: false);

            // Prefer the Tailscale MagicDNS name (matches how the Director registers, and is what
            // TLS certs are issued for); fall back to the best local IP. Both touch the network /
            // shell the tailscale CLI, so run off the UI thread.
            var (host, kind) = await Task.Run(() =>
            {
                var dnsName = TailscaleIdentity.TryGetMagicDnsName();
                if (!string.IsNullOrWhiteSpace(dnsName))
                    return (dnsName!, "Tailscale name");

                var address = EndpointProbe.BestLocalAddress();
                if (address is null)
                    return ((string?)null, "");

                var k = EndpointProbe.IsTailscaleAddress(address) ? "Tailscale IP" : "LAN IP";
                return (address.ToString(), k);
            });

            if (host is null)
            {
                ShowGatewayStatus("No Tailscale identity or reachable network address found. Enter the public URL manually.", error: true);
                return;
            }

            var url = EndpointProbe.BuildAdvertisedUrl(host, _directorPort);
            GatewayAdvertisedBox.Text = url;
            ShowGatewayStatus($"Detected {url} ({kind}). Click Save to apply.", error: false);
            FileLog.Write($"[SettingsDialog] BtnDetectPublicUrl_Click: {url} ({kind})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SettingsDialog] BtnDetectPublicUrl_Click FAILED: {ex.Message}");
            ShowGatewayStatus($"Detection failed: {ex.Message}", error: true);
        }
        finally
        {
            DetectPublicUrlButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// GET {baseUrl}/healthz and summarize the answer. Returns (ok, human-readable message).
    /// Treats any non-2xx or non-gateway response as a failure with a clear reason.
    /// </summary>
    private static async Task<(bool Ok, string Message)> ProbeGatewayAsync(string baseUrl)
    {
        var healthz = baseUrl.TrimEnd('/') + "/healthz";
        var resp = await _probeHttp.GetAsync(healthz);
        if (!resp.IsSuccessStatusCode)
            return (false, $"Gateway returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {healthz}.");

        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = GetStringProp(root, "status");
            if (status is null)
                return (false, $"{baseUrl} answered but does not look like a CC Director gateway.");

            var version = GetStringProp(root, "version") ?? "?";
            var directors = GetIntProp(root, "directors");
            var sessions = GetIntProp(root, "sessions");
            return (true, $"OK: gateway v{version}, {directors} director(s), {sessions} session(s).");
        }
        catch (JsonException)
        {
            return (false, $"{baseUrl} answered but the response was not valid gateway JSON.");
        }
    }

    private static string? GetStringProp(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static int GetIntProp(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                return p.Value.GetInt32();
        return 0;
    }

    private void ShowGatewayStatus(string text, bool error)
    {
        GatewayTestStatus.Text = text;
        GatewayTestStatus.IsVisible = true;
        GatewayTestStatus.Foreground = error
            ? global::Avalonia.Media.Brushes.IndianRed
            : global::Avalonia.Media.Brushes.MediumSeaGreen;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[SettingsDialog] BtnClose_Click: closing");
        Close();
    }
}
