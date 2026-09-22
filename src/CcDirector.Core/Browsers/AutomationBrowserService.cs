using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The behavior layer over <see cref="AutomationBrowserRegistry"/>: it turns a registered
/// <see cref="AutomationBrowser"/> into a running, attachable Chromium instance and reports its live
/// status. Everything here honors the constraints we PROVED on a real machine:
///
///   * A drivable browser MUST use its OWN <c>--user-data-dir</c> and <c>--remote-debugging-port</c>.
///     Chrome 136+ refuses remote-debugging on the default profile, and App-Bound Encryption refuses
///     to carry a login into a copied profile - so we never touch a personal profile. Each browser is
///     its own folder, signed in ONCE by the human.
///   * Machine-local: the debug port is loopback, so only an agent on THIS machine can attach.
///
/// No-fallback rule: a request for a browser that is not installed, or that will not come up, THROWS
/// with a message naming the target. We never silently substitute another browser or swallow a failed
/// launch.
/// </summary>
public static class AutomationBrowserService
{
    // A single short-timeout client for the loopback control probes. Chrome answers /json/version
    // almost instantly when it is up; 2s is generous and keeps a "down" verdict snappy.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>How long we wait for a freshly launched browser's debug port to start answering.</summary>
    private static readonly TimeSpan LaunchReadyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The processes THIS Director launched, by browser id. Held for the Director's lifetime so a
    /// browser whose CDP connection wedges (up but unresponsive to <c>Browser.close</c>) can still be
    /// stopped by killing the exact process we started - no WMI, no command-line matching, and never a
    /// blanket kill by name. A browser launched by a previous Director has no entry here; for those the
    /// CDP close is the only stop path and a wedge is reported, not guessed at.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> LaunchedProcesses = new();

    /// <summary>
    /// Register AND provision a new browser: resolves the exe for <paramref name="kind"/> via
    /// <see cref="BrowserLauncher.DetectBrowsers"/> (THROWS if that browser is not installed), then
    /// records the entry (unique name, allocated port, dedicated directory). Does not launch it.
    /// </summary>
    public static AutomationBrowser Create(string name, BrowserKind kind)
    {
        FileLog.Write($"[AutomationBrowserService] Create: name={name}, kind={kind}");
        // Fail loudly and early if the requested browser is not installed - no point registering a
        // browser we could never launch.
        _ = ResolveInstalled(kind);
        return AutomationBrowserRegistry.Create(name, kind);
    }

    /// <summary>
    /// Launch the browser if it is not already up (idempotent), then wait for its debug port to answer.
    /// Returns the registry entry. THROWS if the exe is missing or the port never comes up.
    /// </summary>
    public static async Task<AutomationBrowser> LaunchAsync(string idOrName, CancellationToken ct = default)
    {
        var browser = AutomationBrowserRegistry.Get(idOrName);
        FileLog.Write($"[AutomationBrowserService] LaunchAsync: id={browser.Id}, port={browser.Port}");

        if (await IsUpAsync(browser, ct).ConfigureAwait(false))
        {
            FileLog.Write($"[AutomationBrowserService] LaunchAsync: id={browser.Id} already up, no-op");
            return browser;
        }

        StartProcess(browser, url: null);
        await WaitUntilUpAsync(browser, ct).ConfigureAwait(false);
        FileLog.Write($"[AutomationBrowserService] LaunchAsync: id={browser.Id} is up on {browser.Port}");
        return browser;
    }

    /// <summary>
    /// True when the browser's debug port answers <c>/json/version</c> with success. This is a probe of
    /// an external process, so a connection failure is the ANSWER ("not running"), not a hidden problem -
    /// hence the narrow catch. It is not a fallback: nothing is substituted, we simply report "down".
    /// </summary>
    public static async Task<bool> IsUpAsync(AutomationBrowser browser, CancellationToken ct = default)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        try
        {
            using var resp = await Http.GetAsync(VersionUrl(browser.Port), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Port refused / no answer within the timeout => the browser is not running.
            return false;
        }
    }

    /// <summary>Convenience overload: probe by id or name.</summary>
    public static Task<bool> IsUpAsync(string idOrName, CancellationToken ct = default)
        => IsUpAsync(AutomationBrowserRegistry.Get(idOrName), ct);

    /// <summary>The attach environment (<c>BU_NAME</c> / <c>BU_CDP_URL</c>) for this browser.</summary>
    public static AttachInfo AttachInfo(string idOrName)
        => AutomationBrowserRegistry.AttachInfoFor(AutomationBrowserRegistry.Get(idOrName));

