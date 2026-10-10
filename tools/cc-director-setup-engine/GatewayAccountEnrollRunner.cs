using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Setup.Engine;

/// <summary>
/// One gateway the installer discovered on the signed-in account (issue #1206): its human-readable
/// <paramref name="Name"/> (shown to the person - a raw URL is never shown) and its reachable front-door
/// <paramref name="EndpointUrl"/> (used as the enroll target). Only gateways that have published an address
/// are surfaced.
/// </summary>
/// <param name="Name">The gateway's device name, shown in the chooser when the account has more than one.</param>
/// <param name="EndpointUrl">The gateway's reachable front-door URL, used as the enroll target.</param>
public sealed record DiscoveredGateway(string Name, string EndpointUrl);

/// <summary>
/// A hosted enrollment for a team (devthrottle_internal#2311): the issued key, the team this Director now works
/// for, and the name the person gave it on screen D1.
/// </summary>
/// <param name="DeviceKey">The device key the hosted Gateway issued.</param>
/// <param name="Team">The team chosen - null when the Gateway has no teams (no chip).</param>
/// <param name="DirectorName">The name typed on D1, or null when nothing was asked.</param>
public sealed record HostedTeamEnrollment(string DeviceKey, DirectorTeam? Team, string? DirectorName);

/// <summary>
/// The installer-time gateway-join gate for a Workstation install. A machine that joins an existing
/// fleet as a Workstation MUST connect to its gateway before the install can finish: the gateway is the
/// account authority, so a Workstation with no gateway connection is useless.
///
/// This replaces the old 4-digit pairing-code mechanism with DevThrottle account sign-in, so signing in
/// is the ONE way any machine registers (the Gateway install already signs in; this brings the
/// Workstation onto the same model - epic #1069, issue #1198). The 4-digit code is gone; the gateway URL
/// stays, because the cloud never learns a fleet's private network address (DevThrottle relays no
/// traffic), so the machine must still be told WHERE its gateway is - only the authorization changes.
///
/// The join reuses three proven pieces end to end, so no new cloud endpoint is introduced:
///   1. The same browser loopback sign-in the Gateway install uses (<see cref="LoopbackLoginListener"/>
///      + <see cref="FirstRunLoginCoordinator.BuildSignInUrl"/>): the user signs in on devthrottle.com
///      and the account access token is captured on a <c>127.0.0.1</c> callback. It is held in memory
///      ONLY and never persisted - a Workstation holds no account credential (the Gateway is the
///      authority, issue #642).
///   2. The account device registry (<see cref="DeviceRegistryClient.RegisterAsync"/>, the same call the
///      Gateway uses to self-register): this Workstation is registered as an account device and the cloud
///      issues its per-device key once.
///   3. The gateway enrollment seam (<c>POST /mobile/enroll</c>, the same endpoint the phone and Cockpit use):
///      the cloud device key is exchanged at the target gateway for a LOCAL, individually-revocable
///      device key. The gateway confirms (account-scoped) the key belongs to its OWN account, so a
///      Workstation signed into a DIFFERENT account is refused with a clear reason.
///
/// On success it persists the gateway URL + the issued LOCAL device key to <c>config.json</c> and the
/// credential file via <see cref="GatewayCredentialStore.SaveEnrolledKey"/> - unchanged downstream, so
/// the Director and the local cc-* tools connect on first run exactly as before.
///
/// It never throws for an expected failure (cancelled or failed sign-in, cloud registration failure,
/// unreachable gateway, wrong account, no key issued); it returns a human-readable reason so the
/// installer step renders it and BLOCKS completion. The sign-in step, the HTTP handler, and the persist
/// action are all injectable so the join logic is unit-testable with no browser, no network, and no disk.
/// The captured access token and both device keys are NEVER written to the log (security rule DT-05).
///
/// THESE MESSAGES STATE THE FACT AND NAME NO CONTROL. This engine is shared by surfaces whose buttons
/// are labelled differently - the wizard's gateway step offers "Try again" and "Sign in and connect",
/// the shared gateway panel offers "Sign in with DevThrottle" - so any button name written here is
/// wrong somewhere. All three cancel messages used to end <c>Click "Sign in to DevThrottle" to try
/// again</c>, which was wrong EVERYWHERE: no surface has a control with that label. It is the heading of
/// the browser sign-in page, which at the moment of a cancellation is precisely the thing the user just
/// closed. So the engine reports WHAT HAPPENED and each surface adds its own recovery wording next to
/// its own buttons (issue #1070).
/// </summary>
public sealed class GatewayAccountEnrollRunner
{
    /// <summary>The account device-registry device type recorded for a Workstation/Director machine,
    /// distinct from the Gateway's own "gateway" type and the browser/phone types.</summary>
    public const string WorkstationDeviceType = "workstation";

    /// <summary>The account device-registry device type a Gateway registers itself as (issue #1206). The
    /// installer filters the account's devices to this type to discover which gateway to enroll against.</summary>
    public const string GatewayDeviceType = "gateway";

    /// <summary>The platform string reported for this Workstation/Director machine (a roster attribute),
    /// resolved per-OS so a Mac enrolls as "macos" (not "windows") and the account roster is accurate.</summary>
    public static string WorkstationPlatform { get; } =
        OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsLinux() ? "linux"
        : "windows";

    /// <summary>How long to wait for the browser sign-in hand-back before treating it as abandoned.
    /// Mirrors the installer's forced sign-in step (issue #657): long enough for a real sign-in, short
    /// enough to recover.</summary>
    public static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<DevThrottleTokens>> _signIn;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly Action<string, string> _persist;
    private readonly Action<DirectorTeam?> _persistTeam;
    private readonly IHostedTeamsSignal _teamsSignal;
    private readonly TimeSpan _httpTimeout;

    // The account token captured by SignInAndDiscoverGatewaysAsync, held in memory ONLY for the immediately
    // following EnrollWithDiscoveredGatewayAsync call (issue #1206). A Workstation persists no account
    // credential; this is never written to disk or logged. Single-use per install wizard instance.
    private DevThrottleTokens? _pendingTokens;

