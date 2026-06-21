namespace CcDirector.Core.Account;

/// <summary>
/// The signed-in DevThrottle identity shown in the account area (issue #582): the user's email and
/// the authentication provider they signed in with. These are read locally from the cached access
/// token's claims - the account area never makes a network call to display them.
/// </summary>
/// <param name="Email">The signed-in user's email address (the <c>email</c> claim).</param>
/// <param name="Provider">The authentication provider the user signed in with (for example <c>google</c> or <c>github</c>).</param>
public sealed record AccountIdentity(string Email, string Provider);