    /// <summary>
    /// Start the one-time human sign-in: ensure the browser is up, then open its account page as a new
    /// tab. The sign-in itself is the HUMAN's hands - we NEVER automate credentials. The caller confirms
    /// completion separately via <see cref="MarkSignedIn"/>.
    /// </summary>
    public static async Task<AutomationBrowser> SignInAsync(string idOrName, CancellationToken ct = default)
    {
        var browser = await LaunchAsync(idOrName, ct).ConfigureAwait(false);
        FileLog.Write($"[AutomationBrowserService] SignInAsync: id={browser.Id} opening account page");
        StartProcess(browser, url: AccountUrl(browser.Kind));
        return browser;
    }

    /// <summary>Record that the human confirmed sign-in for this browser (sets LastSignedInUtc = now).</summary>
    public static AutomationBrowser MarkSignedIn(string idOrName)
        => AutomationBrowserRegistry.MarkSignedIn(idOrName);

    /// <summary>Rename a browser's label (id/port/directory unchanged).</summary>
    public static AutomationBrowser Rename(string idOrName, string newName)
        => AutomationBrowserRegistry.Rename(idOrName, newName);

    /// <summary>
    /// Fold the live status: <see cref="AutomationBrowserStatus.Stopped"/> when the port is down; otherwise
    /// <see cref="AutomationBrowserStatus.NeedsSignIn"/> when it has never been signed in, else
    /// <see cref="AutomationBrowserStatus.Ready"/>. Folded ONCE here so every client renders it verbatim.
    /// </summary>
    public static async Task<AutomationBrowserStatus> StatusAsync(AutomationBrowser browser, CancellationToken ct = default)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        if (!await IsUpAsync(browser, ct).ConfigureAwait(false))
            return AutomationBrowserStatus.Stopped;
        return browser.LastSignedInUtc is null
            ? AutomationBrowserStatus.NeedsSignIn
            : AutomationBrowserStatus.Ready;
    }

    /// <summary>Convenience overload: fold status by id or name.</summary>
    public static Task<AutomationBrowserStatus> StatusAsync(string idOrName, CancellationToken ct = default)
        => StatusAsync(AutomationBrowserRegistry.Get(idOrName), ct);

    /// <summary>
    /// The signed-in account this browser holds, read from the browser's OWN <c>Local State</c> (its
    /// dedicated user-data directory), or null when it has not been signed in yet. This is our own
    /// folder, so the profile's display name / account in <c>info_cache</c> is plainly readable - unlike
    /// a real personal profile's cookies, which App-Bound Encryption locks. Reused
    /// <see cref="BrowserLauncher.GetProfiles"/> so the parse lives in one place.
    /// </summary>
    public static string? ReadAccount(AutomationBrowser browser)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        var info = new BrowserInfo(browser.Kind, browser.Kind.ToString(), ExePathOrEmpty(browser.Kind), browser.UserDataDir);
        var profiles = BrowserLauncher.GetProfiles(info);
        return profiles.FirstOrDefault(p => p.Account is not null)?.Account;
    }

    private static string ExePathOrEmpty(BrowserKind kind)
        => BrowserLauncher.DetectBrowsers().FirstOrDefault(b => b.Kind == kind)?.ExePath ?? string.Empty;

    /// <summary>
    /// Stop the browser cleanly if it is running: Chrome DevTools Protocol <c>Browser.close</c> first
    /// (portable, works even for a browser a previous Director launched), then - only when the port
    /// stays up because the CDP connection is wedged - a <c>Process.Kill</c> of the exact process THIS
    /// Director launched (see <see cref="LaunchedProcesses"/>). A browser that is already down is a
    /// no-op. THROWS when the browser cannot be brought down at all (up, wedged, and not ours to kill),
    /// naming what to do - never a silent "probably stopped".
    /// </summary>
    public static async Task StopAsync(string idOrName, CancellationToken ct = default)
    {
        var browser = AutomationBrowserRegistry.Get(idOrName);
        FileLog.Write($"[AutomationBrowserService] StopAsync: id={browser.Id}, port={browser.Port}");
        if (!await TryShutDownAsync(browser, ct).ConfigureAwait(false))
            throw new TimeoutException(
                $"Browser \"{browser.Name}\" did not shut down: its debug port {browser.Port} is still answering " +
                "after a close request and it was not launched by this Director, so there is no process to stop. " +
                "Close the browser window by hand.");
    }

    /// <summary>
    /// Bring the browser down: CDP <c>Browser.close</c>, wait for the port to release, and if it stays
    /// up fall back to killing the tracked process this Director launched. Returns true when the port is
    /// down (including "was never up"), false when the browser is still answering after every path we
    /// legitimately have.
    /// </summary>
    private static async Task<bool> TryShutDownAsync(AutomationBrowser browser, CancellationToken ct)
    {
        await CloseViaCdpAsync(browser, ct).ConfigureAwait(false);
        if (await WaitUntilDownAsync(browser, ct).ConfigureAwait(false))
        {
            ForgetLaunchedProcess(browser.Id);
            return true;
        }

        // The port is still answering after Browser.close - the wedged-CDP case. Kill the exact
        // process we started, if this Director started it (tracked pid; never a kill by name).
        if (LaunchedProcesses.TryGetValue(browser.Id, out var process) && !process.HasExited)
        {
            FileLog.Write($"[AutomationBrowserService] TryShutDownAsync: id={browser.Id} CDP close did not release port {browser.Port}, killing tracked pid={process.Id}");
            process.Kill(entireProcessTree: true);
            if (await WaitUntilDownAsync(browser, ct).ConfigureAwait(false))
            {
                ForgetLaunchedProcess(browser.Id);
                return true;
            }
        }

        return false;
    }

    private static void ForgetLaunchedProcess(string browserId)
    {
        if (LaunchedProcesses.TryRemove(browserId, out var process))
            process.Dispose();
    }

    /// <summary>
    /// Remove a browser entirely: stop it (CDP close), wait for the port to release, delete its
    /// user-data directory, and drop its registry entry. THROWS if the directory cannot be deleted
    /// because the browser is still holding it (we surface that rather than leaving a half-removed
    /// browser) - the caller can retry after the process has fully exited.
    /// </summary>
    public static async Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        var browser = AutomationBrowserRegistry.Get(idOrName);
        FileLog.Write($"[AutomationBrowserService] RemoveAsync: id={browser.Id}");

        // A shutdown that fails here is not fatal on its own: the folder delete below throws the
        // actionable error if the browser is genuinely still holding its directory.
        if (!await TryShutDownAsync(browser, ct).ConfigureAwait(false))
            FileLog.Write($"[AutomationBrowserService] RemoveAsync: id={browser.Id} still up after shutdown attempts; the folder delete will report if it is locked");

        if (Directory.Exists(browser.UserDataDir))
        {
            try
            {
                Directory.Delete(browser.UserDataDir, recursive: true);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not delete the folder for browser \"{browser.Name}\" at {browser.UserDataDir} - it may still be running. Close it and try again. ({ex.Message})", ex);
            }
        }

        AutomationBrowserRegistry.RemoveEntry(browser.Id);
        FileLog.Write($"[AutomationBrowserService] RemoveAsync: id={browser.Id} removed");
    }

    // --- internals ---

    /// <summary>Resolve the installed <see cref="BrowserInfo"/> for a kind, or THROW naming the browser.</summary>
    private static BrowserInfo ResolveInstalled(BrowserKind kind)
    {
        // A launch is explicit and rare: read the disk now, so a browser installed or removed since
        // the last cached probe is seen at the moment it matters.
        var found = BrowserLauncher.DetectBrowsers(forceProbe: true).FirstOrDefault(b => b.Kind == kind);
        if (found is null)
            throw new InvalidOperationException(
                $"{kind} is not installed on this machine, so a {kind} automation browser cannot be created.");
        return found;
    }

    /// <summary>
    /// Launch (or, when <paramref name="url"/> is set, open a tab in) the browser with its dedicated
    /// data directory and fixed debug port. Passing the same <c>--user-data-dir</c> while an instance is
    /// already running makes Chrome route the URL to that instance as a new tab, which is how sign-in
    /// opens the account page in the very browser the agent will drive.
    /// </summary>
    private static void StartProcess(AutomationBrowser browser, string? url)
    {
        var exe = ResolveInstalled(browser.Kind).ExePath;
        Directory.CreateDirectory(browser.UserDataDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--user-data-dir={browser.UserDataDir}");
        startInfo.ArgumentList.Add($"--remote-debugging-port={browser.Port}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        if (!string.IsNullOrWhiteSpace(url))
            startInfo.ArgumentList.Add(url);

        FileLog.Write($"[AutomationBrowserService] StartProcess: id={browser.Id}, exe={exe}, url={url ?? "(none)"}");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {browser.Kind} for browser \"{browser.Name}\" ({exe}).");
        FileLog.Write($"[AutomationBrowserService] StartProcess: id={browser.Id} pid={process.Id}");

        // Track the process we launched so a wedged-CDP stop can kill exactly this pid and nothing
        // else. Only the FIRST launch per browser is the owning process: when an instance is already
        // running, passing the same --user-data-dir makes this new process hand its URL to the running
        // one and exit, so overwriting the tracked entry with it would lose the real pid.
        var tracked = LaunchedProcesses.GetOrAdd(browser.Id, process);
        if (!ReferenceEquals(tracked, process))
        {
            if (tracked.HasExited)
            {
                LaunchedProcesses[browser.Id] = process;
                tracked.Dispose();
            }
            else
            {
                // The already-running instance owns the browser; this new process only handed it a
                // URL and exits on its own. Release its handle (Dispose never kills the process).
                process.Dispose();
            }
        }
    }

    private static async Task WaitUntilUpAsync(AutomationBrowser browser, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LaunchReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsUpAsync(browser, ct).ConfigureAwait(false))
                return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Browser \"{browser.Name}\" was launched but its debug port {browser.Port} did not come up within {LaunchReadyTimeout.TotalSeconds:0}s.");
    }

    /// <summary>True when the debug port stops answering within the timeout; false when it stays up.</summary>
    private static async Task<bool> WaitUntilDownAsync(AutomationBrowser browser, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LaunchReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsUpAsync(browser, ct).ConfigureAwait(false))
                return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        FileLog.Write($"[AutomationBrowserService] WaitUntilDownAsync: id={browser.Id} still up after {LaunchReadyTimeout.TotalSeconds:0}s");
        return false;
    }

    /// <summary>
    /// Send CDP <c>Browser.close</c> over the browser-level WebSocket advertised by <c>/json/version</c>.
    /// A down browser is a no-op. The websocket disconnecting as Chrome exits is expected, not an error.
    /// A browser that is UP but whose CDP connection is wedged cannot be closed this way - that case is
    /// handled by the tracked-pid kill in <see cref="TryShutDownAsync"/>.
    /// </summary>
    private static async Task CloseViaCdpAsync(AutomationBrowser browser, CancellationToken ct)
    {
        string? wsUrl;
        try
        {
            using var resp = await Http.GetAsync(VersionUrl(browser.Port), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            wsUrl = doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Already down - nothing to close.
            return;
        }

        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            FileLog.Write($"[AutomationBrowserService] CloseViaCdpAsync: id={browser.Id} no webSocketDebuggerUrl, cannot CDP-close");
            return;
        }

        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}");
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            // Chrome tears the socket down as it exits; we do not need to read a reply.
            FileLog.Write($"[AutomationBrowserService] CloseViaCdpAsync: id={browser.Id} Browser.close sent");
        }
        catch (WebSocketException)
        {
            // The socket dropping as the browser exits is the expected outcome of a successful close.
        }
    }

    private static string VersionUrl(int port) => $"http://127.0.0.1:{port}/json/version";

    /// <summary>
    /// The sign-in landing page appropriate to the browser's identity provider.
    ///
    /// Chrome and Edge each have a browser account that carries a whole profile across in one sign-in,
    /// so landing on it is a real shortcut. Brave and Opera have no such account - Brave Sync is a
    /// pairing code and an Opera account syncs bookmarks, neither of which signs the profile in to
    /// anything an agent will drive. For those two the honest landing is the browser's own start page:
    /// the window comes up focused and the human signs in to the sites this profile is FOR, which is
    /// what the sign-in dialog's copy tells them to do.
    ///
    /// Exhaustive on purpose: a fifth browser added to <see cref="BrowserKind"/> without a decision
    /// made here should fail loudly at this seam rather than quietly land someone on a page chosen for
    /// a different browser.
    /// </summary>
    internal static string AccountUrl(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "https://accounts.google.com/",
        BrowserKind.Edge => "https://login.live.com/",
        BrowserKind.Brave => "https://search.brave.com/",
        BrowserKind.Opera => "https://www.opera.com/",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            $"No sign-in landing page has been chosen for {kind}. Add one to AccountUrl."),
    };
}
