using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Web.WebView2.Core;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Avalonia control that hosts a Microsoft WebView2 (Chromium/Edge) browser on Windows.
///
/// IMPORTANT -- why this is an overlay window, not a NativeControlHost:
/// Avalonia's renderer composites the whole top-level window via DirectComposition
/// (WinUIComposition). A WebView2 child window embedded with NativeControlHost lives
/// inside that composited surface and is occluded by it -- navigation succeeds and the
/// page renders (CapturePreviewAsync returns real content) but nothing ever paints on
/// screen: the control shows solid black. This is the well-known Avalonia "airspace"
/// limitation for native child controls.
///
/// To avoid the airspace conflict, the WebView2 is hosted in its own plain Win32 top-level
/// popup window OWNED by the Avalonia top-level (so it stays above it, minimizes/restores
/// with it, and is destroyed with it). There is no Avalonia compositor on that window to
/// occlude the WebView2. We continuously track this control's on-screen rectangle and
/// move/size the popup to match, and hide it whenever this control is not effectively
/// visible (another document tab is active, the Source/raw view is showing, the owner
/// window is minimized, etc).
///
/// Lifecycle: the WebView2 + overlay are created lazily on first attach and kept alive
/// across detach/re-attach (the document-tab UI removes and re-adds viewers when switching
/// tabs). They are torn down once, when the owner window closes.
///
/// Windows-only. On other platforms the control is inert.
/// </summary>
public class WebView2Host : Control
{
    private CoreWebView2Controller? _controller;
    private IntPtr _hostHwnd;
    private IntPtr _ownerHwnd;
    private Window? _ownerWindow;
    private string? _pendingUrl;
    private string? _pendingHtml;
    private bool _initStarted;
    private bool _torndown;

    /// <summary>Raised on the UI thread once CoreWebView2 is ready.</summary>
    public event Action? CoreReady;

    /// <summary>Raised on the UI thread when a navigation finishes (success or failure).</summary>
    public event Action<bool>? NavigationCompleted;

    public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;

    // The area behind the overlay; visible only briefly before the WebView2 paints.
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

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

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        base.Render(context);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_torndown || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // First attach: discover the owner window, create the overlay, start WebView2.
        if (_hostHwnd == IntPtr.Zero)
        {
            _ownerWindow = this.GetVisualRoot() as Window;
            _ownerHwnd = _ownerWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            FileLog.Write($"[WebView2Host] OnAttachedToVisualTree (first): ownerHwnd=0x{_ownerHwnd.ToInt64():X}");
            if (_ownerHwnd == IntPtr.Zero)
                return;

            _hostHwnd = CreateOverlayWindow(_ownerHwnd);
            FileLog.Write($"[WebView2Host] overlay hwnd=0x{_hostHwnd.ToInt64():X}");
            if (_ownerWindow != null)
                _ownerWindow.Closed += OnOwnerClosed;

            _ = StartInitializationAsync();
        }

        // (Re)subscribe geometry/visibility tracking. These are detached in OnDetached.
        LayoutUpdated += OnLayoutUpdated;
        PropertyChanged += OnControlPropertyChanged;
        if (_ownerWindow != null)
        {
            _ownerWindow.PositionChanged += OnOwnerPositionChanged;
            _ownerWindow.PropertyChanged += OnOwnerPropertyChanged;
        }

        UpdateOverlay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FileLog.Write("[WebView2Host] OnDetachedFromVisualTree (hiding overlay, keeping alive)");
        LayoutUpdated -= OnLayoutUpdated;
        PropertyChanged -= OnControlPropertyChanged;
        if (_ownerWindow != null)
        {
            _ownerWindow.PositionChanged -= OnOwnerPositionChanged;
            _ownerWindow.PropertyChanged -= OnOwnerPropertyChanged;
        }

        // Keep the WebView2 alive (tab switches detach then re-attach us); just hide it.
        if (_hostHwnd != IntPtr.Zero) ShowWindow(_hostHwnd, SW_HIDE);
        if (_controller != null) _controller.IsVisible = false;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateOverlay();
    private void OnOwnerPositionChanged(object? sender, PixelPointEventArgs e) => UpdateOverlay();

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
            UpdateOverlay();
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
            UpdateOverlay();
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Teardown();

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

            if (_torndown || _hostHwnd == IntPtr.Zero) return;

