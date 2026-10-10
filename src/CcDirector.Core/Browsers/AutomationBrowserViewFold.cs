using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The finished, render-ready view of one automation browser. Every display decision a client would
/// otherwise make - the status label, the status dot color, the subtitle under the name, which single
/// action to offer, the exact attach command - is folded ONCE here (CLAUDE.md rule 7: the client is
/// dumb). The Control API serializes this record verbatim for the CLI, and the Director's own rail and
/// Settings tab render the SAME record in-process, so no two surfaces can disagree about a browser.
/// </summary>
/// <param name="Id">Stable slug id (also the <c>BU_NAME</c>).</param>
/// <param name="Name">Human-facing, user-editable label.</param>
/// <param name="Browser">The browser's name, e.g. "Chrome" (see <see cref="BrowserKind"/>).</param>
/// <param name="Port">The fixed remote-debugging port.</param>
/// <param name="Status">The folded lifecycle state (see <see cref="AutomationBrowserStatus"/>).</param>
/// <param name="StatusLabel">The human status text: "Ready" / "Needs sign-in" / "Stopped".</param>
/// <param name="DotColor">The status dot's color NAME from the one shared palette ("green" /
/// "yellow" / "grey") - surfaces map the name to a brush via their palette wrapper.</param>
/// <param name="Subtitle">The one-line description under the name, e.g. "Chrome - user@x.com".</param>
/// <param name="ActionLabel">The single next action to offer: "Start" / "Sign in" / "Attach".</param>
/// <param name="Account">The signed-in account read from the browser's own profile, or null.</param>
/// <param name="BuName">The <c>BU_NAME</c> browser-harness attaches with.</param>
/// <param name="BuCdpUrl">The <c>BU_CDP_URL</c> browser-harness attaches with.</param>
/// <param name="AttachCommand">The complete one-liner an agent runs to attach the harness.</param>
/// <param name="UserDataDir">The browser's dedicated user-data directory.</param>
/// <param name="CreatedUtc">When the browser was created.</param>
/// <param name="LastSignedInUtc">When the human last confirmed sign-in, or null if never.</param>
public sealed record AutomationBrowserView(
    string Id,
    string Name,
    string Browser,
    int Port,
    AutomationBrowserStatus Status,
    string StatusLabel,
    string DotColor,
    string Subtitle,
    string ActionLabel,
    string? Account,
    string BuName,
    string BuCdpUrl,
    string AttachCommand,
    string UserDataDir,
    DateTime CreatedUtc,
    DateTime? LastSignedInUtc);

/// <summary>
/// The whole content of the pinned "Browser profiles" row in the Director's left rail. The rail is a
/// navigation strip, not a control panel: the row shows how many profiles exist and whether any are up,
/// and clicking it opens Settings > Browsers, where starting, signing in and attaching live. Folded here
/// with everything else so the row cannot come to disagree with the screen it opens.
/// </summary>
/// <param name="ShowSetup">True when browser-harness is not installed: the row wears a setup nudge
/// instead of a count, so the feature still advertises itself rather than going silent.</param>
/// <param name="ShowCount">True when there is a count worth showing (harness installed, at least one
/// profile registered).</param>
/// <param name="CountText">The number of registered profiles, as text.</param>
/// <param name="RunningDotColor">The dot's colour NAME from the one shared palette when at least one
/// profile is running - the single piece of live status the row carries - or null when none is, which
/// is how a surface knows to show no dot at all.</param>
/// <param name="ToolTip">The row's full sentence, including the running count when it is known.</param>
public sealed record AutomationBrowsersRailView(
    bool ShowSetup,
    bool ShowCount,
    string CountText,
    string? RunningDotColor,
    string ToolTip);

/// <summary>
/// Folds an <see cref="AutomationBrowser"/> into its <see cref="AutomationBrowserView"/>, and answers
/// whether browser-harness (the tool that actually drives these browsers) is installed on this machine.
/// The mapping itself is pure and unit-tested (<see cref="Fold"/>); the async entry points add the two
/// live reads (the debug-port probe and the profile's account) on top.
/// </summary>
public static class AutomationBrowserViewFold
{
    /// <summary>Where "Install Browser Harness" sends the user.</summary>
    public const string HarnessInstallUrl = "https://github.com/browser-use/browser-harness/blob/main/install.md";

    /// <summary>The executable browser-harness ships on PATH; its presence IS the install check.</summary>
    public const string HarnessExecutable = "browser-harness";

    /// <summary>True when browser-harness is installed (resolvable on PATH) on this machine.</summary>
    public static bool IsHarnessInstalled() => ExecutableResolver.Resolve(HarnessExecutable) is not null;

