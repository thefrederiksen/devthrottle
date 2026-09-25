using CcRecorder.Recording;

namespace CcRecorder.Account;

/// <summary>
/// This phone's sign-in to the Gateway: where recordings go and the device key they upload with.
///
/// Sign in: open a listener on the phone itself, open devthrottle.com in the phone's browser, receive the
/// account token pair back on that listener, trade it ONCE at the Gateway's <c>/mobile/enroll</c> for this
/// phone's own device key, keep the key in Android's encrypted storage, and drop the account tokens. The
/// device key shows up on the account's device list and can be revoked on its own.
///
/// Recording never needs a sign-in: audio is always kept on the phone. Only uploading waits for one.
/// </summary>
public static class DeviceAccount
{
    // Older builds stored an editable Gateway address here. The recorder is hosted-only now; it is cleared.
    private const string PrefGatewayUrl = "gateway_url";

    // The pre-sign-in versions stored a hand-typed token in plain preferences. It is never read again.
    private const string LegacyPrefToken = "gateway_token";
    private const string PrefDeviceId = "device_id";
    private const string SecureDeviceKey = "gateway_device_key";

    // How long the app waits for the browser before giving up on a sign-in.
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The Gateway address: always the hosted Gateway. An address saved by an older build (the placeholder or a
    /// hand-typed one) is cleared, so the account token is never sent anywhere else.
    /// </summary>
    public static string GatewayUrl()
    {
        Preferences.Remove(PrefGatewayUrl);
        return RecorderDefaults.GatewayUrl;
    }

    /// <summary>This phone's device key, or null when the phone is not signed in.</summary>
    public static async Task<string?> DeviceKeyAsync()
    {
        Preferences.Remove(LegacyPrefToken);
        var key = await SecureStorage.Default.GetAsync(SecureDeviceKey);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>Forgets the device key. Recordings stay on the phone and upload after the next sign-in.</summary>
    public static Task SignOutAsync()
    {
        RecorderLog.Write("[DeviceAccount] SignOutAsync: device key removed from this phone");
        SecureStorage.Default.Remove(SecureDeviceKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the whole browser sign-in and stores the resulting device key. Throws
    /// <see cref="SignInFailedException"/> with a user-facing reason on every failure.
    /// </summary>
    public static async Task SignInAsync(CancellationToken ct)
    {
        var gateway = GatewayUrl();
        RecorderLog.Write($"[DeviceAccount] SignInAsync: starting, gateway={gateway}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);

        using var listener = new LoopbackSignInListener();
        var waiting = listener.WaitForTokensAsync(timeout.Token);

        var opened = await Browser.Default.OpenAsync(
            GatewayEnrollmentClient.BuildSignInUrl(listener.CallbackUrl, listener.State), BrowserLaunchMode.SystemPreferred);
        if (!opened)
            throw new SignInFailedException("The phone could not open a browser for sign-in.");

        SignInTokens tokens;
        try
        {
            tokens = await waiting;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SignInFailedException("Sign-in timed out after 10 minutes. Press Sign in to try again.");
        }

        // The browser is still in front, and Android keeps a backgrounded app off the network. Trade the
        // sign-in for the device key once the user is back here; the browser page tells them to come back.
        if (!AppForeground.IsForeground)
            RecorderLog.Write("[DeviceAccount] SignInAsync: sign-in received, waiting for the app to come back on screen");
        try
        {
            await AppForeground.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SignInFailedException("Sign-in was not finished: CC Recorder was not reopened within 10 minutes. Press Sign in to try again.");
        }

        var key = await new GatewayEnrollmentClient().EnrollAsync(
            gateway, tokens.AccessToken, DeviceId(), DeviceName(), ct);
        await SecureStorage.Default.SetAsync(SecureDeviceKey, key);
        RecorderLog.Write("[DeviceAccount] SignInAsync: signed in, device key stored (not logged)");
    }

    // The Gateway namespaces this id per account, so it only has to be stable on this phone.
    private static string DeviceId()
    {
        var id = Preferences.Get(PrefDeviceId, "");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = "ccrecorder-" + Guid.NewGuid().ToString("N");
            Preferences.Set(PrefDeviceId, id);
        }
        return id;
    }

    private static string DeviceName()
    {
#if ANDROID
        var model = global::Android.OS.Build.Model;
        return string.IsNullOrWhiteSpace(model) ? "CC Recorder" : $"CC Recorder ({model})";
#else
        return "CC Recorder";
#endif
    }
}
