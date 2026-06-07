using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The Gateway-connectivity troubleshooter (issue #223): runs the two-way handshake, shows
/// the per-leg verdict, then walks the diagnostic ladder in the correct order and reports
/// the FIRST failing rung with its exact fix. Opens instantly with a "running" state and
/// fills in live (responsive-UI rule). Opened from the sidebar indicator (#224) and
/// auto-popped once per distinct failure after the startup self-test.
/// </summary>
public partial class GatewayTroubleshootDialog : Window
{
    private readonly ControlApiHost? _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LadderRung> _completedRungs = new();
    private string _verdictReportLine = "";
    private bool _running;

    public GatewayTroubleshootDialog()
    {
        InitializeComponent();
    }

    public GatewayTroubleshootDialog(ControlApiHost host) : this()
    {
        _host = host;
        Loaded += (_, _) => _ = RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    // ==================== The run ====================

    private async Task RunAsync()
    {
        if (_host is null || _running) return;
        _running = true;
        BtnRetest.IsEnabled = false;
        try
        {
            FileLog.Write("[GatewayTroubleshootDialog] RunAsync: starting handshake + ladder");
            ShowVerdictNeutral("Running two-way handshake...");
            _completedRungs.Clear();
            RungsPanel.Children.Clear();
            RungsPanel.Children.Add(new TextBlock
            {
                Text = "Running checks...",
                Foreground = Brush.Parse("#888888"),
                FontSize = 12,
                FontStyle = FontStyle.Italic,
                Margin = new global::Avalonia.Thickness(4, 4, 0, 0),
            });

            // 1) The handshake itself - the protocol verdict drives the colored block.
            await _host.VerifyGatewayNowAsync(_cts.Token);
            ShowVerdictFromMonitor();

            // 2) The ladder. Config + advertised endpoint resolved off the UI thread
            //    (GatewayConfig reads disk; the endpoint may shell the tailscale CLI).
            var monitor = _host.GatewayMonitor;
            var port = _host.Port;
            var (gatewayUrl, endpoint) = await Task.Run(() =>
            {
                var cfg = GatewayConfig.Load();
                var ep = monitor.LastResult?.CallbackEndpoint;
                if (string.IsNullOrEmpty(ep))
                    ep = TailscaleIdentity.TryGetMagicDnsName() is { } dns
                        ? $"https://{dns}:{port}"
                        : cfg.TailnetEndpoint;
                return (cfg.IsEnabled ? cfg.Url : null, ep);
            }, _cts.Token);

            var selfTest = new GatewayConnectivitySelfTest(
                port, _host.DirectorId, endpoint, gatewayUrl, _host.ServeProvisioner?.LastError);

            RungsPanel.Children.Clear();
            var index = 0;
            await foreach (var rung in selfTest.RunAsync(_cts.Token))
            {
                index++;
                _completedRungs.Add(rung);
                RungsPanel.Children.Add(BuildRungRow(index, rung));
            }
            FileLog.Write($"[GatewayTroubleshootDialog] RunAsync complete: {_completedRungs.Count} rungs");
        }
        catch (OperationCanceledException)
        {
            // Dialog closed mid-run - nothing to update.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTroubleshootDialog] RunAsync FAILED: {ex.Message}");
            ShowVerdictNeutral($"Troubleshooter failed to run: {ex.Message}");
        }
        finally
        {
            _running = false;
            if (!_cts.IsCancellationRequested)
                BtnRetest.IsEnabled = true;
        }
    }

    // ==================== Verdict block ====================

    private void ShowVerdictNeutral(string text)
    {
        VerdictBorder.Background = Brush.Parse("#2A2A2A");
        VerdictBorder.BorderBrush = Brush.Parse("#3C3C3C");
        VerdictText.Foreground = Brush.Parse("#CCCCCC");
        VerdictText.Text = text;
        VerdictLegs.IsVisible = false;
        _verdictReportLine = text;
    }

    private void ShowVerdictFromMonitor()
    {
        if (_host is null) return;
        var m = _host.GatewayMonitor;
        var result = m.LastResult;

        switch (m.Status)
        {
            case GatewayConnectionStatus.Verified:
                VerdictBorder.Background = Brush.Parse("#1B3A2A");
                VerdictBorder.BorderBrush = Brush.Parse("#22C55E");
                VerdictText.Foreground = Brush.Parse("#22C55E");
                VerdictText.Text = "Two-way verification PASSED - the Gateway and this Director can each reach the other.";
                VerdictLegs.Text = result is null
                    ? ""
                    : $"Leg 1 (this Director -> Gateway): OK   |   Leg 2 (Gateway -> this Director): OK, {result.CallbackLatencyMs} ms at {result.CallbackEndpoint}";
                VerdictLegs.IsVisible = result is not null;
                break;

            case GatewayConnectionStatus.Failed:
                VerdictBorder.Background = Brush.Parse("#3A1B1B");
                VerdictBorder.BorderBrush = Brush.Parse("#DC2626");
                VerdictText.Foreground = Brush.Parse("#EF4444");
                VerdictText.Text = $"Two-way verification FAILED - {m.FailureSummary}";
                var legs = result is null
                    ? "Leg 1 (this Director -> Gateway): FAILED - the verify request itself never round-tripped."
                    : $"Leg 1 (this Director -> Gateway): OK   |   Leg 2 (Gateway -> this Director): {(result.CallbackOk ? "OK" : "FAILED")}";
                if (m.LastVerifiedAt is { } at)
                    legs += $"   |   was verified until {at.ToLocalTime():HH:mm:ss}";
                VerdictLegs.Text = legs;
                VerdictLegs.IsVisible = true;
                break;

            case GatewayConnectionStatus.Connecting:
                ShowVerdictNeutral("Still verifying - registration or handshake in flight. Re-test in a few seconds.");
                return;

            default:
                ShowVerdictNeutral("No gateway.url configured - this Director runs local-only. Configure a Gateway in Settings to connect it.");
                return;
        }
        _verdictReportLine = VerdictText.Text + (VerdictLegs.IsVisible ? Environment.NewLine + VerdictLegs.Text : "");
    }

    // ==================== Rung rows ====================

    private Control BuildRungRow(int index, LadderRung rung)
    {
        var (mark, markColor) = rung.Status switch
        {
            RungStatus.Pass => ("OK", "#22C55E"),
            RungStatus.Fail => ("X", "#EF4444"),
            RungStatus.Info => ("i", "#3B82F6"),
            _ => ("-", "#666666"),
        };
        var dim = rung.Status == RungStatus.Skipped;

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"{index}. {rung.Title}",
            Foreground = Brush.Parse(dim ? "#666666" : "#CCCCCC"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = rung.Found,
            Foreground = Brush.Parse(dim ? "#666666" : "#999999"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new global::Avalonia.Thickness(0, 2, 0, 0),
        });

        if (rung.Fix is { } fix)
        {
            body.Children.Add(new Border
            {
                Background = Brush.Parse("#1E1E1E"),
                BorderBrush = Brush.Parse("#3C3C3C"),
                BorderThickness = new global::Avalonia.Thickness(1),
                CornerRadius = new global::Avalonia.CornerRadius(3),
                Padding = new global::Avalonia.Thickness(8, 5),
                Margin = new global::Avalonia.Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = fix,
                    Foreground = Brush.Parse("#CCCCCC"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                    TextWrapping = TextWrapping.Wrap,
                },
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new global::Avalonia.Thickness(0, 8, 0, 0),
            };
            if (rung.CanAutoFix && _host?.ServeProvisioner is not null)
            {
                var fixBtn = new Button
                {
                    Content = "Fix it now",
                    Height = 26,
                    Padding = new global::Avalonia.Thickness(12, 0),
                    Background = Brush.Parse("#007ACC"),
                    Foreground = Brushes.White,
                    BorderThickness = new global::Avalonia.Thickness(0),
                    Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
                };
                fixBtn.Click += (_, _) => _ = FixServeMappingAsync(fixBtn);
                buttons.Children.Add(fixBtn);
            }
            var copyBtn = new Button
            {
                Content = "Copy command",
                Height = 26,
                Padding = new global::Avalonia.Thickness(12, 0),
                Background = Brush.Parse("#3C3C3C"),
                Foreground = Brush.Parse("#CCCCCC"),
                BorderThickness = new global::Avalonia.Thickness(0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            copyBtn.Click += async (_, _) =>
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard is not null) await clipboard.SetTextAsync(fix);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayTroubleshootDialog] Copy command FAILED: {ex.Message}");
                }
            };
            buttons.Children.Add(copyBtn);
            body.Children.Add(buttons);
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*"),
            Margin = new global::Avalonia.Thickness(4, 0, 4, 0),
        };
        var markBlock = new TextBlock
        {
            Text = mark,
            Foreground = Brush.Parse(markColor),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new global::Avalonia.Thickness(0, 1, 0, 0),
        };
        Grid.SetColumn(markBlock, 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(markBlock);
        row.Children.Add(body);

        return new Border
        {
            BorderBrush = Brush.Parse("#2E2E2E"),
            BorderThickness = new global::Avalonia.Thickness(0, 0, 0, 1),
            Padding = new global::Avalonia.Thickness(0, 8),
            Child = row,
        };
    }

    /// <summary>"Fix it now" on the serve-mapping rung: run the self-provisioner's own
    /// EnsureMapping (the same command the Fix box shows), then re-run everything.</summary>
    private async Task FixServeMappingAsync(Button fixBtn)
    {
        var provisioner = _host?.ServeProvisioner;
        if (provisioner is null) return;
        try
        {
            FileLog.Write("[GatewayTroubleshootDialog] Fix it now: asserting serve mapping");
            fixBtn.IsEnabled = false;
            fixBtn.Content = "Fixing...";
            var (ok, error) = await provisioner.EnsureMappingAsync(_cts.Token);
            FileLog.Write($"[GatewayTroubleshootDialog] Fix it now: ok={ok}{(ok ? "" : $", error={error}")}");
            await RunAsync(); // full re-test proves (or disproves) the fix
        }
        catch (OperationCanceledException)
        {
            // Dialog closed mid-fix.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTroubleshootDialog] Fix it now FAILED: {ex.Message}");
            fixBtn.IsEnabled = true;
            fixBtn.Content = "Fix it now";
        }
    }

    // ==================== Buttons ====================

    private void BtnRetest_Click(object? sender, RoutedEventArgs e)
    {
        _ = RunAsync();
    }

    private async void BtnCopyReport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Gateway connection troubleshooter report");
            sb.AppendLine($"Director: {_host?.DirectorId ?? "?"} on {Environment.MachineName}, port {_host?.Port}, {AppVersion.Display}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine(_verdictReportLine);
            sb.AppendLine();
            var i = 0;
            foreach (var rung in _completedRungs)
            {
                i++;
                sb.AppendLine($"[{rung.Status.ToString().ToUpperInvariant()}] {i}. {rung.Title}");
                sb.AppendLine($"    {rung.Found}");
                if (rung.Fix is { } fix) sb.AppendLine($"    Fix: {fix}");
            }
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(sb.ToString());
            FileLog.Write("[GatewayTroubleshootDialog] Report copied to clipboard");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTroubleshootDialog] BtnCopyReport FAILED: {ex.Message}");
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
