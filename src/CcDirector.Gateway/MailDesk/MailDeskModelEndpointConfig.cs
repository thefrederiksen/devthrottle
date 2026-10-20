using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// Where the Mail Desk asks its model. Production asks the hosted Wingman endpoint
/// (<see cref="TranscriptionEndpointResolver.DevThrottleBaseUrl"/>) and nothing else. A throwaway Gateway on a test
/// rig may point the Mail Desk at a fake model on its own machine with one optional key in its config.json:
///
/// <code>
/// { "mailDeskModelBaseUrl": "http://127.0.0.1:7993/v1" }
/// </code>
///
/// The key is honoured ONLY when its host is a loopback address (<c>127.0.0.1</c>, <c>::1</c> or <c>localhost</c>),
/// so no config.json edit can send an account's mail to another server. Any other value - another host, a value that
/// is not an http or https address, an address with a user name, a query or a fragment, an empty string, or a value
/// that is not a string - refuses the Gateway's start with a sentence naming the key and the rule. Absent means the
/// hosted endpoint, so a production Gateway, which has no such key, is unchanged. The Gateway reads it once at
/// construction (Mail Desk phase 4, the rig; decided by the phase 4 Tech Lead on 19 October 2026).
/// </summary>
public static class MailDeskModelEndpointConfig
{
    /// <summary>The config.json key.</summary>
    public const string Key = "mailDeskModelBaseUrl";

    /// <summary>The loopback base URL in this machine's config.json, or null when there is none - then the Mail Desk
    /// asks the Wingman endpoint <see cref="MailDeskModelFactory"/> resolves. Throws when the key holds anything
    /// else.</summary>
    public static string? Read()
    {
        var configured = Parse(CcDirectorConfigService.ReadRaw());
        if (configured is null)
        {
            FileLog.Write($"[MailDeskModelEndpointConfig] Read: {Key} not set; the Mail Desk asks the hosted endpoint");
            return null;
        }
        FileLog.Write($"[MailDeskModelEndpointConfig] Read: {Key} is set; the Mail Desk asks the model on the loopback host {new Uri(configured).Host}");
        return configured;
    }

    /// <summary>The loopback base URL written in one config document, or null when the key is absent. Pure, so it is
    /// tested directly. Throws <see cref="InvalidOperationException"/> with the refusal sentence for any other value.</summary>
    public static string? Parse(JsonObject? root)
    {
        if (root is null || !root.TryGetPropertyValue(Key, out var node))
            return null;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            throw Refused("it is not a string");
        var text = value.GetValue<string>().Trim();
        if (text.Length == 0)
            throw Refused("it is empty");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw Refused("it is not an address");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw Refused($"its scheme is {uri.Scheme}, not http or https");
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw Refused("it holds a user name, a query or a fragment");
        if (!IsLoopbackHost(uri.Host))
            throw Refused($"its host {uri.Host} is not 127.0.0.1, ::1 or localhost");
        return text;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        var bare = host.TrimStart('[').TrimEnd(']');
        return IPAddress.TryParse(bare, out var address)
               && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback));
    }

    private static InvalidOperationException Refused(string why)
        => new($"The Gateway will not start: config.json's {Key} is honoured only as an http or https address on this " +
               $"machine's loopback host (127.0.0.1, ::1 or localhost), and {why}. Remove the key to use the hosted " +
               "endpoint, or point it at a model on this machine.");
}
