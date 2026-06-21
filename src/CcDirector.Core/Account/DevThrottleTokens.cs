namespace CcDirector.Core.Account;

/// <summary>
/// The access-plus-refresh token pair handed to the credential service by the login-completion
/// flow (a loopback listener or a device-code exchange). These are Supabase tokens: a signed,
/// short-lived access token (a JSON Web Token) plus a long-lived refresh token.
///
/// This pair is the unit the credential service receives, stores in the operating system
/// credential store, validates locally, and renews in the background. It is never written to
/// disk in plain text - the store encrypts it at rest.
/// </summary>
/// <param name="AccessToken">The signed access token (a JSON Web Token; validated locally for signature and expiry).</param>
/// <param name="RefreshToken">The long-lived refresh token used to renew the access token when connectivity is available.</param>
public sealed record DevThrottleTokens(string AccessToken, string RefreshToken);
