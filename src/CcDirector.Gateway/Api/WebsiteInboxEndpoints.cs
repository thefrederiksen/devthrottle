using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE WEBSITE WRITES TO YOUR INBOX (Factory Inbox mission, brief E; the owner, 8 November 2026: "I would want all of mine
/// as in-mail, but we still need to support email"). The website (devthrottle.com, on Vercel) is neither a session nor
/// the owner's phone or Cockpit, so until now it could only email him. These routes give it the narrowest way in:
///
///   POST   /gateway/website/inbox      body {what, subject, bodyText | bodyHtml, key?}  -> 201 {messageId, message}
///                                      the website, with its own key. ADDS one message to the one account the key was
///                                      made for, shown from "DevThrottle website - &lt;what&gt;". It can do nothing else.
///   GET    /gateway/website-inbox-key  -> the settings card: where the key stands, in finished words
///   POST   /gateway/website-inbox-key  -> 201 the card, with the new key shown this once
///   DELETE /gateway/website-inbox-key  -> the card, the key revoked
///                                      the owner's own phone or Cockpit (a phone or browser device key) and nobody else.
///
/// THE KEY. Made by the owner, bound to his account when it is made, stored only as its SHA-256
/// (<see cref="WebsiteInboxKeyStore"/>). The write route is one exact path exempt from the host-wide gate
/// (<c>AuthMiddleware.PublicPaths</c>), exactly like the morning report's, and carries its own check: the bearer must be a
/// live website key, or it is 401. The account comes from the key's row; the body names none. No other route knows this
/// kind of key - it is not a device key, a session key or the machine token - so it opens nothing else. Making the key
/// takes a phone or browser key because a Director's key is what the command line on that Director uses: an agent could
/// otherwise make itself one. The plaintext is shown once and lives on Vercel; the Gateway holds no app setting for it,
/// so nothing here touches the deploy workflow.
///
/// WHAT IT WRITES. One unread message from the website, seat <c>what</c> - a closed list
/// (<see cref="YourInboxWords.WebsiteSeats"/>) - with no Reply and no push to the phone (the owner: the phone shows it in
/// the app). An HTML body becomes plain text with every link kept (<see cref="OwnerEmailText"/>). A subject over 200
/// characters or a body over Your inbox's limit is refused (413) so the website emails it instead, as it did before. At
/// most <see cref="DailyLimit"/> messages in any 24 hours per account (429), so a key that leaked cannot flood the inbox.
/// A retried run with the same <c>key</c> writes nothing new and is answered with the first message.
/// </summary>
internal static class WebsiteInboxEndpoints
{
    /// <summary>The website's write route. Exact-match public in <c>AuthMiddleware</c>; this route carries its own check.</summary>
    public const string WritePath = "/gateway/website/inbox";

    /// <summary>The owner's route for the key.</summary>
    public const string KeyPath = "/gateway/website-inbox-key";

    /// <summary>The most messages the website may write to one account in any 24 hours.</summary>
    public const int DailyLimit = 200;

    /// <summary>The longest retry key the website may give.</summary>
    public const int MaxWebsiteKeyLength = 200;

    /// <summary>The largest request body the write route reads, in bytes. Well above any message Your inbox can hold (an
    /// HTML body is turned into much shorter text), and far below what could tie up the Gateway. Over it is 413, and the
    /// website emails the message instead.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>What the routes read. Every function is required, so a wiring line that forgot one fails to compile.</summary>
    internal sealed record Parts(
        WebsiteInboxKeyStore Keys,
        YourInboxStore Inbox,
        Func<HttpContext, TenantId?> ResolveTenant,
        Func<DateTime> NowUtc);

    public static void Map(IEndpointRouteBuilder app, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        FileLog.Write($"[WebsiteInboxEndpoints] mapping {WritePath} (website key) and {KeyPath} (the owner's phone or browser)");

        app.MapPost(WritePath, async (HttpContext ctx, CancellationToken ct) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here.
            try
            {
                return await WriteAsync(parts, ctx, ct);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath} FAILED: {ex.Message}");
                return Error(500, "inbox_unavailable", $"Your inbox could not be written: {ex.Message}. Nothing was written.");
            }
        });

        app.MapGet(KeyPath, (HttpContext ctx) =>
        {
            FileLog.Write($"[WebsiteInboxEndpoints] GET {KeyPath}: identity={AuthMiddleware.IdentityKind(ctx)}");
            // A screen that is not the owner's phone or browser key - a self-hosted Cockpit on the machine token, say - is
            // told the truth as a card, not an error: the key is not made here, and why.
            if (AuthMiddleware.CallingSession(ctx) is null && !IsOwnerDevice(AuthMiddleware.AuthenticatedDevice(ctx)))
                return Results.Json(UnavailableCard(), JsonOpts);
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            return Results.Json(Card(parts.Keys.Status(tenant), null), JsonOpts);
        });

        app.MapPost(KeyPath, (HttpContext ctx) =>
        {
            FileLog.Write($"[WebsiteInboxEndpoints] POST {KeyPath}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out var device) is { } refused) return refused;
            var key = parts.Keys.Mint(tenant, device!.DeviceId, parts.NowUtc());
            return Results.Json(Card(parts.Keys.Status(tenant), key), JsonOpts, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete(KeyPath, (HttpContext ctx) =>
        {
            FileLog.Write($"[WebsiteInboxEndpoints] DELETE {KeyPath}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            return Results.Json(Card(parts.Keys.Revoke(tenant, parts.NowUtc()), null), JsonOpts);
        });
    }

    // ---------------------------------------------------------------- the website's write

    private sealed class WriteBody
    {
        [JsonPropertyName("what")] public string? What { get; set; }
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("bodyText")] public string? BodyText { get; set; }
        [JsonPropertyName("bodyHtml")] public string? BodyHtml { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
    }

    private static async Task<IResult> WriteAsync(Parts parts, HttpContext ctx, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        var resolved = parts.Keys.Resolve(presented);
        if (resolved.Tenant is not { } tenant)
        {
            FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath}: REFUSED (known={resolved.Known}, revoked={resolved.Revoked})");
            return Error(401, resolved.Revoked ? "website_key_revoked" : "website_key_required",
                resolved.Revoked
                    ? "This website key was revoked by the owner. Nothing was written. Make a new one in Settings, Website messages."
                    : "This route takes the website's own key, made by the owner in Settings, Website messages. Nothing was written.");
        }

        // The body is read with a cap, refused first on the length it declares and then while it is read, so a request
        // that claims nothing and sends a great deal is cut off too (as InstallReportEndpoints does).
        if (ctx.Request.ContentLength is > MaxBodyBytes)
            return Error(413, "request_too_large", $"The request is over {MaxBodyBytes} bytes. Nothing was written; send it as an email.");
        var raw = await ReadCappedAsync(ctx.Request.Body, MaxBodyBytes, ct);
        if (raw is null)
            return Error(413, "request_too_large", $"The request is over {MaxBodyBytes} bytes. Nothing was written; send it as an email.");

        WriteBody? body;
        try
        {
            body = JsonSerializer.Deserialize<WriteBody>(raw, JsonOpts);
        }
        catch (JsonException)
        {
            return Error(400, "bad_request_body", "The body must be JSON: {what, subject, bodyText or bodyHtml, key}. Nothing was written.");
        }
        if (body is null)
            return Error(400, "bad_request_body", "The body must be JSON: {what, subject, bodyText or bodyHtml, key}. Nothing was written.");

        var what = (body.What ?? "").Trim();
        if (!YourInboxWords.WebsiteSeats.All.Contains(what))
            return Error(400, "unknown_what",
                $"\"what\" is \"{body.What}\"; it must be one of {string.Join(", ", YourInboxWords.WebsiteSeats.All)}. Nothing was written.");
        var subject = (body.Subject ?? "").Trim();
        if (subject.Length == 0)
            return Error(400, "subject_empty", "The subject is empty. Nothing was written.");
        if (subject.Contains('\n') || subject.Contains('\r') || subject.Length > YourInboxStore.MaxSubjectLength)
            return Error(413, "subject_too_long",
                $"The subject must be one line of at most {YourInboxStore.MaxSubjectLength} characters for Your inbox. Nothing was written; send it as an email.");
        var text = !string.IsNullOrWhiteSpace(body.BodyText) ? body.BodyText! : OwnerEmailText.FromHtml(body.BodyHtml);
        if (string.IsNullOrWhiteSpace(text))
            return Error(400, "body_empty", "The body is empty. Write what the owner needs to know in bodyText or bodyHtml. Nothing was written.");
        if (text.Length > YourInboxStore.MaxBodyLength)
            return Error(413, "body_too_long",
                $"The body is {text.Length} characters and Your inbox holds at most {YourInboxStore.MaxBodyLength}. Nothing was written; send it as an email.");
        var websiteKey = string.IsNullOrWhiteSpace(body.Key) ? null : body.Key.Trim();
        if (websiteKey is { Length: > MaxWebsiteKeyLength })
            return Error(400, "key_too_long", $"\"key\" is longer than {MaxWebsiteKeyLength} characters. Nothing was written.");

        if (websiteKey is not null && parts.Inbox.WebsiteMessageOf(tenant, websiteKey) is { } already)
        {
            FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath}: key already written as {already.Id:D}; nothing new");
            return Written(already, duplicate: true);
        }

        var now = parts.NowUtc();
        var count = parts.Inbox.WebsiteCountSince(tenant, now.AddHours(-24));
        if (count >= DailyLimit)
        {
            FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath}: REFUSED, {count} website messages in 24 hours");
            return Error(429, "daily_limit",
                $"The website has written {count} messages to Your inbox in the last 24 hours, and the limit is {DailyLimit}. Nothing was written; send it as an email.");
        }

        var row = YourInboxStore.ForWebsite(tenant, what, subject, text, websiteKey, now);
        try
        {
            parts.Inbox.Add(tenant, row);
        }
        catch (DbUpdateException) when (websiteKey is not null && parts.Inbox.WebsiteMessageOf(tenant, websiteKey) is not null)
        {
            // Two runs with one key at once: the unique index let one through, and that one is the answer.
            var first = parts.Inbox.WebsiteMessageOf(tenant, websiteKey)!;
            FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath}: a run with the same key won the race ({first.Id:D})");
            return Written(first, duplicate: true);
        }
        parts.Keys.Touch(tenant, now);
        FileLog.Write($"[WebsiteInboxEndpoints] POST {WritePath}: wrote {row.Id:D} what={what} tenant={tenant.ToLogString()}");
        return Written(row, duplicate: false);
    }

    private static IResult Written(YourInboxMessageEntity m, bool duplicate) => Results.Json(new
    {
        messageId = m.Id.ToString("D"),
        duplicate,
        message = duplicate
            ? "This message was already in Your inbox from an earlier run with the same key; nothing new was written."
            : "Written to the owner's Your inbox. No email was sent.",
    }, JsonOpts, statusCode: duplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);

    // ---------------------------------------------------------------- the owner's key

    /// <summary>The owner: his own phone or browser device key. A Director's key, a session key and the machine token are
    /// refused.</summary>
    private static IResult? Owner(Parts parts, HttpContext ctx, out TenantId tenant, out DeviceCredentialIdentityRef? device)
    {
        tenant = default;
        device = null;
        if (AuthMiddleware.CallingSession(ctx) is not null)
            return Error(403, "session_key_out_of_scope", "Only the owner makes the website's key, from his phone or Cockpit. A session cannot.");
        var identity = AuthMiddleware.AuthenticatedDevice(ctx);
        if (identity is null || !IsOwnerDevice(identity))
        {
            FileLog.Write($"[WebsiteInboxEndpoints] owner route REFUSED: device type {identity?.DeviceType ?? "(none)"}");
            return Error(403, "owner_device_required",
                "The website's key is made only from the owner's phone or Cockpit. A Director's key cannot make one, because agents use it.");
        }
        if (parts.ResolveTenant(ctx) is not { } resolved)
            return Error(403, "no_account", "No DevThrottle account is bound to this request.");
        tenant = resolved;
        device = new DeviceCredentialIdentityRef(identity.DeviceId);
        return null;
    }

    /// <summary>The owner's own phone or browser key - the only keys that may make the website's key.</summary>
    private static bool IsOwnerDevice(DeviceCredentialIdentity? device)
        => device?.DeviceType is MobileDeviceEnrollmentService.PhoneDeviceType or MobileDeviceEnrollmentService.BrowserDeviceType;

    /// <summary>The one fact the mint needs from the owner's device.</summary>
    private sealed record DeviceCredentialIdentityRef(string DeviceId);

    /// <summary>
    /// THE SETTINGS CARD, every word folded here (CLAUDE.md rule 7): the card on the Cockpit and the phone prints these as
    /// they are. <paramref name="key"/> is the new key, given only in the answer that made it.
    /// </summary>
    internal static WebsiteInboxCardDto Card(WebsiteInboxKeyStatus status, string? key)
    {
        string line = status.State switch
        {
            WebsiteInboxKeyStore.States.Active => status.LastUsedAtUtc is { } used
                ? $"The website has a key, made {Format(status.CreatedAtUtc)}. It last wrote {Format(used)}."
                : $"The website has a key, made {Format(status.CreatedAtUtc)}. It has not written anything yet.",
            WebsiteInboxKeyStore.States.Revoked => $"The website's key was revoked {Format(status.RevokedAtUtc)}. Until you make a new one, the website emails you.",
            _ => "The website has no key, so it emails you. Make one and put it in the website's settings to have it write here instead.",
        };
        return new WebsiteInboxCardDto(
            Title: "Website messages",
            Explanation: "The DevThrottle website can tell you about the AI cost check, the weekly report, factory applications, "
                + "bookings and the creator report here in Your inbox instead of by email. It uses its own key, which can only add "
                + "a message to your inbox and can do nothing else.",
            State: status.State,
            StatusLine: line,
            CanMint: true,
            MintLabel: status.State == WebsiteInboxKeyStore.States.Active ? "Make a new key" : "Make a key",
            MintNote: status.State == WebsiteInboxKeyStore.States.Active ? "The old key stops working at once." : null,
            CanRevoke: status.State == WebsiteInboxKeyStore.States.Active,
            RevokeLabel: "Revoke the key",
            Key: key,
            KeyLine: key is null ? null
                : "Copy this key now; it is shown only once. Put it in the website's Vercel settings as WEBSITE_INBOX_KEY, then redeploy the website.");
    }

    /// <summary>
    /// THE CARD WHERE THE KEY CANNOT BE MADE: a screen signed in with something other than the owner's phone or browser
    /// key, such as a self-hosted Cockpit on this Gateway's machine token. It says so and offers no button.
    /// </summary>
    internal static WebsiteInboxCardDto UnavailableCard() => new(
        Title: "Website messages",
        Explanation: "The DevThrottle website can tell you about the AI cost check, the weekly report, factory applications, "
            + "bookings and the creator report in Your inbox instead of by email, with a key you make for it.",
        State: "unavailable",
        StatusLine: "Not available here. The website's key is made only from your phone or a Cockpit signed in to your DevThrottle "
            + "account, and this screen is signed in with this Gateway's own key. Open Settings on your phone to make it.",
        CanMint: false,
        MintLabel: "Make a key",
        MintNote: null,
        CanRevoke: false,
        RevokeLabel: "Revoke the key",
        Key: null,
        KeyLine: null);

    private static string Format(DateTime? utc) => utc is { } t ? t.ToString("d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "at a time not recorded";

    /// <summary>The body, or null when it is over <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IResult Error(int status, string code, string error)
        => Results.Json(new { error, code }, JsonOpts, statusCode: status);
}

/// <summary>The settings card for the website's key: every word finished on the Gateway.</summary>
/// <param name="Key">The new key, present only in the answer that made it.</param>
internal sealed record WebsiteInboxCardDto(
    string Title,
    string Explanation,
    string State,
    string StatusLine,
    bool CanMint,
    string MintLabel,
    string? MintNote,
    bool CanRevoke,
    string RevokeLabel,
    string? Key,
    string? KeyLine);