    /// <summary>
    /// Build a runner. By default it drives a real browser loopback sign-in, makes real cloud/gateway HTTP
    /// calls, and persists with <see cref="GatewayCredentialStore.SaveEnrolledKey"/>. Tests inject a fake
    /// sign-in that returns a token with no browser, a fake HTTP handler, and a capturing persist action so
    /// the join logic runs with no network and no disk writes (no fallback construction - each has an
    /// explicit default).
    /// </summary>
    /// <param name="signIn">Performs the browser sign-in and returns the captured account token pair; null
    /// uses the real loopback + system-browser flow with a <see cref="DefaultSignInTimeout"/> deadline.</param>
    /// <param name="handlerFactory">Supplies the <see cref="HttpMessageHandler"/> for the cloud register and
    /// the gateway enroll calls; null uses a real <see cref="HttpClientHandler"/>.</param>
    /// <param name="persist">Persists the verified (gatewayUrl, localDeviceKey) pair; null uses
    /// <see cref="GatewayCredentialStore.SaveEnrolledKey"/>.</param>
    /// <param name="httpTimeout">Per-call timeout for the HTTP calls; null uses 15 seconds.</param>
    /// <param name="persistTeam">Records the team a hosted enrollment chose for THIS Director, beside its key
    /// (devthrottle_internal#2311); null means the Gateway has no teams and any recorded team is forgotten.
    /// Null uses <see cref="DirectorTeamStore"/> in this process's own storage home.</param>
    /// <param name="teamsSignal">Asks the hosted Gateway whether it has Teams released; null reads its anonymous
    /// health answer through the same HTTP handler.</param>
    /// <param name="signInAddressDisplay">The screen that shows the sign-in address while the sign-in waits, so
    /// the person has a way in when Windows opens no browser (issue #3504). Null means nobody shows the address
    /// (the command line), and then a browser that cannot be opened fails the sign-in.</param>
    /// <param name="openBrowser">Opens the system browser at the sign-in address; null shell-executes it.</param>
    public GatewayAccountEnrollRunner(
        Func<CancellationToken, Task<DevThrottleTokens>>? signIn = null,
        Func<HttpMessageHandler>? handlerFactory = null,
        Action<string, string>? persist = null,
        TimeSpan? httpTimeout = null,
        Action<DirectorTeam?>? persistTeam = null,
        IHostedTeamsSignal? teamsSignal = null,
        ISignInAddressDisplay? signInAddressDisplay = null,
        Action<string>? openBrowser = null)
    {
        var open = openBrowser ?? OpenSystemBrowser;
        _signIn = signIn ?? (ct => SignInViaBrowserAsync(signInAddressDisplay, open, ct));
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
        _persist = persist ?? GatewayCredentialStore.SaveEnrolledKey;
        _persistTeam = persistTeam ?? PersistTeamInThisHome;
        _httpTimeout = httpTimeout ?? TimeSpan.FromSeconds(15);
        _teamsSignal = teamsSignal ?? new HealthzTeamsSignal(_handlerFactory, _httpTimeout);
    }

    /// <summary>The route the hosted Gateway lists the signed-in person's teams on, asked only when its signal
    /// says it has Teams.</summary>
    public const string HostedTeamsRoute = "devices/enroll-hosted/teams";

    /// <summary>
    /// The route that moves an enrolled Director to another team (screen D3). Its exact shape is the Gateway
    /// Developer's contract for devthrottle_internal#2311; this is the shape the Director codes against.
    /// </summary>
    public const string HostedMoveRoute = "devices/enroll-hosted/move";

    private static void PersistTeamInThisHome(DirectorTeam? team)
    {
        if (team is null)
            DirectorTeamStore.Clear();
        else
            DirectorTeamStore.Save(team);
    }

    /// <summary>
    /// Sign in with the DevThrottle account, register this Workstation as an account device, exchange its
    /// cloud device key at the gateway at <paramref name="gatewayUrl"/> for a local device key, and on
    /// success persist the gateway URL + local device key. Returns the issued
    /// <see cref="MobileEnrollmentResponse"/> on success, or a human-readable reason on any expected
    /// failure. The installer gates the Finish button on <see cref="OperationResult{T}.Success"/>.
    /// </summary>
    /// <param name="gatewayUrl">The target gateway's reachable URL (for example a tailnet address).</param>
    /// <param name="deviceId">This machine's stable device/install id - used both as the cloud
    /// registration install id and as the <c>/mobile/enroll</c> device id, so the gateway's local record maps
    /// to the same cloud roster row.</param>
    /// <param name="machineName">A human-readable device name shown in the account roster.</param>
    /// <param name="ct">Cancelled by the installer's Cancel button while the sign-in is in flight.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> VerifyAndSaveAsync(
        string gatewayUrl, string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<MobileEnrollmentResponse>.Fail("Enter the gateway URL.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");
        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway URL is not valid. Use http://host:port or https://host:port.");

        var url = gatewayUrl.Trim();
        EngineLog.Write($"[GatewayAccountEnrollRunner] VerifyAndSaveAsync: gateway={url}, deviceId={deviceId}, machine={machineName}");

