using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Makes DevThrottle-hosted transcription "just work" for a signed-in user with zero configuration
/// (issue #881). After sign-in (and at Gateway startup for an already-signed-in install), it ensures a
/// DevThrottle inference key is present as the hosted-AI credential:
///
///   - If the vault already holds a <c>DEVTHROTTLE_API_KEY</c>, do nothing. That covers BOTH a manual
///     key the user pasted (the manual override wins) AND a previously auto-minted key (reuse across
///     restarts, so we never mint a fresh key on every boot - no key sprawl).
///   - Otherwise, mint one for the signed-in account (the account JWT authenticates the mint) and store
///     it in the vault, where the single transcription owner already reads it.
///
/// Best-effort throughout: it is fired from the best-effort post-sign-in hook and a detached startup
/// task, so any failure (offline, mint rejected) is logged and swallowed - the user simply sees the
/// add-credits / manual-key state until connectivity returns, and sign-in itself never fails.
///
/// The key value is never logged (security rule DT-05).
/// </summary>
public sealed class TranscriptionKeyAutoProvisioner
{
    /// <summary>
    /// The vault entry that records the stable id of an AUTO-MINTED inference key. Its presence marks the
    /// stored <c>DEVTHROTTLE_API_KEY</c> as one this Gateway minted (not a manual key), so sign-out can
    /// revoke it; a manually-pasted key has no id entry and is never revoked on sign-out.
    /// </summary>
    public const string InferenceKeyIdVaultName = "DEVTHROTTLE_API_KEY_ID";

    private readonly KeyVault _vault;
    private readonly Func<string?> _accessTokenProvider;
    private readonly IInferenceKeyMinter _minter;
    private readonly string _label;

    /// <summary>
    /// Serializes provisioning within this process. <see cref="EnsureAsync"/> is fired from BOTH the
    /// post-sign-in hook and a detached startup task, and those race on a fresh sign-in during startup.
    /// Without this gate the check-then-mint window lets every concurrent caller find an empty vault and
    /// mint its own key, so one Gateway leaks several live keys in the same second (issue #1136). The gate
    /// plus a re-read of the vault after acquiring it collapse the concurrent callers to exactly one mint.
    /// Never disposed: the provisioner lives for the whole process and this semaphore allocates no
    /// unmanaged handle unless its wait handle is used (it is not).
    /// </summary>
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    // The owning host's deployment signal, fixed at construction (see GatewayHostOptions).
    private readonly bool _hosted;

    /// <param name="vault">The Gateway key vault - where the transcription owner reads the hosted key.</param>
    /// <param name="accessTokenProvider">Supplies the signed-in account JWT (or null when not signed in);
    /// invoked fresh each call so it always reflects the current credential.</param>
    /// <param name="minter">Mints an inference key for the account.</param>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>): on hosted the
    /// provisioner is inert. REQUIRED - see <see cref="GatewayHostOptions"/>.</param>
    /// <param name="label">A recognisable name for the minted key (defaults to the machine name).</param>
    public TranscriptionKeyAutoProvisioner(
        KeyVault vault, Func<string?> accessTokenProvider, IInferenceKeyMinter minter, bool hosted, string? label = null)
    {
        _hosted = hosted;
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        _minter = minter ?? throw new ArgumentNullException(nameof(minter));
        _label = string.IsNullOrWhiteSpace(label) ? $"cc-director {Environment.MachineName}" : label!;
    }

    /// <summary>
    /// Ensures a DevThrottle inference key is present as the hosted transcription credential. Returns
    /// true only when this call MINTED and stored a new key; false when nothing was needed (a key was
    /// already present) or nothing could be done (not signed in, or the mint did not return a key).
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        // HOSTED-GATED. The global key vault is denied in whole on hosted (VaultEndpoints), and a deny on the
        // read route alone is not enough: this provisioner writes minted key material into that same global
        // vault on every sign-in and at startup, by a path that never touches the denied routes. On hosted it
        // is inert. The gate reads the deployment signal directly, so it cannot fail open by a caller omitting
        // an argument.
        if (_hosted)
        {
            FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: hosted - the global vault is denied on hosted, provisioning is inert");
            return false;
        }

        // Serialize the whole check-then-mint sequence so concurrent callers (the sign-in hook and the
        // startup task) mint at most one key between them (issue #1136).
        await _ensureGate.WaitAsync(ct);
        try
        {
            var existing = _vault.Get(TranscriptionEndpointResolver.DevThrottleKeyName);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: a DevThrottle key is already stored -> nothing to mint");
                return false;
            }

