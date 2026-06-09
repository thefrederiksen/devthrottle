using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

public partial class HelpDialog : Window
{
    // Built once in PopulateAboutSection from the same rows shown on screen, so the
    // clipboard text and the visible values can never drift apart.
    private string _copyText = "";

    public HelpDialog()
    {
        FileLog.Write("[HelpDialog] Constructor: initializing");
        InitializeComponent();
        PopulateAboutSection();
    }

    /// <summary>
    /// Fill the "ABOUT THIS INSTANCE" panel with the data the Gateway uses to locate
    /// this Director (id, control endpoint, pid, machine, instance file) and stash the
    /// same data as plain text for the "Copy info" button.
    /// </summary>
    private void PopulateAboutSection()
    {
        var rows = BuildInfoRows();

        var sb = new StringBuilder();
        sb.AppendLine("CC Director - instance information");
        foreach (var (label, value) in rows)
        {
            AboutPanel.Children.Add(MakeRow(label, value));
            sb.AppendLine($"{label}: {value}");
        }
        _copyText = sb.ToString().TrimEnd();
    }

    private static List<(string Label, string Value)> BuildInfoRows()
    {
        var app = global::Avalonia.Application.Current as App;
        var host = app?.ControlApiHost;

        var version = AppVersion.Display;
        var processPath = Environment.ProcessPath ?? "(unknown)";

        // DirectorId / Port / endpoint / instance file only exist once the Control API
        // host has started. It always has by the time this dialog is reachable, but if it
        // somehow has not, say so plainly rather than print a misleading value.
        string directorId, controlEndpoint, instanceFile;
        if (host is null)
        {
            directorId = "(control API not started)";
            controlEndpoint = "(control API not started)";
            instanceFile = "(control API not started)";
        }
        else
        {
            directorId = host.DirectorId;
            controlEndpoint = host.Port > 0 ? $"http://127.0.0.1:{host.Port}" : "(no port)";
            instanceFile = Path.Combine(InstanceRegistration.InstancesDirectory, $"{host.DirectorId}.json");
        }

        var rows = new List<(string, string)>
        {
            ("Version", version),
            ("CC Director ID", directorId),
            ("Process ID (PID)", Environment.ProcessId.ToString()),
            ("Process name", Path.GetFileName(processPath)),
            ("Location", processPath),
            ("Control endpoint", controlEndpoint),
            ("Machine", Environment.MachineName),
            ("User", Environment.UserName),
            ("Instance file", instanceFile),
        };

        // Deployment + connection diagnostics: which build this is, where it lives, the Gateway it
        // talks to (the URL), and the versions of every installed component.
        if (AboutInfo.BuildDate() is { } built)
            rows.Add(("Build date", built.ToString("yyyy-MM-dd HH:mm:ss")));
        rows.Add(("Install root", AboutInfo.InstallRoot));

        var gateway = GatewayConfig.Load();
        rows.Add(("Gateway", gateway.IsEnabled ? gateway.Url : "(standalone - no gateway configured)"));

        foreach (var kv in AboutInfo.InstalledComponents().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(($"Installed: {kv.Key}", kv.Value));

        return rows;
    }

    private static Control MakeRow(string label, string value)
    {
        var grid = new Grid
        {
            Margin = new global::Avalonia.Thickness(0, 0, 0, 6),
            ColumnDefinitions = new ColumnDefinitions("150,*"),
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            FontSize = 12,
        };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private async void BtnCopyInfo_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HelpDialog] BtnCopyInfo_Click: copying instance info to clipboard");
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            FileLog.Write("[HelpDialog] BtnCopyInfo_Click: no clipboard available");
            return;
        }

        await clipboard.SetTextAsync(_copyText);
        if (sender is Button btn)
            btn.Content = "Copied";
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[HelpDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
