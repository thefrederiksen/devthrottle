using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Proof.ErrorReporting;

/// <summary>
/// THE DRIVER of the Error Logging mission's end-to-end proof (issue #3675), run by
/// <c>scripts/prove-error-reporting.ps1</c>. Each verb does ONE thing and writes its answer as JSON to the file
/// named by <c>--out</c> - never only to the console, because the read runs under <c>cc-secrets run</c>, which
/// crashes when a child prints a character outside ASCII (issue #3262).
///
///   components      - the component list, read from <see cref="ErrorReportLimits.Components"/>. The script never
///                     keeps its own list, so a surface added to the contract cannot be left out of the proof.
///   device          - a Director, launcher or self-hosted Gateway app error, through the REAL
///                     <see cref="ErrorReporter"/>: started as that component on this machine's own Gateway
///                     credential, an error line logged inside an <see cref="ErrorContext"/>, then the
///                     reporter's own flush. What is NOT exercised: the running app that would have logged it.
///   browser         - a Cockpit or phone error, as the browser shells send it to <c>POST /client-errors</c>, on
///                     this machine's Gateway credential. What is NOT exercised: the screen that would have shown it.
///   gateway-refusal - a prompt to a session no Director holds, sent on this session's own key. The Gateway refuses
///                     it with 404 and stores its OWN row through RecordRefusal (the phone's red box is stored by the same
///                     function, but the phone itself would be held, not refused). The correlation id is the
///                     Gateway's - no client can choose it - so the proof marker rides in the session id.
///   install         - a failed install step, through the installer's REAL <see cref="InstallFailureReporter"/>,
///                     from a throwaway install root so the machine's own install id and its hourly allowance are
///                     not touched. An install report has no correlation id; the marker rides in the step.
///   sanity          - one administrator read that must return at least one row. A read that cannot return a row
///                     would make every "did not arrive" meaningless, so the proof checks its instrument first.
///   read            - the administrator read of every named component since a moment, into one file.
///
/// Nothing here deletes or rewrites anything on the Gateway. Every row it causes carries the run's marker
/// (<c>errproof-...</c>), so the owner and the nightly reader can tell proof rows from real ones.
/// </summary>
public static class Program
{
    /// <summary>A credential-shaped value planted in the Director's proof error. The Gateway and the reporter scrub
    /// it; the script checks the stored message does not carry it.</summary>
    internal const string PlantedSecret = "PROOFplanted0secret0value0must0never0be0stored";