            var accessToken = _accessTokenProvider();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: not signed in (no access token) -> cannot mint yet");
                return false;
            }

            FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: no DevThrottle key stored and signed in -> minting an inference key");
            var minted = await _minter.MintAsync(accessToken, _label, ct);
            if (minted is null || string.IsNullOrWhiteSpace(minted.Key))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: mint returned no key -> leaving unset (user sees the add-credits/manual state)");
                return false;
            }

            // SetIfAbsent (not Set) guards a race with a concurrent manual save or another Gateway process
            // sharing this account - the first writer wins and we never clobber a key that appeared meanwhile.
            var stored = _vault.SetIfAbsent(TranscriptionEndpointResolver.DevThrottleKeyName, minted.Key);
            if (stored)
            {
                if (!string.IsNullOrWhiteSpace(minted.Id))
                    // Record the id so sign-out can revoke THIS auto-minted key (a manual key has no id and
                    // is never revoked). Set (not SetIfAbsent) because the id belongs to the key we stored.
                    _vault.Set(InferenceKeyIdVaultName, minted.Id!);
                FileLog.Write($"[TranscriptionKeyAutoProvisioner] EnsureAsync: minted inference key stored, id recorded={!string.IsNullOrWhiteSpace(minted.Id)}");
                return true;
            }

            // A key appeared between our re-read and the store (a concurrent manual save, or another Gateway
            // process on the same account). We hold a freshly minted key that nothing will use, so revoke it
            // now rather than leave it as a live orphan on the account (issue #1136). The revoke uses the SAME
            // account token that minted it, so there is no cross-account concern. Best-effort.
            if (!string.IsNullOrWhiteSpace(minted.Id))
            {
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: a key appeared first -> revoking the key we just minted to avoid an orphan");
                await _minter.RevokeAsync(accessToken, minted.Id!, ct);
            }
            else
            {
                // No id means we cannot revoke it; the just-minted key may remain on the account. Log it so
                // the orphan is visible rather than silent.
                FileLog.Write("[TranscriptionKeyAutoProvisioner] EnsureAsync: a key appeared first but the mint returned no id -> cannot revoke the just-minted key (it may remain on the account)");
            }
            return false;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    /// <summary>
    /// On sign-out, revokes the AUTO-MINTED inference key (if any) and clears it from the vault, so a
    /// signed-out install leaves no live key behind. A MANUALLY-pasted key (no recorded id) is left
    /// untouched - it is the user's own key, not ours to revoke. Best-effort: any failure is logged and
    /// swallowed so sign-out never fails; call it BEFORE the credential is cleared (it needs the JWT).
    ///
    /// The local key is ALWAYS cleared here (whether or not the cloud revoke succeeded): the stored key
    /// carries no account binding, so leaving it would let the NEXT account signed in on this machine
    /// reuse it (spending the wrong account's credits) and would keep a live credential on a signed-out
    /// machine. If the cloud revoke itself failed, the still-live cloud key can be revoked from the
    /// website; that residual orphan is the lesser evil versus cross-account reuse. Returns true when an
    /// auto-minted key was found and cleared.
    /// </summary>
    public async Task<bool> RevokeMintedKeyAsync(CancellationToken ct = default)
    {
        // HOSTED-GATED, for symmetry with EnsureAsync: on hosted this provisioner never minted or stored a
        // key (Ensure is inert), so there is nothing of ours in the global vault to revoke or clear. Return
        // early rather than issue Deletes against the hosted global vault.
        if (_hosted)
        {
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: hosted - provisioning is inert, nothing minted to revoke");
            return false;
        }

        var keyId = _vault.Get(InferenceKeyIdVaultName);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: no auto-minted key id -> nothing to revoke (manual key left untouched)");
            return false;
        }

        var accessToken = _accessTokenProvider();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            var revoked = await _minter.RevokeAsync(accessToken, keyId!, ct);
            FileLog.Write($"[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: cloud revoke result={revoked}");
        }
        else
        {
            FileLog.Write("[TranscriptionKeyAutoProvisioner] RevokeMintedKeyAsync: no access token to revoke with (clearing locally anyway)");
        }

        // Clear the local copies regardless of the cloud outcome: the stored key has no account binding, so
        // it must not remain on this machine to be reused by whoever signs in next (issue #1136). If the
        // cloud revoke failed, that key can still be revoked from the website.
        _vault.Delete(TranscriptionEndpointResolver.DevThrottleKeyName);
        _vault.Delete(InferenceKeyIdVaultName);
        return true;
    }
}
