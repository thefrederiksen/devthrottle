using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>A browser profile this Director started for one run of the license tool, and puts back after it. Its
/// identifier, display name and browser-harness attach values travel to the tool in the run's environment
/// (<see cref="LicenseBrowserOpener.EnvironmentFor"/>).</summary>
public sealed class LicenseOpenedBrowser : IAsyncDisposable
{
    private readonly Func<Task> _putBack;
    private int _closed;

    public LicenseOpenedBrowser(string id, string name, string buName, string buCdpUrl, Func<Task> putBack)
    {
        Id = id;
        Name = name;
        BuName = buName;
        BuCdpUrl = buCdpUrl;
        _putBack = putBack ?? throw new ArgumentNullException(nameof(putBack));
    }

    public string Id { get; }

    public string Name { get; }

    public string BuName { get; }

    public string BuCdpUrl { get; }

    /// <summary>Puts the profile back as it was found: stopped again when it was stopped. Once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        await _putBack();
    }
}

/// <summary>Starts one of this Director's own browser profiles for a run of the license tool.</summary>
public interface ILicenseBrowserOpener
{
    /// <summary>The profile <paramref name="idOrName"/> started and ready to drive. Throws
    /// <see cref="LicenseCommandLineException"/>, saying why, when it is not one of this machine's profiles or does
    /// not start.</summary>
    Task<LicenseOpenedBrowser> OpenAsync(string idOrName, CancellationToken cancellationToken);
}

/// <summary>
/// WHY THE DIRECTOR STARTS THE PROFILE ITSELF (mission "Licenses - Loud When Stranded", step 4). From 19 to 26 October
/// 2026 the daily approver check answered check-failed on every profile of SOREN_NORTH, in seconds, while the same
/// command typed in a session worked. The license tool opened a browser profile only through the Gateway, as a
/// session - it needs <c>CC_DIRECTOR_ID</c> and a session key, which a session is given at launch - and the Director's
/// own process is not a session and has neither. So every run the Director made of <c>license check-approver</c>,
/// <c>license approve</c> and <c>license sign-in</c> with a profile here (the renewal included) failed with "CC_DIRECTOR_ID
/// is not set". The browsers are this Director's: it starts the profile here, as the tunnel's own browser verb does
/// (<c>BrowserExecutor</c>), hands the tool the attach values, and stops the profile again after when it was stopped.
/// </summary>
public sealed class LicenseBrowserOpener : ILicenseBrowserOpener
{
    /// <summary>The tool's variables for the profile it is handed (tools/cc-devthrottle license_signin.ENV_ATTACHED_BROWSER_*).</summary>
    public const string AttachedIdVariable = "CC_LICENSE_ATTACHED_BROWSER_ID";

    public const string AttachedNameVariable = "CC_LICENSE_ATTACHED_BROWSER_NAME";

    /// <summary>The variables a run of the tool is given for <paramref name="browser"/>.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentFor(LicenseOpenedBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return new Dictionary<string, string>
        {
            [AttachedIdVariable] = browser.Id,
            [AttachedNameVariable] = browser.Name,
            ["BU_NAME"] = browser.BuName,
            ["BU_CDP_URL"] = browser.BuCdpUrl,
        };
    }

    private readonly LicenseBrowserControl _control;

    public LicenseBrowserOpener() : this(LicenseBrowserControl.ThisMachine)
    {
    }

    /// <summary>For tests: the profiles and the browser service it drives.</summary>
    internal LicenseBrowserOpener(LicenseBrowserControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>
    /// A BROWSER THAT IS ALREADY RUNNING IS READ WHERE IT IS, AND NEVER CLOSED (the Delivery Lead's ruling on step 4).
    /// The daily approver check reads every profile on the machine, and a session or a night-time factory may be driving
    /// one of them: the put-back stops only a profile this run STARTED, and "started" is decided from a definite answer.
    /// A probe that did not answer in time is not "stopped" - a running browser on a busy machine looks like that, and
    /// starting it would open a second window while stopping it after would close the session's browser - so on doubt
    /// the run starts nothing, stops nothing, and the check says why. The tool drives the profile under a
    /// browser-harness daemon of its own, so the session's daemon is never moved or ended either.
    /// </summary>
    public async Task<LicenseOpenedBrowser> OpenAsync(string idOrName, CancellationToken cancellationToken)
    {
        FileLog.Write($"[LicenseBrowserOpener] OpenAsync: profile={idOrName}");
        var browser = _control.Find(idOrName)
            ?? throw new LicenseCommandLineException($"No browser profile \"{idOrName}\" is on this machine.");
        bool startedHere;
        try
        {
            startedHere = await _control.StartIfStoppedAsync(browser, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            FileLog.Write($"[LicenseBrowserOpener] OpenAsync FAILED: profile={browser.Id}: {ex.Message}");
            throw new LicenseCommandLineException($"The browser profile \"{browser.Name}\" did not start on this machine: {ex.Message}");
        }
        var attach = _control.AttachInfoFor(browser);
        FileLog.Write($"[LicenseBrowserOpener] OpenAsync: profile={browser.Id} is up, startedHere={startedHere}");
        return new LicenseOpenedBrowser(browser.Id, browser.Name, attach.BuName, attach.BuCdpUrl, async () =>
        {
            if (!startedHere)
            {
                FileLog.Write($"[LicenseBrowserOpener] put back: profile={browser.Id} was already running, left running");
                return;
            }
            try
            {
                await _control.StopAsync(browser.Id, CancellationToken.None);
                FileLog.Write($"[LicenseBrowserOpener] put back: profile={browser.Id} stopped again");
            }
            catch (TimeoutException ex)
            {
                // The run is over and answered; a profile left running is said, not hidden.
                FileLog.Write($"[LicenseBrowserOpener] put back FAILED: profile={browser.Id} is still running: {ex.Message}");
            }
        });
    }
}

/// <summary>What <see cref="LicenseBrowserOpener"/> drives: this machine's browser profiles and the browser service, or a
/// test's stand-ins.</summary>
internal sealed record LicenseBrowserControl(
    Func<string, AutomationBrowser?> Find,
    Func<AutomationBrowser, CancellationToken, Task<bool>> StartIfStoppedAsync,
    Func<AutomationBrowser, AttachInfo> AttachInfoFor,
    Func<string, CancellationToken, Task> StopAsync)
{
    public static readonly LicenseBrowserControl ThisMachine = new(
        AutomationBrowserRegistry.Find,
        (browser, ct) => AutomationBrowserService.StartIfStoppedAsync(browser, ct),
        AutomationBrowserRegistry.AttachInfoFor,
        (id, ct) => AutomationBrowserService.StopAsync(id, ct));
}
