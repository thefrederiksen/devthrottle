namespace CcDirector.Core.Account;

/// <summary>
/// The binding to the operating system credential store. The tokens are written here encrypted
/// at rest - on Windows through Windows Data Protection (the DPAPI, via
/// <c>System.Security.Cryptography.ProtectedData</c>); the macOS Keychain is a later
/// implementation of this same interface.
///
/// The store deals only in the encrypted token pair: it never exposes the raw bytes to disk in
/// plain text, and a reader on a different machine or user account cannot decrypt them.
/// </summary>
public interface IProtectedTokenStore
{
    /// <summary>True when a token pair is currently stored.</summary>
    bool HasTokens { get; }

    /// <summary>
    /// Encrypts and writes the token pair to the operating system credential store, replacing any
    /// existing entry.
    /// </summary>
    void Save(DevThrottleTokens tokens);

    /// <summary>
    /// Reads and decrypts the stored token pair, or returns null when nothing is stored or the
    /// stored entry cannot be decrypted (for example, copied from another machine).
    /// </summary>
    DevThrottleTokens? Load();

    /// <summary>Removes the stored token pair. A no-op when nothing is stored.</summary>
    void Clear();
}
