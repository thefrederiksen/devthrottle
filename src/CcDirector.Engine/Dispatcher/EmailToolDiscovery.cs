using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Engine.Dispatcher;

/// <summary>
/// Queries email tools at startup to discover which email addresses they can send from.
/// Each tool is expected to support: {tool} accounts list --json
/// </summary>
public static class EmailToolDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<EmailRoutingTable> DiscoverAsync(
        string binDirectory, IReadOnlyList<string> toolNames)
    {
        FileLog.Write($"[EmailToolDiscovery] Starting discovery in {binDirectory} for tools: [{string.Join(", ", toolNames)}]");

        var routes = new List<EmailRoute>();

        foreach (var toolName in toolNames)
        {
            var toolPath = Path.Combine(binDirectory, $"{toolName}.exe");
            if (!File.Exists(toolPath))
            {
                FileLog.Write($"[EmailToolDiscovery] WARNING: Tool not found: {toolPath}");
                continue;
            }

            var toolRoutes = await DiscoverToolAsync(toolName, toolPath);
            foreach (var route in toolRoutes)
            {
                if (routes.Any(r => r.EmailAddress.Equals(route.EmailAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    FileLog.Write($"[EmailToolDiscovery] Duplicate email {route.EmailAddress} from {toolName} -- first tool wins");
                    continue;
                }

                routes.Add(route);
                FileLog.Write($"[EmailToolDiscovery] Discovered: {route.EmailAddress} -> {route.ToolName} ({route.AccountName})");
            }
        }

        FileLog.Write($"[EmailToolDiscovery] Discovery complete: {routes.Count} routes");
        return new EmailRoutingTable(routes);
    }

    private static async Task<List<EmailRoute>> DiscoverToolAsync(string toolName, string toolPath)
    {
        var routes = new List<EmailRoute>();

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("accounts");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--json");

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                FileLog.Write($"[EmailToolDiscovery] Failed to start {toolName}");
                return routes;
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await stderrTask;

            using var cts = new CancellationTokenSource(Timeout);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                FileLog.Write($"[EmailToolDiscovery] {toolName} exited with code {process.ExitCode}: {stderr}");
                return routes;
            }

            var result = JsonSerializer.Deserialize<ToolAccountsResult>(stdout, JsonOptions);
            if (result?.Accounts == null)
            {
                FileLog.Write($"[EmailToolDiscovery] {toolName} returned null accounts");
                return routes;
            }

            foreach (var acct in result.Accounts)
            {
                if (!acct.CanSend)
                    continue;

                if (string.IsNullOrWhiteSpace(acct.Email))
                    continue;

                routes.Add(new EmailRoute(acct.Email, toolPath, toolName, acct.Name));
            }
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[EmailToolDiscovery] {toolName} FAILED: timed out after {Timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[EmailToolDiscovery] {toolName} FAILED: {ex.Message}");
        }

        return routes;
    }

    private sealed class ToolAccountsResult
    {
        public string? Tool { get; set; }
        public List<ToolAccount>? Accounts { get; set; }
    }

    private sealed class ToolAccount
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool Authenticated { get; set; }
        public bool CanSend { get; set; }
    }
}
