using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2ColdBootTest;

internal static class Program
{
    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    public static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), $"webview2-test-{Environment.ProcessId}.log");

    [STAThread]
    private static void Main(string[] args)
    {
        File.WriteAllText(LogPath, ""); // truncate
        Log($"START pid={Environment.ProcessId} mode={(args.Length > 0 ? args[0] : "load-html-then-exit")}");
        LogHostMemory("initial");

        ApplicationConfiguration.Initialize();

        var mode = args.Length > 0 ? args[0] : "load-html-then-exit";
        var htmlPath = args.Length > 1
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "test.html");

        if (!File.Exists(htmlPath))
            File.WriteAllText(htmlPath,
                "<!DOCTYPE html><html><head><title>Test</title></head>" +
                "<body><h1>WebView2 Cold Boot Test</h1><p>If you can read this, it loaded.</p></body></html>");

        var form = new TestForm(mode, htmlPath);
        Log($"form-constructed");

        Application.Run(form);

        Log($"END");
        WriteWebView2ProcessSummary("final");
    }

    public static void Log(string message)
    {
        var line = $"[{ProcessClock.ElapsedMilliseconds,6}ms] {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { /* ignore */ }
    }

    public static void LogHostMemory(string label)
    {
        var p = Process.GetCurrentProcess();
        Log($"memory[{label}] host: ws={p.WorkingSet64 / 1024 / 1024}MB private={p.PrivateMemorySize64 / 1024 / 1024}MB");
    }

    /// <summary>
    /// Sum memory across all msedgewebview2.exe processes on the machine that started AFTER us.
    /// Heuristic for our WebView2 tree without needing parent-pid lookups.
    /// </summary>
    public static void WriteWebView2ProcessSummary(string label)
    {
        var ourStart = Process.GetCurrentProcess().StartTime;
        long ws = 0, priv = 0;
        int count = 0;
        foreach (var p in Process.GetProcessesByName("msedgewebview2"))
        {
            try
            {
                if (p.StartTime < ourStart) continue;
                ws += p.WorkingSet64;
                priv += p.PrivateMemorySize64;
                count++;
            }
            catch { /* dead or access-denied */ }
        }
        Log($"webview2-procs[{label}] count={count} ws={ws / 1024 / 1024}MB private={priv / 1024 / 1024}MB");
    }
}

internal sealed class TestForm : Form
{
    private readonly string _mode;
    private readonly string _htmlPath;
    private WebView2 _webView = null!;

    public TestForm(string mode, string htmlPath)
    {
        _mode = mode;
        _htmlPath = htmlPath;
        Text = "WebView2 Cold Boot Test";
        Width = 900;
        Height = 700;
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        Program.Log("form-loaded");

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        Program.Log("webview-control-added-to-form");

        _webView.CoreWebView2InitializationCompleted += (_, args) =>
        {
            Program.Log($"CoreWebView2InitializationCompleted success={args.IsSuccess} exception={args.InitializationException?.Message ?? "<none>"}");
            Program.LogHostMemory("after-corewebview2-init");
            Program.WriteWebView2ProcessSummary("after-corewebview2-init");
        };

        _webView.NavigationStarting += (_, args) =>
        {
            Program.Log($"NavigationStarting uri={args.Uri}");
        };

        _webView.NavigationCompleted += (_, args) =>
        {
            Program.Log($"NavigationCompleted success={args.IsSuccess} status={args.HttpStatusCode}");
            Program.LogHostMemory("after-navigation-completed");
            Program.WriteWebView2ProcessSummary("after-navigation-completed");

            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Program.LogHostMemory("2s-after-nav");
                Program.WriteWebView2ProcessSummary("2s-after-nav");
                if (_mode == "load-html-then-exit")
                {
                    Program.Log("auto-exit");
                    Close();
                }
            };
            t.Start();
        };

        try
        {
            Program.Log("EnsureCoreWebView2Async:start");
            await _webView.EnsureCoreWebView2Async();
            Program.Log("EnsureCoreWebView2Async:returned");

            if (_mode == "init-only" || _mode == "init-only-then-exit")
            {
                Program.LogHostMemory("init-only-done");
                Program.WriteWebView2ProcessSummary("init-only-done");
                if (_mode == "init-only-then-exit")
                {
                    var t = new System.Windows.Forms.Timer { Interval = 2000 };
                    t.Tick += (_, _) =>
                    {
                        t.Stop();
                        Program.LogHostMemory("init-only-2s-later");
                        Program.WriteWebView2ProcessSummary("init-only-2s-later");
                        Close();
                    };
                    t.Start();
                }
                return;
            }

            var uri = new Uri(_htmlPath);
            Program.Log($"about-to-set-Source to {uri.AbsoluteUri}");
            _webView.Source = uri;
        }
        catch (Exception ex)
        {
            Program.Log($"ERROR: {ex.Message}");
        }
    }
}