    /// <summary>
    /// Fold every registered browser on this machine into its render-ready view, all at once.
    ///
    /// CONCURRENT ON PURPOSE, and the reason is measured rather than assumed. Each fold's cost is one
    /// probe of a loopback debug port, and a probe of a port nothing is listening on does NOT fail
    /// instantly on every machine: on a box where something in the network stack swallows the RST, the
    /// refusal takes a full two seconds. Folded one after another, that is two seconds PER STOPPED
    /// BROWSER before the list can paint - four browsers spent eight seconds on "Checking browsers...",
    /// and it grew with every browser added. Concurrently it is the cost of the slowest single probe,
    /// whatever the machine's refusal behaviour is, and it stops growing.
    ///
    /// Order is preserved: the results come back in registry order, not completion order, so the list
    /// does not reshuffle itself between refreshes.
    /// </summary>
    public static async Task<IReadOnlyList<AutomationBrowserView>> ListAsync(CancellationToken ct = default)
    {
        var browsers = AutomationBrowserRegistry.Load();
        return await Task.WhenAll(browsers.Select(b => FoldAsync(b, ct))).ConfigureAwait(false);
    }

    /// <summary>
    /// Fold every registered browser WITHOUT asking whether any of them is running. Reads only local
    /// files, so it returns in microseconds however many browsers there are and whatever the machine's
    /// network stack does.
    ///
    /// This is what a surface paints FIRST. Each browser carries its real name, browser, port, account
    /// and attach command - everything except the one fact that costs a probe. The surface then calls
    /// <see cref="FoldAsync"/> per browser and swaps each row in as its answer lands.
    ///
    /// <paramref name="previous"/> is the list this surface is currently showing, when it has one. A
    /// browser already on screen KEEPS its last known status while the fresh probe runs, and only a
    /// browser being shown for the first time reads <see cref="AutomationBrowserStatus.Checking"/>.
    /// Without that, a surface that re-reads on a timer would blink every row back to "Checking..."
    /// every few seconds, which reads as instability rather than as a refresh. Everything else in the
    /// view is still re-read from the registry, so a rename shows immediately - it is ONLY the status
    /// that is carried over, because it is the only field we have not re-established yet.
    /// </summary>
    public static IReadOnlyList<AutomationBrowserView> ListPending(IReadOnlyList<AutomationBrowserView>? previous = null)
    {
        var lastKnown = previous?
            .Where(v => v.Status != AutomationBrowserStatus.Checking)
            .ToDictionary(v => v.Id, v => v.Status, StringComparer.OrdinalIgnoreCase);

        return AutomationBrowserRegistry.Load()
            .Select(b => lastKnown is not null && lastKnown.TryGetValue(b.Id, out var status)
                ? Fold(b, status, ReadAccountOrNull(b))
                : FoldPending(b))
            .ToList();
    }