        // 1. Sign in with the DevThrottle account (browser loopback). The token is held in memory only and
        // never persisted - a Workstation holds no account credential.
        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not-an-error: the sign-in was cancelled before a credential arrived (the person or the closing window stopped it); nothing failed
            EngineLog.Write("[GatewayAccountEnrollRunner] VerifyAndSaveAsync: sign-in cancelled before a credential arrived");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] VerifyAndSaveAsync: sign-in FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not return a usable credential. Please try again.");

        return await RegisterAndEnrollAsync(tokens, url, deviceId, machineName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sign in with the DevThrottle account and discover the account's gateways so the installer can enroll
    /// against one WITHOUT the person typing a gateway URL (issue #1206). The account already knows which
    /// gateways belong to the user: each signed-in Gateway publishes its own reachable front-door URL as its
    /// device <c>endpoint_url</c>, so this signs in (the same browser loopback flow), lists the account's
    /// devices, and returns the ones of type "gateway" that carry a non-empty <c>endpoint_url</c>.
    ///
    /// The captured token is held in memory only for the immediately-following
    /// <see cref="EnrollWithDiscoveredGatewayAsync"/> call (a Workstation holds no account credential) and is
    /// never persisted or logged. Returns the discovered gateways (one or more) on success, or a clear,
    /// actionable reason when the sign-in did not complete or the account has NO reachable gateway yet - never
    /// an empty list dressed as success and never a fabricated address.
    /// </summary>
    /// <param name="ct">Cancelled by the installer's Cancel button while the sign-in is in flight.</param>
    public async Task<OperationResult<IReadOnlyList<DiscoveredGateway>>> SignInAndDiscoverGatewaysAsync(CancellationToken ct = default)
    {
        EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: starting account sign-in + gateway discovery");

        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not-an-error: the sign-in was cancelled before a credential arrived (the person or the closing window stopped it); nothing failed
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: sign-in cancelled before a credential arrived");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: sign-in FAILED: {ex.Message}");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Sign-in did not return a usable credential. Please try again.");

        IReadOnlyList<CloudDeviceRecord> devices;
        try
        {
            using var cloudHttp = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
            var registry = new DeviceRegistryClient(cloudHttp);
            devices = await registry.ListDevicesAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: listing account devices FAILED: {ex.Message}");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "Signed in, but your DevThrottle account devices could not be read. Please check your connection and try again.");
        }

        // A gateway the installer can enroll against is a device of type "gateway" that has published a
        // reachable front-door URL. A gateway with no address yet is NOT offered (there is nowhere to enroll).
        var gateways = new List<DiscoveredGateway>();
        foreach (var device in devices)
        {
            if (string.Equals(device.DeviceType, GatewayDeviceType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(device.EndpointUrl))
            {
                gateways.Add(new DiscoveredGateway(device.Name, device.EndpointUrl.Trim()));
            }
        }

        if (gateways.Count == 0)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: no reachable gateway on this account");
            return OperationResult<IReadOnlyList<DiscoveredGateway>>.Fail(
                "No reachable gateway is registered on your account yet. Start and sign in your gateway first, then run this installer.");
        }

        // Hold the token in memory for the enroll call that follows the person's gateway choice.
        _pendingTokens = tokens;
        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndDiscoverGatewaysAsync: discovered {gateways.Count} reachable gateway(s) on the account");
        return OperationResult<IReadOnlyList<DiscoveredGateway>>.Ok(gateways);
    }

    /// <summary>
    /// Register this Workstation on the account and enroll it at the gateway discovered by
    /// <see cref="SignInAndDiscoverGatewaysAsync"/>, using the token that call captured (issue #1206). This is
    /// the second half of the drop-the-URL-box flow: the person never types the address - it is the
    /// discovered gateway's own published <paramref name="gatewayUrl"/>. On success it persists the gateway
    /// URL + issued local device key exactly as the manual path did.
    ///
    /// Must be called after a successful <see cref="SignInAndDiscoverGatewaysAsync"/> in the same run; if no
    /// captured token is held it fails with a clear reason rather than silently signing in again.
    /// </summary>
    /// <param name="gatewayUrl">The discovered gateway's reachable front-door URL (never typed by the person).</param>
    /// <param name="deviceId">This machine's stable device/install id.</param>
    /// <param name="machineName">A human-readable device name shown in the account roster.</param>
    /// <param name="ct">Cancels the register/enroll HTTP calls.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> EnrollWithDiscoveredGatewayAsync(
        string gatewayUrl, string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<MobileEnrollmentResponse>.Fail("The discovered gateway has no address.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");
        if (!Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The discovered gateway address is not a valid URL.");

        var tokens = _pendingTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Please sign in to DevThrottle first.");

        var url = gatewayUrl.Trim();
        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollWithDiscoveredGatewayAsync: gateway={url}, deviceId={deviceId}, machine={machineName}");
        return await RegisterAndEnrollAsync(tokens, url, deviceId, machineName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Join this machine to DevThrottle's HOSTED gateway instead of a gateway the account runs itself: sign in
    /// with the DevThrottle account and enroll at <see cref="HostedGateway"/> for a local, revocable device
    /// key bound to this account's tenant. On success it persists the hosted URL + issued key exactly as the
    /// self-hosted paths do, so everything downstream - the Director's stream connection, the local cc-* tools -
    /// is unchanged.
    ///
    /// THIS PATH IS SHORTER THAN THE SELF-HOSTED ONE, AND DELIBERATELY SO. Self-hosted registers the machine in
    /// the account DEVICE REGISTRY to obtain a cloud device key, then exchanges THAT key at the target gateway
    /// via <c>/mobile/enroll</c>, because a gateway the account runs has no way to judge an account token itself. The
    /// hosted gateway does: <c>POST /devices/enroll-hosted</c> takes the ACCOUNT ACCESS TOKEN directly, verifies
    /// it (signature, expiry, audience, issuer), maps its subject to a tenant, and mints the per-device key in
    /// ONE step. So there is no device-registry exchange here - not a missing step, an unnecessary one. There is
    /// also nothing to provision and nothing to discover: hosted is ONE shared multi-tenant gateway, and
    /// enrolling is what makes this account a tenant on it.
    ///
    /// The captured account token is held in memory ONLY for the duration of this call and is never persisted or
    /// logged (security rule DT-05), matching the self-hosted paths - a workstation holds no account credential.
    /// </summary>
    /// <param name="deviceId">This machine's stable device/install id, sent as the hosted device id.</param>
    /// <param name="machineName">A human-readable device name recorded on the hosted gateway.</param>
    /// <param name="ct">Cancelled by the caller while the sign-in or enroll is in flight.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> SignInAndEnrollHostedAsync(
        string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");

        string hostedUrl;
        try
        {
            hostedUrl = HostedGateway.ResolveUrl();
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndEnrollHostedAsync FAILED: hosted address unusable: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(ex.Message);
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndEnrollHostedAsync: hosted={hostedUrl}, deviceId={deviceId}, machine={machineName}");

        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not-an-error: the sign-in was cancelled before a credential arrived (the person or the closing window stopped it); nothing failed
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndEnrollHostedAsync: sign-in cancelled before a credential arrived");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAndEnrollHostedAsync: sign-in FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Sign-in did not return a usable credential. Please try again.");

        return await EnrollAtHostedGatewayAsync(hostedUrl, tokens.AccessToken, deviceId, machineName, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enroll at the HOSTED gateway using the account token already captured by a preceding
    /// <see cref="SignInAndDiscoverGatewaysAsync"/> in the same run - the seam for a chooser that offers
    /// "DevThrottle hosted" alongside the account's own gateways, so picking hosted costs no second sign-in.
    /// Fails with a clear reason if no captured token is held, rather than silently signing in again.
    /// </summary>
    /// <param name="deviceId">This machine's stable device/install id.</param>
    /// <param name="machineName">A human-readable device name recorded on the hosted gateway.</param>
    /// <param name="ct">Cancels the enroll call.</param>
    public async Task<OperationResult<MobileEnrollmentResponse>> EnrollWithHostedGatewayAsync(
        string deviceId, string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<MobileEnrollmentResponse>.Fail("This machine has no device id.");

        var tokens = _pendingTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Please sign in to DevThrottle first.");

        string hostedUrl;
        try
        {
            hostedUrl = HostedGateway.ResolveUrl();
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollWithHostedGatewayAsync FAILED: hosted address unusable: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(ex.Message);
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollWithHostedGatewayAsync: hosted={hostedUrl}, deviceId={deviceId}, machine={machineName}");
        return await EnrollAtHostedGatewayAsync(hostedUrl, tokens.AccessToken, deviceId, machineName, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST the ACCOUNT ACCESS TOKEN to the hosted gateway's <c>/devices/enroll-hosted</c> and map the reply to
    /// a success-or-reason result, persisting the hosted URL + issued local device key on success ONLY. A
    /// transport failure, a 401 (the account token was not accepted), any other non-2xx, or a 2xx carrying no
    /// device key all return a clear reason and persist NOTHING - so a machine can never end up pointed at the
    /// hosted gateway without a key that the hosted gateway actually issued. Neither the account token nor the
    /// issued device key is ever logged (security rule DT-05).
    /// </summary>
    private async Task<OperationResult<MobileEnrollmentResponse>> EnrollAtHostedGatewayAsync(
        string hostedUrl, string accountAccessToken, string deviceId, string machineName, string? teamId, CancellationToken ct)
    {
        var request = new EnrollSignedInRequest
        {
            DeviceId = deviceId,
            MachineName = machineName,
            Platform = WorkstationPlatform,
            DeviceType = WorkstationDeviceType,
        };

        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
        http.BaseAddress = new Uri(hostedUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accountAccessToken);

        HttpResponseMessage resp;
        try
        {
            // With no team the request is exactly the one sent before Teams existed - the same object, so the
            // same bytes. With a team, the same fields plus "teamId" (devthrottle_internal#2311).
            if (teamId is null)
            {
                resp = await http.PostAsJsonAsync("devices/enroll-hosted", request, ct).ConfigureAwait(false);
            }
            else
            {
                var withTeam = JsonSerializer.SerializeToNode(request, WebJson)!.AsObject();
                withTeam["teamId"] = teamId;
                resp = await http.PostAsJsonAsync("devices/enroll-hosted", withTeam, WebJson, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync transport FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"Could not reach the DevThrottle hosted gateway at {hostedUrl}. Please check your connection and try again.");
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync rejected: HTTP 401 (account token not accepted)");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The DevThrottle hosted gateway did not accept your sign-in. Please sign in again and try once more.");
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            // The person cannot run sessions in the chosen team. The Gateway says why in plain words; the person
            // reads exactly that, not a status code.
            var refusal = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync refused: HTTP 403");
            return OperationResult<MobileEnrollmentResponse>.Fail(refusal);
        }
        if (!resp.IsSuccessStatusCode)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"The DevThrottle hosted gateway refused the enrollment: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        DeviceRegistrationResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync FAILED: could not read reply: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The DevThrottle hosted gateway accepted the sign-in but its reply could not be read.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync: 2xx with no device key in reply");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The DevThrottle hosted gateway accepted the sign-in but returned no device key.");
        }

        _persist(hostedUrl, body.DeviceKey);
        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtHostedGatewayAsync: persisted hosted url + local per-device key (machine={machineName}, team={(teamId ?? "none")})");

        // The hosted reply carries the registry echo as well as the key; the callers of every enroll path only
        // need the key, so it is handed back in the same shape the self-hosted paths return.
        return OperationResult<MobileEnrollmentResponse>.Ok(new MobileEnrollmentResponse { DeviceKey = body.DeviceKey });
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Join the HOSTED gateway FOR A TEAM (devthrottle_internal#2311, screen D1): ask the Gateway whether it has
    /// Teams, sign in, ask the Gateway which teams the person may run sessions in, ask the person which one this
    /// Director is for, and enroll with that team's id. The new key and the chosen team are stored together in
    /// this Director's own home, so two Directors on one computer can work for two teams.
    ///
    /// The Gateway's own signal decides, never a guess from another route's answer (review finding F1):
    /// <list type="bullet">
    /// <item>The Gateway says it has no Teams, or says nothing about Teams (a Gateway from before Teams): no teams
    /// call, nothing asked, the enrollment is exactly the one sent before Teams, and no team is recorded.</item>
    /// <item>It says it has Teams: the teams call, and any answer but 200 is an error shown as it is. No team
    /// listed: nothing asked, today's enrollment, the personal account recorded. Exactly one: nothing asked, that
    /// team's id sent, and the Director named "&lt;computer&gt; - &lt;team&gt;" as D1 would have started it. Two or
    /// more: the person is asked, the chosen team's id is sent, and a refusal is returned in the Gateway's own words.</item>
    /// </list>
    /// The account token is held in memory only and never logged.
    /// </summary>
    /// <param name="deviceId">This Director's own id, sent as the hosted device id.</param>
    /// <param name="machineName">The computer's name, recorded on the Gateway.</param>
    /// <param name="chooseTeam">Asks the person (screen D1). Returns null when they cancel.</param>
    /// <param name="ct">Cancels the sign-in, the listing or the enrollment.</param>
    public async Task<OperationResult<HostedTeamEnrollment>> SignInChooseTeamAndEnrollHostedAsync(
        string deviceId, string machineName,
        Func<TeamQuestion, CancellationToken, Task<TeamAnswer?>> chooseTeam,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chooseTeam);
        if (string.IsNullOrWhiteSpace(deviceId))
            return OperationResult<HostedTeamEnrollment>.Fail("This machine has no device id.");

        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: deviceId={deviceId}, machine={machineName}");

        var released = await HostedTeamsReleasedAsync(ct).ConfigureAwait(false);
        if (!released.Success)
            return OperationResult<HostedTeamEnrollment>.Fail(released.ErrorMessage!);
        var (hostedUrl, teamsReleased) = released.Value;

        var signedIn = await SignInAsync(ct).ConfigureAwait(false);
        if (!signedIn.Success)
            return OperationResult<HostedTeamEnrollment>.Fail(signedIn.ErrorMessage!);
        var accessToken = signedIn.Value!;

        DirectorTeam? team;
        string? directorName = null;
        if (!teamsReleased)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: this Gateway has no teams; enrolling as before Teams");
            team = null;
        }
        else
        {
            var listed = await ListHostedTeamsAsync(hostedUrl, accessToken, ct).ConfigureAwait(false);
            if (!listed.Success)
                return OperationResult<HostedTeamEnrollment>.Fail(listed.ErrorMessage!);

            var choices = TeamChoices.Build(listed.Value!);
            if (TeamChoices.TakenWithoutAsking(choices) is { } taken)
            {
                team = taken.ToTeam();
                if (taken.IsPersonal)
                {
                    EngineLog.Write("[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: no team to choose; the personal account, not asked");
                }
                else
                {
                    // D1 is not shown, so the Director gets the name D1 would have started with: "<computer> - <team>".
                    directorName = TeamChoices.SuggestDirectorName(machineName, taken);
                    EngineLog.Write($"[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: one team, not asked; team {team.TeamId}");
                }
            }
            else
            {
                EngineLog.Write($"[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: asking which team ({choices.Count} choices)");
                var chosen = await chooseTeam(new TeamQuestion(choices, machineName), ct).ConfigureAwait(false);
                if (chosen is null)
                {
                    EngineLog.Write("[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: no team chosen; nothing enrolled");
                    return OperationResult<HostedTeamEnrollment>.Fail("No team was chosen, so this Director was not connected.");
                }
                team = chosen.Choice.ToTeam();
                directorName = chosen.DirectorName;
                EngineLog.Write($"[GatewayAccountEnrollRunner] SignInChooseTeamAndEnrollHostedAsync: chose {(team.IsPersonal ? "the personal account" : "team " + team.TeamId)}");
            }
        }

        var enrolled = await EnrollAtHostedGatewayAsync(hostedUrl, accessToken, deviceId, machineName, team?.TeamId, ct).ConfigureAwait(false);
        if (!enrolled.Success)
            return OperationResult<HostedTeamEnrollment>.Fail(enrolled.ErrorMessage!);

        _persistTeam(team);
        return OperationResult<HostedTeamEnrollment>.Ok(new HostedTeamEnrollment(enrolled.Value!.DeviceKey, team, directorName));
    }

    /// <summary>
    /// List the teams the person may move this Director to (screen D3). Asks the Gateway's signal first: with no
    /// Teams there, it answers so without signing in. Otherwise it signs in and keeps the account token in memory
    /// for the <see cref="MoveHostedDirectorAsync"/> that follows; the token is never written or logged.
    /// </summary>
    public async Task<OperationResult<HostedTeamsAnswer>> SignInAndListHostedTeamsAsync(CancellationToken ct = default)
    {
        EngineLog.Write("[GatewayAccountEnrollRunner] SignInAndListHostedTeamsAsync");
        var released = await HostedTeamsReleasedAsync(ct).ConfigureAwait(false);
        if (!released.Success)
            return OperationResult<HostedTeamsAnswer>.Fail(released.ErrorMessage!);
        var (hostedUrl, teamsReleased) = released.Value;
        if (!teamsReleased)
            return OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(false, Array.Empty<HostedTeam>()));

        var signedIn = await SignInAsync(ct).ConfigureAwait(false);
        if (!signedIn.Success)
            return OperationResult<HostedTeamsAnswer>.Fail(signedIn.ErrorMessage!);

        var listed = await ListHostedTeamsAsync(hostedUrl, signedIn.Value!, ct).ConfigureAwait(false);
        return listed.Success
            ? OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(true, listed.Value!))
            : OperationResult<HostedTeamsAnswer>.Fail(listed.ErrorMessage!);
    }

    /// <summary>A move refused with 404: the account has no working key for this Director.</summary>
    public const string MoveNoWorkingKey =
        "DevThrottle has no working key for this Director on your account, so it cannot be moved. Connect it again from the Gateway tab.";

    /// <summary>A move refused with 403 because another account set this Director up.</summary>
    public const string MoveSomeoneElses =
        "This Director was set up with a different DevThrottle account, so it cannot be moved from yours. Sign in with the account that set it up.";

    /// <summary>A move refused with 409 because the Director already works for that team.</summary>
    public const string MoveAlreadyThere = "This Director already works for that team. Nothing was changed.";

    /// <summary>A move refused with 409 because the Gateway still has sessions registered for this Director.</summary>
    public const string MoveSessionsOpen =
        "The Gateway still has sessions registered for this Director. Close every session on it, then move it. Nothing was changed.";

    // The Gateway's own sentences for the refusals that share a status (its contract: "the sentences are constants on
    // HostedEnrollmentEndpoint, so a client can match on them"). Copied, because the Director does not reference the
    // Gateway.
    private const string GatewaySomeoneElsesKey =
        "That Director was set up by a different account, so it cannot be moved from yours. Sign in with the account that set it up.";
    private const string GatewaySameTeam = "This Director is already set up for that team. Nothing was changed.";
    private const string GatewaySessionsOpen =
        "This Director still has sessions open. Close every session on it, then change its team. Nothing was changed.";

    /// <summary>
    /// The words a person reads for a refused move. 404 is always "no working key"; 403 and 409 each have more than
    /// one cause, told apart by the Gateway's own sentence. A refusal not named here - a team the person cannot run
    /// sessions in, the payment gate, a Director set up in two places - is shown in the Gateway's words, which are
    /// already written for a person.
    /// </summary>
    public static string MoveRefusalInPlainWords(HttpStatusCode status, string gatewayWords) => status switch
    {
        HttpStatusCode.NotFound => MoveNoWorkingKey,
        HttpStatusCode.Forbidden when gatewayWords == GatewaySomeoneElsesKey => MoveSomeoneElses,
        HttpStatusCode.Conflict when gatewayWords == GatewaySameTeam => MoveAlreadyThere,
        HttpStatusCode.Conflict when gatewayWords == GatewaySessionsOpen => MoveSessionsOpen,
        _ => gatewayWords,
    };

    /// <summary>What a move answers when the account token it holds is no longer accepted (review finding F9).</summary>
    public const string MoveSignInExpired =
        "Your DevThrottle sign-in has run out. Press \"Choose another team...\" to sign in again, then move.";

    /// <summary>
    /// Move this Director to another team (screen D3), with the account token a preceding
    /// <see cref="SignInAndListHostedTeamsAsync"/> captured. The Director is named by its own id
    /// (<c>{deviceId, teamId}</c>, the Gateway contract); the Gateway finds its working key on the signed-in
    /// account. Returns the new device key; the Gateway revokes the old one. The key is never logged. A refusal - a session is still registered, or the person cannot run sessions there - comes back
    /// in the Gateway's own words; an expired sign-in says how to sign in again. Stores nothing: the caller
    /// stores the team and the key.
    /// </summary>
    /// <param name="directorId">This Director's own id - the device id it was set up with.</param>
    /// <param name="teamId">The team to move to, or null for the personal account.</param>
    /// <param name="ct">Cancels the call.</param>
    public async Task<OperationResult<DirectorMoveAnswer>> MoveHostedDirectorAsync(string directorId, string? teamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            return OperationResult<DirectorMoveAnswer>.Fail("This Director has no id yet - it is still starting. Try again in a moment.");
        var tokens = _pendingTokens;
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<DirectorMoveAnswer>.Fail("Please sign in to DevThrottle first.");

        var hostedUrl = HostedGateway.ResolveUrl();
        EngineLog.Write($"[GatewayAccountEnrollRunner] MoveHostedDirectorAsync: hosted={hostedUrl}, director={directorId}, team={(teamId ?? "personal account")}");

        using var http = HostedClient(hostedUrl, tokens.AccessToken);
        var body = new JsonObject { ["deviceId"] = directorId, ["teamId"] = teamId };

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(HostedMoveRoute, body, WebJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] MoveHostedDirectorAsync transport FAILED: {ex.Message}");
            return OperationResult<DirectorMoveAnswer>.Fail(
                $"Could not reach the DevThrottle hosted gateway at {hostedUrl}. Please check your connection and try again.");
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] MoveHostedDirectorAsync refused: HTTP 401 (the held sign-in is no longer accepted)");
            return OperationResult<DirectorMoveAnswer>.Fail(MoveSignInExpired);
        }
        if (!resp.IsSuccessStatusCode)
        {
            var refusal = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
            EngineLog.Write($"[GatewayAccountEnrollRunner] MoveHostedDirectorAsync refused: HTTP {(int)resp.StatusCode}");
            return OperationResult<DirectorMoveAnswer>.Fail(MoveRefusalInPlainWords(resp.StatusCode, refusal));
        }

        // A 200 means the Gateway HAS moved the Director. A reply without a readable key is handed back as an empty
        // key, so the mover tells the person the move happened and the key did not arrive. Where the revoked key was
        // working is passed on exactly as the Gateway said it, or as nothing when it did not (review RM-F8).
        var reply = await ReadJsonAsync<DeviceRegistrationResponse>(resp, ct).ConfigureAwait(false);
        var movedFrom = reply?.MovedFrom is { } from ? new DirectorMovedFrom(string.IsNullOrWhiteSpace(from.TeamId) ? null : from.TeamId) : null;
        if (reply is null || string.IsNullOrWhiteSpace(reply.DeviceKey))
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] MoveHostedDirectorAsync: 2xx with no readable device key in reply");
            return OperationResult<DirectorMoveAnswer>.Ok(new DirectorMoveAnswer("", movedFrom));
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] MoveHostedDirectorAsync: moved; new device key received " +
                        $"({(movedFrom is null ? "the Gateway did not say what it left" : movedFrom.TeamId is null ? "left the personal account" : "left a team")})");
        return OperationResult<DirectorMoveAnswer>.Ok(new DirectorMoveAnswer(reply.DeviceKey, movedFrom));
    }

    // The hosted address and whether the Gateway there says it has Teams.
    private async Task<OperationResult<(string HostedUrl, bool TeamsReleased)>> HostedTeamsReleasedAsync(CancellationToken ct)
    {
        string hostedUrl;
        try
        {
            hostedUrl = HostedGateway.ResolveUrl();
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] HostedTeamsReleasedAsync FAILED: hosted address unusable: {ex.Message}");
            return OperationResult<(string, bool)>.Fail(ex.Message);
        }

        var released = await _teamsSignal.TeamsReleasedAsync(hostedUrl, ct).ConfigureAwait(false);
        if (!released.Success)
            return OperationResult<(string, bool)>.Fail(released.ErrorMessage!);
        EngineLog.Write($"[GatewayAccountEnrollRunner] HostedTeamsReleasedAsync: hosted={hostedUrl}, teams={released.Value}");
        return OperationResult<(string, bool)>.Ok((hostedUrl, released.Value));
    }

    // Sign in, keeping the token in memory for a follow-up call in this run. Returns the access token.
    private async Task<OperationResult<string>> SignInAsync(CancellationToken ct)
    {
        DevThrottleTokens tokens;
        try
        {
            tokens = await _signIn(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not-an-error: the sign-in was cancelled before a credential arrived (the person or the closing window stopped it); nothing failed
            EngineLog.Write("[GatewayAccountEnrollRunner] SignInAsync: sign-in cancelled before a credential arrived");
            return OperationResult<string>.Fail("Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] SignInAsync: sign-in FAILED: {ex.Message}");
            return OperationResult<string>.Fail(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken))
            return OperationResult<string>.Fail("Sign-in did not return a usable credential. Please try again.");

        _pendingTokens = tokens;
        return OperationResult<string>.Ok(tokens.AccessToken);
    }

    // GET the teams route, on a Gateway that SAID it has Teams. Any answer but 200 is an error, shown in the
    // Gateway's words - never read as "no teams", which would quietly put a team member's Director on their
    // personal account. An unreadable 200 is a failure too, not an exception.
    private async Task<OperationResult<IReadOnlyList<HostedTeam>>> ListHostedTeamsAsync(string hostedUrl, string accessToken, CancellationToken ct)
    {
        using var http = HostedClient(hostedUrl, accessToken);

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(HostedTeamsRoute, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] ListHostedTeamsAsync transport FAILED: {ex.Message}");
            return OperationResult<IReadOnlyList<HostedTeam>>.Fail(
                $"Could not reach the DevThrottle hosted gateway at {hostedUrl}. Please check your connection and try again.");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var refusal = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
            EngineLog.Write($"[GatewayAccountEnrollRunner] ListHostedTeamsAsync failed: HTTP {(int)resp.StatusCode}");
            return OperationResult<IReadOnlyList<HostedTeam>>.Fail(refusal);
        }

        var reply = await ReadJsonAsync<HostedTeamsReply>(resp, ct).ConfigureAwait(false);
        if (reply?.Teams is null)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] ListHostedTeamsAsync: 200 with an unreadable body");
            return OperationResult<IReadOnlyList<HostedTeam>>.Fail(
                "The DevThrottle hosted gateway answered the team list with something this Director cannot read.");
        }
        foreach (var team in reply.Teams)
        {
            if (TeamChoices.Unreadable(team) is { } problem)
            {
                EngineLog.Write($"[GatewayAccountEnrollRunner] ListHostedTeamsAsync: unreadable entry: {problem}");
                return OperationResult<IReadOnlyList<HostedTeam>>.Fail(
                    $"The DevThrottle hosted gateway listed {problem}, so this Director cannot offer the list.");
            }
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] ListHostedTeamsAsync: {reply.Teams.Count} team(s) listed");
        return OperationResult<IReadOnlyList<HostedTeam>>.Ok(reply.Teams);
    }

    // A JSON reply, or null when it is not readable as T.
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(WebJson, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] ReadJsonAsync FAILED: reply is not readable as {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }

    private HttpClient HostedClient(string hostedUrl, string accessToken)
    {
        var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
        http.BaseAddress = new Uri(hostedUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    // The Gateway's refusals are {"error": "<plain words>"}. Its words are shown as they are; a reply that does
    // not carry them is named for what it is, with its status, rather than guessed at.
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj && obj["error"] is JsonValue v
                && v.TryGetValue<string>(out var message) && !string.IsNullOrWhiteSpace(message))
                return message;
        }
        catch (JsonException)
        {
            // Not JSON: reported below with its status.
        }
        return $"The DevThrottle hosted gateway refused: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.";
    }

    /// <summary>
    /// Test that a gateway is actually reachable at a user-entered address BEFORE the install commits to it
    /// (issue #1233 - the mandatory Test gate). Accepts either a bare computer name plus <paramref name="port"/>
    /// (built into <c>http://name:port</c> - the primary manual form) or a full address pasted into
    /// <paramref name="computerNameOrUrl"/> (normalized, e.g. a Tailscale https URL). Probes GET /healthz with
    /// the configured timeout. On success it returns the RESOLVED url (the connect step then enrolls against
    /// exactly that); on failure it returns a clear, calm reason and NO url, so the installer never registers
    /// this machine against an address it could not reach. /healthz is unauthenticated, so the person can prove
    /// the address works first, then Connect (which signs in and enrolls). Never throws for an expected failure.
    /// </summary>
    /// <param name="computerNameOrUrl">A bare gateway computer name, or a full pasted gateway address.</param>
    /// <param name="port">The gateway port, used only when <paramref name="computerNameOrUrl"/> is a bare name.</param>
    /// <param name="ct">Cancels the probe.</param>
    public async Task<OperationResult<string>> TestGatewayAddressAsync(
        string? computerNameOrUrl, int port, CancellationToken ct = default)
    {
        var raw = (computerNameOrUrl ?? string.Empty).Trim();
        if (raw.Length == 0)
            return OperationResult<string>.Fail("Enter the gateway computer name and port.");

        // A pasted full address (has a scheme) is normalized as-is; anything else is a bare computer name
        // combined with the port. Both paths validate before we probe.
        string url;
        string? buildError;
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!GatewayAddress.TryNormalize(raw, out url, out buildError))
                return OperationResult<string>.Fail(buildError!);
        }
        else if (!GatewayAddress.TryFromComputerNameAndPort(raw, port, out url, out buildError))
        {
            return OperationResult<string>.Fail(buildError!);
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] TestGatewayAddressAsync: probing {url}/healthz");
        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
        var reason = await GatewayEndpointSelector.ProbeHealthzAsync(url, http, ct).ConfigureAwait(false);
        if (reason is null)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] TestGatewayAddressAsync: {url} is reachable");
            return OperationResult<string>.Ok(url);
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] TestGatewayAddressAsync: {url} not reachable: {reason}");
        return OperationResult<string>.Fail(
            $"Could not reach a gateway at {url}. Check the computer name and port, and that the gateway is running and not blocked by a firewall.");
    }

    /// <summary>
    /// The shared register-then-enroll-then-persist steps, run against an ALREADY-captured account token:
    /// (2) register this machine as an account device so the cloud issues its per-device key, (3) exchange
    /// that cloud key at the target gateway for a LOCAL device key via <c>/mobile/enroll</c>, and (4) persist the
    /// gateway URL + local key to config.json. Returns the issued <see cref="MobileEnrollmentResponse"/> on
    /// success, or a human-readable reason on any expected failure (no persist). The device keys are never
    /// logged (security rule DT-05).
    /// </summary>
    private async Task<OperationResult<MobileEnrollmentResponse>> RegisterAndEnrollAsync(
        DevThrottleTokens tokens, string url, string deviceId, string machineName, CancellationToken ct)
    {
        // 2. Register THIS machine as a device on the account (the same call the Gateway self-registers
        // with), so the cloud issues its per-device key. The account access token authorizes the call.
        CloudDeviceRegistrationResult cloud;
        try
        {
            using var cloudHttp = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
            var registry = new DeviceRegistryClient(cloudHttp);
            cloud = await registry.RegisterAsync(
                tokens.AccessToken,
                new CloudDeviceRegistrationRequest(deviceId, WorkstationPlatform, machineName, WorkstationDeviceType, AppVersion.Semver),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] RegisterAndEnrollAsync: cloud device registration FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "Signed in, but this workstation could not be registered on your DevThrottle account. Please check your connection and try again.");
        }

        if (string.IsNullOrWhiteSpace(cloud.DeviceKey))
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The account registered this workstation but returned no device key. Please try again.");

        // 3. Exchange the cloud device key at the target gateway for a LOCAL device key (the same
        // /mobile/enroll seam the phone and Cockpit use), then 4. persist the gateway URL + local key.
        var enroll = await EnrollAtGatewayAsync(url, cloud.DeviceKey, deviceId, machineName, ct).ConfigureAwait(false);
        if (!enroll.Success || enroll.Value is null)
            return enroll;

        _persist(url, enroll.Value.DeviceKey);
        EngineLog.Write($"[GatewayAccountEnrollRunner] RegisterAndEnrollAsync: persisted gateway url + local per-device key (machine={machineName})");
        return OperationResult<MobileEnrollmentResponse>.Ok(enroll.Value);
    }

    /// <summary>
    /// POST the cloud device key to the gateway's <c>/mobile/enroll</c> and map the reply to a
    /// success-or-reason result. A transport failure, a 403 (this workstation is not on the gateway's
    /// account), a 409 (the gateway itself is not signed in), any other non-2xx, or a 2xx with no local key
    /// all return a clear reason and NO key - so the install can never finish on an enrollment that did not
    /// actually issue a local device key. The device keys are never logged (security rule DT-05).
    /// </summary>
    private async Task<OperationResult<MobileEnrollmentResponse>> EnrollAtGatewayAsync(
        string gatewayUrl, string cloudDeviceKey, string deviceId, string machineName, CancellationToken ct)
    {
        var request = new MobileEnrollmentRequest
        {
            DeviceKey = cloudDeviceKey,
            DeviceId = deviceId,
            Name = machineName,
            Platform = WorkstationPlatform,
        };

        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _httpTimeout };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("mobile/enroll", request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync transport FAILED: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"Could not reach the gateway at {gatewayUrl}. Check the URL and that the gateway is running.");
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync rejected: HTTP 403 (workstation not on the gateway's account)");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "This workstation is signed in to a different DevThrottle account than the gateway. Sign in with the same account the gateway uses, then try again.");
        }
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync rejected: HTTP 409 (gateway not signed in)");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway is not signed in to a DevThrottle account yet. Sign in on the gateway host, then try again.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                $"The gateway refused the enrollment: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        MobileEnrollmentResponse? body;
        try
        {
            body = await resp.Content.ReadFromJsonAsync<MobileEnrollmentResponse>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync FAILED: could not read reply: {ex.Message}");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway accepted the sign-in but its reply could not be read.");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            EngineLog.Write("[GatewayAccountEnrollRunner] EnrollAtGatewayAsync: 2xx with no local device key in reply");
            return OperationResult<MobileEnrollmentResponse>.Fail(
                "The gateway accepted the sign-in but returned no device key.");
        }

        EngineLog.Write($"[GatewayAccountEnrollRunner] EnrollAtGatewayAsync: local per-device key issued for machine={machineName}");
        return OperationResult<MobileEnrollmentResponse>.Ok(body);
    }

    /// <summary>
    /// The default sign-in: stand up a <see cref="LoopbackLoginListener"/> on <c>127.0.0.1</c>, open the
    /// system browser at the DevThrottle sign-in address carrying the loopback callback as the
    /// <c>redirect_uri</c>, and wait for the browser to hand the account token pair back. Honors the
    /// caller's cancellation (the Cancel button) and a <see cref="DefaultSignInTimeout"/> deadline so an
    /// abandoned sign-in is never a dead end. The token value is never logged.
    ///
    /// With a <paramref name="display"/>, the address is shown before the browser is asked to open it, so it is
    /// on screen even when the shell opens nothing (issue #3504). The listener is already accepting at that point,
    /// so a person who takes the address to any browser completes the same sign-in - which is also why a shell
    /// that cannot open a browser at all does not end the sign-in when the address is on screen: the address is
    /// still a working way in, and the screen says the browser did not open. With no display the address is
    /// nowhere a person can see it, so that same failure ends the sign-in. The address is withdrawn when the wait
    /// ends, before the listener closes, because nothing answers at it after that.
    /// </summary>
    private static async Task<DevThrottleTokens> SignInViaBrowserAsync(
        ISignInAddressDisplay? display, Action<string> openBrowser, CancellationToken ct)
    {
        using var listener = new LoopbackLoginListener();
        var signInUrl = FirstRunLoginCoordinator.BuildSignInUrl(listener.CallbackUrl);
        EngineLog.Write($"[GatewayAccountEnrollRunner] SignInViaBrowserAsync: sign-in url={signInUrl}");

        if (display is null)
        {
            openBrowser(signInUrl);
            return await WaitForBrowserHandBackAsync(listener, ct).ConfigureAwait(false);
        }

        display.Show(signInUrl);
        try
        {
            try
            {
                openBrowser(signInUrl);
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[GatewayAccountEnrollRunner] SignInViaBrowserAsync FAILED: Windows could not open a browser: {ex.Message}; waiting for the address on screen");
                display.BrowserDidNotOpen(ex.Message);
            }
            return await WaitForBrowserHandBackAsync(listener, ct).ConfigureAwait(false);
        }
        finally
        {
            display.Withdraw();
        }
    }

    private static async Task<DevThrottleTokens> WaitForBrowserHandBackAsync(LoopbackLoginListener listener, CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(DefaultSignInTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        return await listener.WaitForCredentialAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Opens the user's default browser at the given address via the shell.</summary>
    private static void OpenSystemBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
