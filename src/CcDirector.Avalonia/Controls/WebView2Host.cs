using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Web.WebView2.Core;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Avalonia control that hosts a Microsoft WebView2 (Chromium/Edge) browser
/// directly on Windows. Replaces the WebView.Avalonia wrapper packages which
/// were found to silently not initialize on this codebase (toolbar renders,
/// WebView area stays solid black, zero msedgewebview2.exe children ever
/// spawn). This control wraps Microsoft.Web.WebView2.Core directly --
/// CoreWebView2Environment + CoreWebView2Controller -- the same API
/// mindzieStudioDesktop uses successfully.
///
/// Windows-only. On macOS/Linux the control attaches but does not render
/// content -- a future cross-platform fallback would call into WKWebView /
/// WebKitGTK. The cc-director Windows experience is what we are fixing.
///
/// Lifecycle: when Avalonia attaches us to the visual tree, NativeControlHost
/// gives us a parent HWND. We async-create the WebView2 environment and
/// controller; the controller manages its own native window. When our bounds
/// change in Avalonia we resize the controller. On detach we dispose.
/// </summary>
public class WebView2Host : NativeControlHost
{
    private CoreWebView2Controller? _controller;
    private IntPtr _parentHwnd;
    private string? _pendingUrl;
    private string? _pendingHtml;
    private bool _initStarted;

    /// <summary>Raised on the UI thread once CoreWebView2 is ready.</summary>
    public event Action? CoreReady;

    /// <summary>Raised on the UI thread when a navigation finishes (success or failure).</summary>
    public event Action<bool>? NavigationCompleted;

    public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;

    public void Navigate(string url)
    {
        if (_controller?.CoreWebView2 is { } cw)
        {
            FileLog.Write($"[WebView2Host] Navigate: {url}");
            cw.Navigate(url);
        }
        else
        {
            _pendingUrl = url;
            _pendingHtml = null;
            FileLog.Write($"[WebView2Host] Navigate queued (not ready): {url}");
        }
    }

    public void NavigateToString(string html)
    {
        if (_controller?.CoreWebView2 is { } cw)
        {
            FileLog.Write($"[WebView2Host] NavigateToString: length={html.Length}");
            cw.NavigateToString(html);
        }
        else
        {
            _pendingHtml = html;
            _pendingUrl = null;
            FileLog.Write($"[WebView2Host] NavigateToString queued (not ready): length={html.Length}");
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _parentHwnd = parent.Handle;
        FileLog.Write($"[WebView2Host] CreateNativeControlCore: parentHwnd=0x{_parentHwnd.ToInt64():X}");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            FileLog.Write("[WebView2Host] non-Windows platform: returning base handle (no WebView2)");
            return base.CreateNativeControlCore(parent);
        }

        _ = StartInitializationAsync();
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        FileLog.Write("[WebView2Host] DestroyNativeControlCore");
        try { _controller?.Close(); } catch (Exception ex) { FileLog.Write($"[WebView2Host] controller.Close FAILED: {ex.Message}"); }
        _controller = null;
        base.DestroyNativeControlCore(control);
    }

    private async Task StartInitializationAsync()
    {
        if (_initStarted) return;
        _initStarted = true;

        try
        {
            var userDataFolder = ResolveUserDataFolder();
            Directory.CreateDirectory(userDataFolder);
            FileLog.Write($"[WebView2Host] CoreWebView2Environment.CreateAsync start (userDataFolder={userDataFolder})");
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);
            FileLog.Write("[WebView2Host] CoreWebView2Environment.CreateAsync done");

            FileLog.Write($"[WebView2Host] CreateCoreWebView2ControllerAsync start (parent=0x{_parentHwnd.ToInt64():X})");
            _controller = await env.CreateCoreWebView2ControllerAsync(_parentHwnd);
            FileLog.Write("[WebView2Host] CreateCoreWebView2ControllerAsync done");

            ApplyBoundsToController();

            _controller.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                FileLog.Write($"[WebView2Host] NavigationCompleted IsSuccess={e.IsSuccess} status={e.HttpStatusCode}");
                NavigationCompleted?.Invoke(e.IsSuccess);
            };

            CoreReady?.Invoke();

            if (_pendingUrl is { } u) { _pendingUrl = null; Navigate(u); }
            else if (_pendingHtml is { } h) { _pendingHtml = null; NavigateToString(h); }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebView2Host] init FAILED: {ex.Message}");
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        ApplyBoundsToController();
        return result;
    }

    /// <summary>
    /// Per-instance WebView2 user-data folder under cc-director's standard
    /// state root. WebView2 exclusively locks its user-data folder, so each
    /// running Director instance needs its own. Keyed by the running exe's
    /// filename, giving:
    ///   %LOCALAPPDATA%\cc-director\webview2\cc-director-avalonia1\
    ///   %LOCALAPPDATA%\cc-director\webview2\cc-director-avalonia2\
    ///   %LOCALAPPDATA%\cc-director\webview2\cc-director\           (production)
    /// Default location (next to the exe) is rejected because it pollutes
    /// the install/build directory with hundreds of MB of Chromium cache.
    /// </summary>
    private static string ResolveUserDataFolder()
    {
        var exePath = Environment.ProcessPath ?? "cc-director";
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(exeName)) exeName = "cc-director";
        return Path.Combine(CcStorage.Root(), "webview2", exeName);
    }

    private void ApplyBoundsToController()
    {
        if (_controller is null) return;
        var b = Bounds;
        var rect = new global::System.Drawing.Rectangle(0, 0, (int)b.Width, (int)b.Height);
        try
        {
            _controller.Bounds = rect;
            _controller.IsVisible = b.Width > 0 && b.Height > 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebView2Host] ApplyBoundsToController FAILED: {ex.Message}");
        }
    }
}