    private const string ProofSurface = "error reporting proof";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: prove-error-reporting <components|device|browser|gateway-refusal|install|sanity|read> [--option value ...]");
            return 2;
        }

        var verb = args[0];
        Dictionary<string, string> options;
        string outPath;
        try
        {
            options = ParseOptions(args.Skip(1).ToArray());
            outPath = Required(options, "out");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[prove-error-reporting] {verb}: {ex.Message}");
            return 2;
        }

        try
        {
            var result = verb switch
            {
                "components" => Components(),
                "device" => Device(Required(options, "component"), Required(options, "marker"), Required(options, "target"), Required(options, "logs")),
                "browser" => await BrowserAsync(Required(options, "component"), Required(options, "marker"), Required(options, "target")),
                "gateway-refusal" => await GatewayRefusalAsync(Required(options, "marker"), Required(options, "target")),
                "install" => await InstallAsync(Required(options, "marker"), Required(options, "target"), Required(options, "root")),
                "sanity" => await SanityAsync(Required(options, "target")),
                "read" => await ReadAsync(Required(options, "target"), Required(options, "since"), Required(options, "components")),
                _ => throw new ArgumentException($"unknown verb '{verb}'"),
            };
            Write(outPath, result);
            Console.WriteLine($"[prove-error-reporting] {verb}: {result["outcome"]}");
            return 0;
        }
        catch (Exception ex)
        {
            // The entry point's one catch: the failure is written where the script reads, and said, never swallowed.
            Write(outPath, new JsonObject { ["outcome"] = "failed", ["error"] = $"{ex.GetType().Name}: {ex.Message}" });
            Console.Error.WriteLine($"[prove-error-reporting] {verb} FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static JsonObject Components()
        => new() { ["outcome"] = "read", ["components"] = new JsonArray(ErrorReportLimits.Components.Select(c => (JsonNode)c).ToArray()) };

    private static JsonObject Device(string component, string marker, string target, string logs)
    {
        if (!ErrorReportLimits.DeviceComponents.Contains(component))
            throw new ArgumentException($"device takes one of: {string.Join(", ", ErrorReportLimits.DeviceComponents)}");

        // The self-hosted Gateway app reports only ever to the HOSTED Gateway (issue #3643) - the same wrapper the app uses.
        Func<GatewayConfig> config = component == ErrorReportLimits.GatewayApp
            ? ErrorReporter.HostedConnectionOnly(GatewayConfig.Load, () => target)
            : GatewayConfig.Load;
        if (Unusable(config(), target) is { } reason)
            return NotProven(reason);

        // This process's own log, kept out of the Director's log folder.
        FileLog.UseLogDirectory(logs, "prove-error-reporting");
        FileLog.Start();
        try
        {
            ErrorReporter.Start(component, config);
            var reporter = ErrorReporter.Current
                ?? throw new InvalidOperationException("ErrorReporter.Start ran and no reporter is current");
            using (ErrorContext.Begin(correlationId: marker, surface: ProofSurface,
                       action: $"prove that a {component} error reaches the error store", userVisible: true))
            {
                FileLog.Write($"[ProveErrorReporting] Trigger FAILED: a deliberate {component} error for the end-to-end reporting proof; planted credential token={PlantedSecret}");
            }
            // The same last send a dying process makes: it ignores the hourly budget and waits for a send in flight.
            ErrorReporter.FlushBeforeExit(TimeSpan.FromSeconds(30));
            var sent = reporter.Sent;
            var dropped = reporter.Dropped;
            return new JsonObject
            {
                ["outcome"] = sent >= 1 ? "sent" : "not-sent",
                ["sent"] = sent,
                ["dropped"] = dropped,
                ["triggered_how"] = $"ErrorReporter as {component}, this machine's credential, a FAILED line in an ErrorContext",
            };
        }
        finally
        {
            FileLog.Stop();
        }
    }

    private static async Task<JsonObject> BrowserAsync(string component, string marker, string target)
    {
        if (component is not (ErrorReportLimits.Cockpit or ErrorReportLimits.Mobile))
            throw new ArgumentException($"browser takes {ErrorReportLimits.Cockpit} or {ErrorReportLimits.Mobile}");
        var config = GatewayConfig.Load();
        if (Unusable(config, target) is { } reason)
            return NotProven(reason);

        // The body is the browser shells' ClientErrorReport (packages/client-core/src/errors/reportClientError.ts),
        // field for field, and nothing else - the route refuses an unknown field by name.
        var body = new JsonObject
        {
            ["component"] = component,
            ["surface"] = ProofSurface,
            ["action"] = $"prove that a {component} error reaches the error store",
            ["message"] = $"A deliberate {component} error for the end-to-end reporting proof",
            ["user_visible"] = true,
            ["exception_type"] = "ProofError",
            ["error_code"] = "error_reporting_proof",
            ["correlation_id"] = marker,
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, target.TrimEnd('/') + "/client-errors")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        using var response = await http.SendAsync(request);
        var status = (int)response.StatusCode;
        return new JsonObject
        {
            ["outcome"] = response.IsSuccessStatusCode ? "sent" : "refused",
            ["http_status"] = status,
            ["answer"] = Cut(await response.Content.ReadAsStringAsync(), 300),
            ["triggered_how"] = "POST /client-errors with the browser shells' report shape, this machine's credential",
        };
    }

    private static async Task<JsonObject> GatewayRefusalAsync(string marker, string target)
    {
        var url = Environment.GetEnvironmentVariable("CC_GATEWAY_URL")?.Trim() ?? "";
        var key = Environment.GetEnvironmentVariable("CC_GATEWAY_SESSION_KEY")?.Trim() ?? "";
        if (url.Length == 0 || key.Length == 0)
            return NotProven("needs a DevThrottle session's own key (CC_GATEWAY_URL and CC_GATEWAY_SESSION_KEY); run the script inside a session");
        if (!SameGateway(url, target))
            return NotProven($"this session's key belongs to {url}, not the target {target}");

        // A SESSION key, on purpose. With a device credential the Gateway HOLDS a prompt for a session it cannot find
        // and tries to deliver it later; with a session key it answers 404 at once and nothing is held or delivered.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{target.TrimEnd('/')}/sessions/{Uri.EscapeDataString(marker)}/prompt")
        {
            Content = JsonContent.Create(new { text = "error reporting proof: no session has this id" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request);
        var status = (int)response.StatusCode;
        var correlation = response.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.FirstOrDefault() ?? "" : "";
        return new JsonObject
        {
            ["outcome"] = status is 404 or >= 500 ? "refused-as-expected" : "unexpected-answer",
            ["http_status"] = status,
            ["correlation_id"] = correlation,
            ["answer"] = Cut(await response.Content.ReadAsStringAsync(), 300),
            ["triggered_how"] = "POST /sessions/<marker>/prompt on this session's key; the Gateway stores its own refusal",
        };
    }

    private static async Task<JsonObject> InstallAsync(string marker, string target, string root)
    {
        // A throwaway install root: its own install id, so the machine's real id and its ten-an-hour allowance are untouched.
        Directory.CreateDirectory(root);
        var layout = new InstallLayout(Path.GetFullPath(root));
        var reporter = new InstallFailureReporter(layout, "prove-error-reporting", gatewayUrl: () => target);
        var accepted = await reporter.ReportAsync("prove-error-reporting", marker,
            "A deliberate install failure for the end-to-end reporting proof", "no diagnostics: this is a proof row");
        return new JsonObject
        {
            ["outcome"] = accepted ? "sent" : "not-sent",
            ["install_id"] = InstallId.ReadOrCreate(layout.LocalRoot),
            ["triggered_how"] = "InstallFailureReporter.ReportAsync from a throwaway install root, step = marker",
        };
    }

    private static async Task<JsonObject> SanityAsync(string target)
    {
        var (status, answer) = await AdminGetAsync(target, "since=90d&limit=1");
        var returned = answer?["returned"]?.GetValue<int>() ?? -1;
        var first = answer?["errors"] is JsonArray { Count: > 0 } errors ? errors[0] : null;
        return new JsonObject
        {
            ["outcome"] = status == 200 && returned >= 1 && first?["component"] is not null ? "can-read-a-row" : "broken",
            ["http_status"] = status,
            ["returned"] = returned,
            ["total_matched"] = answer?["total_matched"]?.DeepClone(),
            ["first_component"] = first?["component"]?.DeepClone(),
            ["first_received_utc"] = first?["received_utc"]?.DeepClone(),
        };
    }

    private static async Task<JsonObject> ReadAsync(string target, string since, string components)
    {
        var reads = new JsonArray();
        foreach (var component in components.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (status, answer) = await AdminGetAsync(target,
                $"since={Uri.EscapeDataString(since)}&component={Uri.EscapeDataString(component)}&limit=500");
            reads.Add(new JsonObject
            {
                ["component"] = component,
                ["http_status"] = status,
                ["total_matched"] = answer?["total_matched"]?.DeepClone(),
                ["errors"] = answer?["errors"]?.DeepClone() ?? new JsonArray(),
            });
        }
        return new JsonObject { ["outcome"] = "read", ["reads"] = reads };
    }

    /// <summary>One administrator read, on the service token the caller put in ADMIN_SERVICE_TOKEN
    /// (<c>cc-secrets run admin-service-token -- ...</c>).</summary>
    private static async Task<(int Status, JsonNode? Answer)> AdminGetAsync(string target, string query)
    {
        var token = Environment.GetEnvironmentVariable("ADMIN_SERVICE_TOKEN")?.Trim() ?? "";
        if (token.Length == 0)
            throw new InvalidOperationException("ADMIN_SERVICE_TOKEN is not set. Run this verb as: cc-secrets run admin-service-token -- prove-error-reporting ...");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.TrimEnd('/')}/gateway/admin/director-errors?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"the administrator read answered {(int)response.StatusCode}: {Cut(text, 300)}");
        return ((int)response.StatusCode, JsonNode.Parse(text));
    }

    /// <summary>Why this machine's Gateway connection cannot carry a proof to <paramref name="target"/>, or null when it can.</summary>
    private static string? Unusable(GatewayConfig config, string target)
    {
        if (!config.HasCredential)
            return $"this machine has no Gateway credential for {target} (not signed in, or signed in elsewhere)";
        if (!SameGateway(config.Url, target))
            return $"this machine is signed in to {config.Url}, not the target {target}";
        return null;
    }

    private static bool SameGateway(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
           && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
           && x.Port == y.Port;

    private static JsonObject NotProven(string reason) => new() { ["outcome"] = "not-proven", ["reason"] = reason };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"options are --name value pairs; '{args[i]}' is not one");
            options[args[i][2..]] = args[++i];
        }
        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) && value.Trim().Length > 0
            ? value.Trim()
            : throw new ArgumentException($"--{name} is required");

    private static void Write(string path, JsonObject result)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // System.Text.Json escapes everything outside ASCII by default, so the file is ASCII whatever a message held.
        File.WriteAllText(full, result.ToJsonString(WriteOptions));
    }
}