            FileLog.Write($"[WebView2Host] CreateCoreWebView2ControllerAsync start (host=0x{_hostHwnd.ToInt64():X})");
            _controller = await env.CreateCoreWebView2ControllerAsync(_hostHwnd);
            FileLog.Write("[WebView2Host] CreateCoreWebView2ControllerAsync done");

            if (_torndown)
            {
                try { _controller.Close(); } catch { /* shutting down */ }
                _controller = null;
                return;
            }

            _controller.DefaultBackgroundColor = global::System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e);

            _controller.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                FileLog.Write($"[WebView2Host] NavigationCompleted IsSuccess={e.IsSuccess} status={e.HttpStatusCode}");
                NavigationCompleted?.Invoke(e.IsSuccess);
            };

            UpdateOverlay();
            CoreReady?.Invoke();

            if (_pendingUrl is { } u) { _pendingUrl = null; Navigate(u); }
            else if (_pendingHtml is { } h) { _pendingHtml = null; NavigateToString(h); }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebView2Host] init FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Match the overlay window (and the WebView2 controller bounds) to this control's
    /// on-screen rectangle, and show/hide it based on effective visibility.
    /// </summary>
    private void UpdateOverlay()
    {
        if (_torndown || _hostHwnd == IntPtr.Zero) return;

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var ownerMinimized = _ownerWindow?.WindowState == WindowState.Minimized;
        var visible = IsEffectivelyVisible && !ownerMinimized
                      && Bounds.Width > 0 && Bounds.Height > 0;

        if (!visible)
        {
            ShowWindow(_hostHwnd, SW_HIDE);
            if (_controller != null) _controller.IsVisible = false;
            return;
        }

        PixelPoint origin;
        try { origin = this.PointToScreen(new Point(0, 0)); }
        catch { return; } // not laid out yet

        var scale = top.RenderScaling;
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));

        SetWindowPos(_hostHwnd, IntPtr.Zero, origin.X, origin.Y, w, h,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);

        if (_controller != null)
        {
            _controller.RasterizationScale = scale;
            _controller.Bounds = new global::System.Drawing.Rectangle(0, 0, w, h);
            _controller.IsVisible = true;
        }
    }

    private void Teardown()
    {
        if (_torndown) return;
        _torndown = true;
        FileLog.Write("[WebView2Host] Teardown");

        if (_ownerWindow != null) _ownerWindow.Closed -= OnOwnerClosed;

        try { _controller?.Close(); }
        catch (Exception ex) { FileLog.Write($"[WebView2Host] controller.Close FAILED: {ex.Message}"); }
        _controller = null;

        if (_hostHwnd != IntPtr.Zero)
        {
            DestroyWindow(_hostHwnd);
            _hostHwnd = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Per-instance WebView2 user-data folder under cc-director's standard state root,
    /// keyed by the running exe's filename (WebView2 exclusively locks this folder, so
    /// each running Director instance needs its own).
    /// </summary>
    private static string ResolveUserDataFolder()
    {
        var exePath = Environment.ProcessPath ?? "cc-director";
        var exeName = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrWhiteSpace(exeName)) exeName = "cc-director";
        return Path.Combine(CcStorage.Root(), "webview2", exeName);
    }

    // ==================== Win32 overlay window ====================

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static ushort _classAtom;
    private static WndProc? _wndProcKeepAlive;
    private static readonly object ClassLock = new();

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr CreateOverlayWindow(IntPtr ownerHwnd)
    {
        EnsureWindowClass();
        var hwnd = CreateWindowExW(
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "CcDirectorWebViewOverlay",
            string.Empty,
            WS_POPUP,
            0, 0, 1, 1,
            ownerHwnd,           // owner: stays above, minimizes/closes with the owner
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        else
            FileLog.Write($"[WebView2Host] CreateWindowExW FAILED err={Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    private static void EnsureWindowClass()
    {
        lock (ClassLock)
        {
            if (_classAtom != 0) return;

            _wndProcKeepAlive = DefWindowProcW;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "CcDirectorWebViewOverlay",
            };
            _classAtom = RegisterClassExW(ref wc);
            if (_classAtom == 0)
                FileLog.Write($"[WebView2Host] RegisterClassExW FAILED err={Marshal.GetLastWin32Error()}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
