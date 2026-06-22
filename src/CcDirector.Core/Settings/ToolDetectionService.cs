using System.Diagnostics;
using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Settings;

/// <summary>Result of resolving an external command-line tool used by CC Director.</summary>
public sealed record ToolDetectResult(
    AgentKind Tool,
    string DisplayName,
    bool Found,
    string ConfiguredPath,
    string? ResolvedPath,
    string Source,
    string Message);

/// <summary>Result of launching a harmless version probe for an external tool.</summary>
public sealed record ToolTestResult(
    AgentKind Tool,
    string DisplayName,
    bool Ok,
    string Path,
    string? Version,
    string Message);

/// <summary>Last persisted safe-version-check result for one agent CLI.</summary>
public sealed record ToolValidationStatus(
    AgentKind Tool,
    string DisplayName,
    bool Ok,
    string Path,
    string? Version,
    string Message,
    DateTime TestedAtUtc,
    bool MatchesCurrentPath);

/// <summary>
/// UI-free discovery and version probing for the agent CLIs surfaced in Settings &gt; Tools.
/// The Settings dialog and startup/session preflight use the same <see cref="AgentOptions"/>
/// fields, so a detected path is the path Director will actually launch.
/// </summary>
public sealed class ToolDetectionService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public static readonly IReadOnlyList<AgentKind> SupportedTools = new[]
    {
        AgentKind.ClaudeCode,
        AgentKind.Pi,
        AgentKind.Codex,
        AgentKind.Gemini,
        AgentKind.OpenCode,
        AgentKind.Cursor,
        AgentKind.Grok,
        AgentKind.Copilot,
    };

    /// <summary>Detect the effective executable for Claude Code, Pi, Codex, Gemini, OpenCode, or Cursor.</summary>
    public ToolDetectResult DetectTool(AgentKind tool, AgentOptions options, string? overridePath = null)
    {
        FileLog.Write($"[ToolDetectionService] DetectTool: tool={tool}, overridePath={overridePath ?? "(null)"}");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var display = DisplayName(tool);
        var configured = string.IsNullOrWhiteSpace(overridePath) ? GetConfiguredPath(tool, options) : overridePath.Trim();
        var preferKnownCandidate = tool == AgentKind.Pi
            && string.Equals(configured, DefaultNpmCliPath("pi"), StringComparison.OrdinalIgnoreCase);

        if (preferKnownCandidate)
        {
            var known = ProbeKnownCandidates(tool, configured, display);
            if (known is not null)
                return known;
        }

        var resolved = ExecutableResolver.Resolve(configured);
        if (resolved is not null)
        {
            var source = Path.IsPathRooted(configured) || configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar)
                ? "configured path"
                : "PATH";
            FileLog.Write($"[ToolDetectionService] DetectTool: tool={tool}, resolved={resolved}, source={source}");
            return new ToolDetectResult(tool, display, true, configured, resolved, source, $"Found {display} at {resolved}.");
        }

        var detected = ProbeKnownCandidates(tool, configured, display);
        if (detected is not null)
            return detected;

        FileLog.Write($"[ToolDetectionService] DetectTool: tool={tool}, not found, configured={configured}");
        return new ToolDetectResult(tool, display, false, configured, null, "not found", $"{display} was not found. Configure its path or install it.");
    }

    /// <summary>Launch the tool with a harmless version command and summarize the result.</summary>
    public async Task<ToolTestResult> TestToolAsync(AgentKind tool, string path, CancellationToken ct = default)
    {
        FileLog.Write($"[ToolDetectionService] TestToolAsync: tool={tool}, path={path}");
        var display = DisplayName(tool);
        if (string.IsNullOrWhiteSpace(path))
            return new ToolTestResult(tool, display, false, "", null, "Enter a path first.");

        var resolved = ExecutableResolver.Resolve(path.Trim());
        if (resolved is null)
            return new ToolTestResult(tool, display, false, path.Trim(), null, $"{display} was not found at {path.Trim()}.");

        var versionArgs = VersionArguments(tool);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DefaultTimeout);

        try
        {
            var (exitCode, output) = await RunVersionCommandAsync(resolved, versionArgs, timeout.Token);
            var firstLine = FirstUsefulLine(output);
            if (exitCode == 0)
            {
                FileLog.Write($"[ToolDetectionService] TestToolAsync: tool={tool}, ok, version={firstLine ?? "(none)"}");
                return new ToolTestResult(tool, display, true, resolved, firstLine, firstLine is null
                    ? $"OK: {display} launched."
                    : $"OK: {display} reported {firstLine}.");
            }

            FileLog.Write($"[ToolDetectionService] TestToolAsync: tool={tool}, exitCode={exitCode}, output={Truncate(output, 300)}");
            return new ToolTestResult(tool, display, false, resolved, firstLine,
                $"{display} launched but exited with code {exitCode}: {Truncate(output, 500)}");
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[ToolDetectionService] TestToolAsync: tool={tool}, timed out");
            return new ToolTestResult(tool, display, false, resolved, null, $"{display} did not answer within {DefaultTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Build the config patch that records the latest safe version-check result.</summary>
    public static JsonObject BuildValidationPatch(ToolTestResult result)
    {
        FileLog.Write($"[ToolDetectionService] BuildValidationPatch: tool={result.Tool}, ok={result.Ok}, path={result.Path}");
        var key = ValidationKey(result.Tool);
        return new JsonObject
        {
            ["agent_status"] = new JsonObject
            {
                [key] = new JsonObject
                {
                    ["ok"] = result.Ok,
                    ["path"] = result.Path,
                    ["version"] = result.Version,
                    ["message"] = result.Message,
                    ["tested_at_utc"] = DateTime.UtcNow.ToString("O"),
                }
            }
        };
    }

    /// <summary>Read the latest persisted safe-version-check result for a tool.</summary>
    public static ToolValidationStatus? ReadValidationStatus(AgentKind tool, AgentOptions options)
    {
        FileLog.Write($"[ToolDetectionService] ReadValidationStatus: tool={tool}");
        if (options is null) throw new ArgumentNullException(nameof(options));

        var root = CcDirectorConfigService.ReadRaw();
        var status = root["agent_status"] as JsonObject;
        var node = status?[ValidationKey(tool)] as JsonObject;
        if (node is null) return null;

        bool GetBool(string key) => node[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        string GetString(string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        var testedAtText = GetString("tested_at_utc");
        DateTime.TryParse(testedAtText, out var testedAt);
        var storedPath = GetString("path");
        var matches = StoredPathMatchesCurrent(tool, options, storedPath);

        return new ToolValidationStatus(
            tool,
            DisplayName(tool),
            GetBool("ok"),
            storedPath,
            string.IsNullOrWhiteSpace(GetString("version")) ? null : GetString("version"),
            GetString("message"),
            testedAt == default ? DateTime.MinValue : testedAt.ToUniversalTime(),
            matches);
    }

    /// <summary>True when the latest persisted version-check succeeded for the current configured path.</summary>
    public static bool IsToolValidated(AgentKind tool, AgentOptions options)
    {
        FileLog.Write($"[ToolDetectionService] IsToolValidated: tool={tool}");
        var status = ReadValidationStatus(tool, options);
        return status is { Ok: true, MatchesCurrentPath: true };
    }

    /// <summary>Return the mutable <see cref="AgentOptions"/> path property for a supported tool.</summary>
    public static string GetConfiguredPath(AgentKind tool, AgentOptions options) => tool switch
    {
        AgentKind.ClaudeCode => options.ClaudePath,
        AgentKind.Pi => options.PiPath,
        AgentKind.Codex => options.CodexPath,
        AgentKind.Gemini => options.GeminiPath,
        AgentKind.OpenCode => options.OpenCodePath,
        AgentKind.Cursor => options.CursorPath,
        AgentKind.Grok => options.GrokPath,
        AgentKind.Copilot => options.CopilotPath,
        _ => throw new NotSupportedException($"[ToolDetectionService] Tool {tool} is not supported in Settings > Tools yet.")
    };

    /// <summary>Update the mutable <see cref="AgentOptions"/> path property for a supported tool.</summary>
    public static void SetConfiguredPath(AgentKind tool, AgentOptions options, string path)
    {
        FileLog.Write($"[ToolDetectionService] SetConfiguredPath: tool={tool}, path={path}");
        switch (tool)
        {
            case AgentKind.ClaudeCode:
                options.ClaudePath = path;
                break;
            case AgentKind.Pi:
                options.PiPath = path;
                break;
            case AgentKind.Codex:
                options.CodexPath = path;
                break;
            case AgentKind.Gemini:
                options.GeminiPath = path;
                break;
            case AgentKind.OpenCode:
                options.OpenCodePath = path;
                break;
            case AgentKind.Cursor:
                options.CursorPath = path;
                break;
            case AgentKind.Grok:
                options.GrokPath = path;
                break;
            case AgentKind.Copilot:
                options.CopilotPath = path;
                break;
            default:
                throw new NotSupportedException($"[ToolDetectionService] Tool {tool} is not supported in Settings > Tools yet.");
        }
    }

    public static string DisplayName(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.Pi => "Pi",
        AgentKind.Codex => "Codex",
        AgentKind.Gemini => "Gemini",
        AgentKind.OpenCode => "OpenCode",
        AgentKind.Cursor => "Cursor",
        AgentKind.Grok => "Grok",
        AgentKind.Copilot => "GitHub Copilot",
        _ => tool.ToString()
    };

    private static string VersionArguments(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "--version",
        AgentKind.Pi => "--version",
        AgentKind.Codex => "--version",
        _ => "--version"
    };

    private static ToolDetectResult? ProbeKnownCandidates(AgentKind tool, string configured, string display)
    {
        foreach (var candidate in KnownCandidates(tool))
        {
            var resolved = ExecutableResolver.Resolve(candidate);
            if (resolved is not null)
            {
                FileLog.Write($"[ToolDetectionService] DetectTool: tool={tool}, resolved={resolved}, source=detected");
                return new ToolDetectResult(tool, display, true, configured, resolved, "detected", $"Detected {display} at {resolved}.");
            }
        }

        return null;
    }

    private static IEnumerable<string> KnownCandidates(AgentKind tool)
    {
        if (tool == AgentKind.ClaudeCode)
        {
            yield return "claude";
            yield return DefaultNpmCliPath("claude");
        }
        else if (tool == AgentKind.Pi)
        {
            yield return @"D:\Tools\Pi\pi.exe";
            yield return DefaultNpmCliPath("pi");
            yield return "pi";
        }
        else if (tool == AgentKind.Codex)
        {
            yield return DefaultNpmCliPath("codex");
            yield return "codex";
        }
        else if (tool == AgentKind.Gemini)
        {
            yield return DefaultNpmCliPath("gemini");
            yield return "gemini";
        }
        else if (tool == AgentKind.OpenCode)
        {
            yield return DefaultNpmCliPath("opencode");
            yield return "opencode";
        }
        else if (tool == AgentKind.Cursor)
        {
            yield return "cursor-agent";
        }
        else if (tool == AgentKind.Grok)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".grok", "bin", "grok.exe");
            yield return "grok";
        }
        else if (tool == AgentKind.Copilot)
        {
            // npm global install drops copilot.cmd in %APPDATA%\npm; the bare "copilot" is the
            // PATH fallback (Homebrew/WinGet/gh.io installs put it on PATH directly).
            yield return DefaultNpmCliPath("copilot");
            yield return "copilot";
        }
    }

    private static string ValidationKey(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => "claude",
        AgentKind.Pi => "pi",
        AgentKind.Codex => "codex",
        AgentKind.Gemini => "gemini",
        AgentKind.OpenCode => "opencode",
        AgentKind.Cursor => "cursor",
        AgentKind.Copilot => "copilot",
        _ => tool.ToString().ToLowerInvariant(),
    };

    private static bool StoredPathMatchesCurrent(AgentKind tool, AgentOptions options, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return false;
        var configured = GetConfiguredPath(tool, options);
        var resolved = ExecutableResolver.Resolve(configured);
        return string.Equals(storedPath, resolved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(storedPath, configured, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultNpmCliPath(string binName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData) ? binName : Path.Combine(appData, "npm", binName + ".cmd");
    }

    private static async Task<(int ExitCode, string Output)> RunVersionCommandAsync(string resolvedPath, string arguments, CancellationToken ct)
    {
        var isCommandScript = OperatingSystem.IsWindows()
            && (resolvedPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || resolvedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var psi = isCommandScript
            ? new ProcessStartInfo("cmd.exe", $"/c \"\"{resolvedPath}\" {arguments}\"")
            : new ProcessStartInfo(resolvedPath, arguments);

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {resolvedPath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = (await stdoutTask + Environment.NewLine + await stderrTask).Trim();
        return (process.ExitCode, output);
    }

    private static string? FirstUsefulLine(string output)
        => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(no output)";
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }
}