    /// <summary>
    /// Fold ONE browser without probing it: the full view, status
    /// <see cref="AutomationBrowserStatus.Checking"/>. Used for a browser that has just been created
    /// and so cannot have a probed status yet, and by <see cref="ListPending"/> for rows being shown
    /// for the first time.
    /// </summary>
    public static AutomationBrowserView FoldPending(AutomationBrowser browser)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));
        return Fold(browser, AutomationBrowserStatus.Checking, ReadAccountOrNull(browser));
    }

    /// <summary>Fold one browser: probe its live status, read its signed-in account, then map.</summary>
    public static async Task<AutomationBrowserView> FoldAsync(AutomationBrowser browser, CancellationToken ct = default)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));

        var status = await AutomationBrowserService.StatusAsync(browser, ct).ConfigureAwait(false);
        return Fold(browser, status, ReadAccountOrNull(browser));
    }

    /// <summary>
    /// The signed-in account, or null when it cannot be read. The account is decoration on the
    /// subtitle, never load-bearing: an unreadable profile (first run, mid-write Local State) must not
    /// take the whole list down, so this read alone is allowed to degrade to "no account shown" - and
    /// it logs when it does.
    /// </summary>
    private static string? ReadAccountOrNull(AutomationBrowser browser)
    {
        try
        {
            return AutomationBrowserService.ReadAccount(browser);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AutomationBrowserViewFold] ReadAccount id={browser.Id} FAILED (non-fatal): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The pure mapping from (browser, live status, account) to the finished view. No I/O, fully
    /// unit-tested - this is where the display rules live and the ONLY place they may live.
    /// </summary>
    public static AutomationBrowserView Fold(AutomationBrowser browser, AutomationBrowserStatus status, string? account)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));

        var attach = AutomationBrowserRegistry.AttachInfoFor(browser);
        return new AutomationBrowserView(
            Id: browser.Id,
            Name: browser.Name,
            Browser: browser.Kind.ToString(),
            Port: browser.Port,
            Status: status,
            StatusLabel: StatusLabel(status),
            DotColor: DotColor(status),
            Subtitle: Subtitle(browser.Kind, status, account),
            ActionLabel: ActionLabel(status),
            Account: account,
            BuName: attach.BuName,
            BuCdpUrl: attach.BuCdpUrl,
            AttachCommand: AttachCommand(browser.Id),
            UserDataDir: browser.UserDataDir,
            CreatedUtc: browser.CreatedUtc,
            LastSignedInUtc: browser.LastSignedInUtc);
    }

    /// <summary>The human status text every surface shows verbatim.</summary>
    public static string StatusLabel(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "Stopped",
        AutomationBrowserStatus.NeedsSignIn => "Needs sign-in",
        AutomationBrowserStatus.Ready => "Ready",
        AutomationBrowserStatus.Checking => "Checking...",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>The status dot's color NAME in the one shared session palette: running and signed in is
    /// green, running but never signed in is yellow (attention), not running is grey.</summary>
    public static string DotColor(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "grey",
        AutomationBrowserStatus.NeedsSignIn => "yellow",
        AutomationBrowserStatus.Ready => "green",
        // Grey, the same as stopped: an unknown state must not claim attention, and a colour of its
        // own would read as a fourth thing a browser can be rather than "we have not asked yet".
        AutomationBrowserStatus.Checking => "grey",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>
    /// The single next action a surface offers for this browser, or empty while the answer is still
    /// unknown. Empty is the honest answer for <see cref="AutomationBrowserStatus.Checking"/>: the
    /// right action depends entirely on whether the browser is running, and offering "Start" for a
    /// browser that turns out to be running is worse than offering nothing for a moment.
    /// </summary>
    public static string ActionLabel(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "Start",
        AutomationBrowserStatus.NeedsSignIn => "Sign in",
        AutomationBrowserStatus.Ready => "Attach",
        AutomationBrowserStatus.Checking => "",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>The one-line description under the name: the browser kind, then the most useful fact
    /// (the signed-in account when known, otherwise the state in plain words).</summary>
    public static string Subtitle(BrowserKind kind, AutomationBrowserStatus status, string? account)
    {
        if (!string.IsNullOrWhiteSpace(account))
            return $"{kind} - {account}";

        return status switch
        {
            AutomationBrowserStatus.Stopped => $"{kind} - stopped",
            AutomationBrowserStatus.NeedsSignIn => $"{kind} - not signed in yet",
            AutomationBrowserStatus.Ready => $"{kind} - signed in",
            AutomationBrowserStatus.Checking => $"{kind} - checking...",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
        };
    }

    /// <summary>
    /// Fold the whole list down to the single pinned rail row (see <see cref="AutomationBrowsersRailView"/>).
    ///
    /// The running count deliberately goes UNSAID while any profile is still being probed. A row that
    /// reads "none running" for the second before the probes land, and then flips to "2 running", is
    /// stating something false rather than stating nothing - and this row repaints on a timer, so it
    /// would do it repeatedly. Running means the debug port answered, whether or not the browser has
    /// been signed in yet.
    /// </summary>
    public static AutomationBrowsersRailView FoldRail(IReadOnlyList<AutomationBrowserView> views, bool harnessInstalled)
    {
        if (views is null) throw new ArgumentNullException(nameof(views));

        if (!harnessInstalled)
            return new AutomationBrowsersRailView(
                ShowSetup: true,
                ShowCount: false,
                CountText: "",
                RunningDotColor: null,
                ToolTip: "Browser control is not set up yet. Click to install Browser Harness and set up a signed-in browser.");

        if (views.Count == 0)
            return new AutomationBrowsersRailView(
                ShowSetup: false,
                ShowCount: false,
                CountText: "",
                RunningDotColor: null,
                ToolTip: "No browser profiles yet. Click to add one your agents can drive.");

        var stillChecking = views.Any(v => v.Status == AutomationBrowserStatus.Checking);
        var running = views.Count(v => v.Status is AutomationBrowserStatus.Ready or AutomationBrowserStatus.NeedsSignIn);
        var profiles = views.Count == 1 ? "1 browser profile" : $"{views.Count} browser profiles";
        var runningClause = stillChecking ? "" : running == 0 ? ", none running" : $", {running} running";

        return new AutomationBrowsersRailView(
            ShowSetup: false,
            ShowCount: true,
            CountText: views.Count.ToString(),
            // Green is the palette's "up and usable"; there is no second colour here on purpose. A
            // browser that is running but not signed in is still running, and the row has no room to
            // say more than that - the screen it opens does.
            RunningDotColor: running > 0 ? "green" : null,
            ToolTip: $"{profiles}{runningClause}. Click to manage them in Settings.");
    }

    /// <summary>The complete attach one-liner an agent runs. Built on the slug id (never the free-text
    /// name) so the command is always shell-safe.</summary>
    public static string AttachCommand(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A browser id is required.", nameof(id));
        return $"eval \"$(cc-devthrottle browser attach '{id}')\"";
    }
}
