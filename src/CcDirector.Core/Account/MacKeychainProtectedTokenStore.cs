using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// macOS implementation of the operating system credential store, backed by the login Keychain
/// through the <c>security</c> command-line tool. The token pair is serialized to JSON and stored as
/// a single generic-password item; the Keychain encrypts it at rest with the user's login key, so the
/// raw token string never sits in plain text on disk and only the same macOS user can read it back.
/// This is the macOS counterpart the <see cref="IProtectedTokenStore"/> summary named, standing beside
/// the Windows Data Protection store; the two present the identical Save / Load / Clear contract.
///
/// Threat model note: the value is passed to <c>security add-generic-password -w</c> as a process
/// argument. On macOS the argument vector of a process is visible only to the same user and to root -
/// the same principals who can already read this user's Keychain - so this matches the Windows store's
/// current-user protection scope rather than widening it.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainProtectedTokenStore : IProtectedTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The default Keychain service label under which the token pair item is stored.</summary>
    public const string DefaultService = "com.devthrottle.gateway.credential";

    /// <summary>The default Keychain account label for the token pair item.</summary>
    public const string DefaultAccount = "devthrottle-tokens";

    private readonly string _service;
    private readonly string _account;

    /// <summary>
    /// Creates the store. The default service and account labels address the one machine-wide Gateway
    /// credential; tests pass unique labels so a round-trip never touches the real login item.
    /// </summary>
    public MacKeychainProtectedTokenStore(string? service = null, string? account = null)
    {
        _service = string.IsNullOrWhiteSpace(service) ? DefaultService : service;
        _account = string.IsNullOrWhiteSpace(account) ? DefaultAccount : account;
        FileLog.Write($"[MacKeychainProtectedTokenStore] ctor: service={_service}, account={_account}");
    }

    public bool HasTokens => Load() is not null;

    public void Save(DevThrottleTokens tokens)
    {
        FileLog.Write("[MacKeychainProtectedTokenStore] Save: writing token pair to the login Keychain");

        if (tokens is null)
            throw new ArgumentNullException(nameof(tokens));
        if (string.IsNullOrEmpty(tokens.AccessToken))
            throw new ArgumentException("Access token is required", nameof(tokens));

        var json = JsonSerializer.Serialize(tokens, JsonOptions);

        // Replace semantics to match the Windows store's overwrite: remove any existing item first, then
        // add the new one. delete on a missing item is not an error here (there is simply nothing to
        // replace), which is why its exit code is not treated as a failure.
        RunSecurity("delete-generic-password", "-s", _service, "-a", _account);

        var (exit, _, stderr) = RunSecurity(
            "add-generic-password", "-s", _service, "-a", _account, "-U", "-w", json);
        if (exit != 0)
            throw new InvalidOperationException(
                $"security add-generic-password failed (exit {exit}): {stderr.Trim()}");

        FileLog.Write("[MacKeychainProtectedTokenStore] Save: token pair stored in the Keychain");
    }

    public DevThrottleTokens? Load()
    {
        var (exit, stdout, _) = RunSecurity(
            "find-generic-password", "-s", _service, "-a", _account, "-w");

        // Exit 44 (errSecItemNotFound territory) or any non-zero means nothing is stored - the same
        // "nothing to load" outcome the Windows store returns when the blob file is absent.
        if (exit != 0)
        {
            FileLog.Write("[MacKeychainProtectedTokenStore] Load: no stored token pair");
            return null;
        }

        var json = stdout.TrimEnd('\n', '\r');
        try
        {
            var tokens = JsonSerializer.Deserialize<DevThrottleTokens>(json, JsonOptions);
            FileLog.Write($"[MacKeychainProtectedTokenStore] Load: read token pair (hasTokens={tokens is not null})");
            return tokens;
        }
        catch (JsonException)
        {
            // A stored value that will not parse is treated as "nothing usable", matching the Windows
            // store's behavior when a blob cannot be decrypted. This is an explicit null, not a fallback.
            FileLog.Write("[MacKeychainProtectedTokenStore] Load FAILED: stored value did not parse; treating as none");
            return null;
        }
    }

    public void Clear()
    {
        FileLog.Write("[MacKeychainProtectedTokenStore] Clear: removing token pair from the Keychain");
        RunSecurity("delete-generic-password", "-s", _service, "-a", _account);
    }

    private static (int Exit, string StdOut, string StdErr) RunSecurity(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start /usr/bin/security");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }
}
