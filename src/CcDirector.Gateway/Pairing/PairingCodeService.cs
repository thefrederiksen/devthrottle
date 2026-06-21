using System.Security.Cryptography;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// Mints and verifies the short-lived pairing code that authorizes a new device to enroll
/// (issue #469). The code is the Anchor-B grant: it is shown ONLY in the Gateway host's local
/// window and never crosses the network, so possession of the code proves local presence at the
/// gateway host (the root of trust).
///
/// Locked decisions (issue #469): the code is exactly 4 numeric digits, lives 5 minutes, and is
/// single-use (consumed on the first successful registration; invalid after expiry).
///
/// At most one code is active at a time: minting a fresh code replaces any prior one (clicking
/// "Register a new device" again starts over). The code is held in memory only - it is a transient
/// grant, never persisted, so a Gateway restart cancels any pending pairing.
///
/// Thread-safe: the corner-app UI thread mints/reads while the request thread verifies/consumes.
/// </summary>
public sealed class PairingCodeService
{
    /// <summary>How long a minted code stays valid before it expires.</summary>
    public static TimeSpan CodeLifetime { get; } = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Func<DateTime> _utcNow;

    private string? _code;
    private DateTime _expiresUtc;
    private bool _consumed;

    public PairingCodeService() : this(() => DateTime.UtcNow) { }

    /// <summary>Test seam: inject a clock so expiry is deterministic in unit tests.</summary>
    internal PairingCodeService(Func<DateTime> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Mint a fresh 4-digit pairing code, replacing any code currently active. Returns the code so
    /// the host window can display it; the code is never logged (it is a secret grant).
    /// </summary>
    public PairingCodeState Mint()
    {
        lock (_lock)
        {
            _code = GenerateFourDigitCode();
            _expiresUtc = _utcNow() + CodeLifetime;
            _consumed = false;
            FileLog.Write($"[PairingCodeService] Minted a 4-digit pairing code (expires {_expiresUtc:o})");
            return new PairingCodeState(_code, _expiresUtc);
        }
    }

    /// <summary>
    /// The currently active code's state for the host window to render, or null when no code is
    /// active (never minted, expired, or consumed). The UI never holds the code across ticks - it
    /// reads it here so a consumed/expired code disappears from the screen on the next tick.
    /// </summary>
    public PairingCodeState? Current()
    {
        lock (_lock)
        {
            if (_code is null || _consumed) return null;
            if (_utcNow() >= _expiresUtc)
            {
                FileLog.Write("[PairingCodeService] Active code expired");
                _code = null;
                return null;
            }
            return new PairingCodeState(_code, _expiresUtc);
        }
    }

    /// <summary>Cancel the active code (the host clicked Cancel). Idempotent.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (_code is null) return;
            FileLog.Write("[PairingCodeService] Active code cancelled");
            _code = null;
            _consumed = false;
        }
    }

    /// <summary>
    /// Verify a submitted code and, on success, atomically consume it so it can never be reused.
    /// Returns true only when the submitted code matches the active code, has not expired, and has
    /// not already been consumed. A failed verification leaves the active code untouched (a wrong
    /// guess does not burn the real code; an expired code is cleared).
    /// </summary>
    public bool TryVerifyAndConsume(string submittedCode)
    {
        if (string.IsNullOrEmpty(submittedCode)) return false;

        lock (_lock)
        {
            if (_code is null || _consumed)
            {
                FileLog.Write("[PairingCodeService] Verify rejected: no active code");
                return false;
            }
            if (_utcNow() >= _expiresUtc)
            {
                FileLog.Write("[PairingCodeService] Verify rejected: code expired");
                _code = null;
                return false;
            }
            // Constant-time compare so a wrong code reveals nothing through timing.
            var matches = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(_code),
                System.Text.Encoding.ASCII.GetBytes(submittedCode));
            if (!matches)
            {
                FileLog.Write("[PairingCodeService] Verify rejected: code mismatch");
                return false;
            }

            _consumed = true;
            _code = null;
            FileLog.Write("[PairingCodeService] Code verified and consumed");
            return true;
        }
    }

    /// <summary>Generate a uniformly-random 4-digit code (0000-9999) from a CSPRNG.</summary>
    private static string GenerateFourDigitCode()
    {
        var value = RandomNumberGenerator.GetInt32(0, 10000);
        return value.ToString("D4");
    }
}

/// <summary>An active pairing code and its expiry, for the host window to render (issue #469).</summary>
public sealed class PairingCodeState
{
    public PairingCodeState(string code, DateTime expiresUtc)
    {
        Code = code;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>The 4-digit code shown on the host screen.</summary>
    public string Code { get; }

    /// <summary>When the code expires (UTC).</summary>
    public DateTime ExpiresUtc { get; }
}
