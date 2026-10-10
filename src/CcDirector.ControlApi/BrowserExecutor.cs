using System.Text.Json.Nodes;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The DIRECTOR-LEVEL BROWSER area of the tunnel command surface: DevThrottle's automation browsers - the
/// drivable, signed-in-once Chromium instances an agent attaches to with browser-harness.
///
/// Remove-the-network-port mission, phase 2. These verbs existed only as loopback routes on the Director
/// (the old BrowserEndpoints routes), because the only caller was the <c>cc-devthrottle browser</c> command
/// line on the same machine and it reached them through the Director's own TCP port. That port is being
/// removed, so the command line now calls the Gateway, and the Gateway carries the command down the tunnel
/// to the Director - the same shape every other agent verb already has, just never built for these.
///
/// NOTHING ABOUT WHAT A BROWSER IS HAS CHANGED. A browser's debug port is loopback and its profile
/// directory is on one machine, so only the Director on THAT machine can start or drive it - and that is
/// still exactly what happens. The Gateway does not drive a browser; it carries a command to the Director
/// that does. The verbs are addressed to a DIRECTOR id for that reason: "the browsers on my machine" is
/// answered by the Director that owns the machine, never resolved from a name in a payload.
///
/// This class holds the ONE implementation. The BrowserEndpoints routes were briefly a thin adapter over it and then died with the Director's listener (Remove-the-network-port mission, phase 5)
/// rather than a second copy, so the loopback routes that still serve already-installed command lines
/// cannot drift from the tunnel verb that replaces them.
/// </summary>
internal sealed class BrowserExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "browsers-list",
        "browsers-create",
        "browsers-start",
        "browsers-stop",
        "browsers-signin",
        "browsers-rename",
        "browsers-attach",
        "browsers-delete",
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
        => ExecuteAsync(command.Verb, command.PayloadJson, cancellationToken);

    /// <summary>
    /// Run one browser verb. Split from <see cref="ExecuteAsync(SessionCommandContext, DirectorCommand, CancellationToken)"/>
    /// so the loopback adapter can call it without inventing a command envelope: these verbs need neither a
    /// session nor the Director's services, only the payload.
    /// </summary>
    internal static async Task<DirectorCommandResult> ExecuteAsync(string verb, string? payloadJson, CancellationToken ct)
    {
        // The browser id every per-browser verb names, read from the payload. It is a path segment on the
        // Gateway route and folded into the payload there, exactly as the queue verbs fold theirs.
        string BrowserId() => ReadString(payloadJson, "id") ?? "";

        switch (verb)
        {
            case "browsers-list":
            {
                var views = await AutomationBrowserViewFold.ListAsync(ct).ConfigureAwait(false);
                return Ok(new
                {
                    browsers = views,
                    harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled(),
                    harnessInstallUrl = AutomationBrowserViewFold.HarnessInstallUrl,
                });
            }

            case "browsers-create":
            {
                var name = ReadString(payloadJson, "name");
                if (string.IsNullOrWhiteSpace(name))
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "name is required");
                if (!Enum.TryParse<BrowserKind>(ReadString(payloadJson, "browser")?.Trim(), ignoreCase: true, out var kind))
                    // Listed from the enum so the error names every browser THIS build accepts, rather than
                    // an older pair somebody has to discover is out of date.
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                        $"unknown browser \"{ReadString(payloadJson, "browser")}\" - use one of: "
                        + string.Join(", ", Enum.GetNames<BrowserKind>().Select(n => n.ToLowerInvariant())));

                try
                {
                    var created = AutomationBrowserService.Create(name!, kind);
                    return Ok(await AutomationBrowserViewFold.FoldAsync(created, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
                }
            }

            case "browsers-start":
                return await WithBrowserAsync(BrowserId(), async b =>
                    Ok(await AutomationBrowserViewFold.FoldAsync(
                        await AutomationBrowserService.LaunchAsync(b.Id, ct).ConfigureAwait(false), ct).ConfigureAwait(false)));

            case "browsers-stop":
                return await WithBrowserAsync(BrowserId(), async b =>
                {
                    await AutomationBrowserService.StopAsync(b.Id, ct).ConfigureAwait(false);
                    return Ok(await AutomationBrowserViewFold.FoldAsync(b, ct).ConfigureAwait(false));
                });

            case "browsers-attach":
                return await WithBrowserAsync(BrowserId(), b =>
                {
                    var attach = AutomationBrowserRegistry.AttachInfoFor(b);
                    return Task.FromResult(Ok(new { buName = attach.BuName, buCdpUrl = attach.BuCdpUrl }));
                });

            case "browsers-signin":
                return await WithBrowserAsync(BrowserId(), async b =>
                {
                    // done:true records that the HUMAN finished signing in; otherwise launch and open the
                    // account page for them to do it by hand. Credentials are never automated.
                    if (ReadBool(payloadJson, "done") == true)
                        return Ok(await AutomationBrowserViewFold.FoldAsync(
                            AutomationBrowserService.MarkSignedIn(b.Id), ct).ConfigureAwait(false));

                    return Ok(await AutomationBrowserViewFold.FoldAsync(
                        await AutomationBrowserService.SignInAsync(b.Id, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
                });

            case "browsers-rename":
            {
                var to = ReadString(payloadJson, "name");
                if (string.IsNullOrWhiteSpace(to))
                    return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "name is required");
                return await WithBrowserAsync(BrowserId(), async b =>
                {
                    try
                    {
                        return Ok(await AutomationBrowserViewFold.FoldAsync(
                            AutomationBrowserService.Rename(b.Id, to!), ct).ConfigureAwait(false));
                    }
                    catch (InvalidOperationException ex)
                    {
                        return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
                    }
                });
            }

            case "browsers-delete":
                return await WithBrowserAsync(BrowserId(), async b =>
                {
                    try
                    {
                        await AutomationBrowserService.RemoveAsync(b.Id, ct).ConfigureAwait(false);
                        return Ok(new { removed = true, id = b.Id, name = b.Name });
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException)
                    {
                        // A folder that will not delete, or a browser that will not shut down, is a CONFLICT,
                        // not a server fault: something on this machine still holds it, and the caller can
                        // close that and try again.
                        return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
                    }
                });

            default:
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"verb '{verb}' is not handled by the browser area");
        }
    }

    /// <summary>Resolve the browser or fail NotFound naming it; run <paramref name="body"/> otherwise.</summary>
    private static async Task<DirectorCommandResult> WithBrowserAsync(string id, Func<AutomationBrowser, Task<DirectorCommandResult>> body)
    {
        var browser = AutomationBrowserRegistry.Find(id);
        if (browser is null)
        {
            FileLog.Write($"[BrowserExecutor] no automation browser \"{id}\" on this machine");
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound,
                $"No automation browser \"{id}\" on this machine.");
        }
        return await body(browser).ConfigureAwait(false);
    }

    private static DirectorCommandResult Ok(object body)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(body));

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json)?.AsObject(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // Read case-insensitively: the payload is written by a web-shaped client (camelCase) and by the
    // Gateway's own path fold, and a reader that insisted on one casing would return null for the other -
    // a body that parsed and said nothing, which is worse than one that failed.
    private static string? ReadString(string? json, string name)
    {
        var obj = Parse(json);
        if (obj is null) return null;
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value?.GetValue<string>();
        return null;
    }

    private static bool? ReadBool(string? json, string name)
    {
        var obj = Parse(json);
        if (obj is null) return null;
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                try { return kv.Value?.GetValue<bool>(); }
                catch (Exception) { return null; }
        return null;
    }
}
