using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the agent-management REST surface under <c>/settings/agents</c> so an external agent,
/// a script, the Cockpit, or a remote machine can do everything the Settings dialog Agents tab
/// and its Add/Edit modal do - without the graphical dialog (issue #584). The generic
/// <c>GET/PUT /settings</c> routes in <see cref="SettingsEndpoint"/> can read and scalar-patch
/// config.json, but a deep merge-patch of the <c>agent.entries</c> ARRAY cannot reliably add one
/// agent, change one field of one agent, remove one, or reorder them - so the agent library needs
/// these dedicated routes:
///
///   Library (the <c>agent.entries</c> array):
///     GET    /settings/agents                 -> list every entry, in order.
///     GET    /settings/agents/{id}            -> one entry by its stable id.
///     POST   /settings/agents                 -> add a new entry; returns it with its assigned id.
///     PATCH  /settings/agents/{id}            -> update one or more values of one entry.
///     DELETE /settings/agents/{id}            -> remove one entry by id.
///     POST   /settings/agents/{id}/enabled    -> toggle/set one entry's enabled flag.
///     POST   /settings/agents/reorder         -> reorder the entries by a list of ids.
///
///   Parity sub-routes (reuse the SAME Core services the Agents tab uses):
///     POST   /settings/agents/detect          -> resolve a built-in tool's executable path
///                                                 (<see cref="ToolDetectionService.DetectTool"/>).
///     POST   /settings/agents/quick-check      -> probe a tool at a path and persist the validation
///                                                 status (<see cref="ToolDetectionService.TestToolAsync"/>).
///     POST   /settings/agents/command-line     -> the resolved launch command line (exe + effective
///                                                 args) for a configuration
///                                                 (<see cref="AgentToolConfig.ResolveEffectiveCommandLineArguments"/>).
///     GET    /settings/agents/catalog          -> the selectable types and, per type, the presets,
///                                                 the known models, the driver-detected default model,
///                                                 and whether the type supports model selection
///                                                 (<see cref="AgentToolCatalog"/> / <see cref="AgentDrivers"/>).
///
/// All writes persist through <see cref="AgentEntryStore.SaveEntries"/> -> the non-lossy
/// <see cref="CcDirectorConfigService.MergePatch"/> write authority, so unrelated config.json
/// sections are never clobbered. An invalid identifier or value is rejected with a clear error and
/// a non-success status code; config.json is left unchanged. Loopback-only and subject to the host's
/// auth middleware, exactly like the other routes (both are properties of the shared host app).
/// </summary>
internal static class AgentsEndpoint
{
    /// <summary>The patch body for PATCH /settings/agents/{id}: any field present is applied; an
    /// absent field is left unchanged. Strings are case-preserved; <see cref="Type"/> and
    /// <see cref="LaunchMode"/> are parsed against their enums and rejected when unrecognized.</summary>
    public sealed record AgentPatchRequest(
        string? DisplayName,
        string? Type,
        bool? Enabled,
        string? ExecutablePath,
        string? PresetId,
        string? DefaultModel,
        string? ArgsOverride,
        string? LaunchMode);

    /// <summary>The body for POST /settings/agents (add). Same fields as a patch; only the fields
    /// supplied are set, the rest take the entry's defaults.</summary>
    public sealed record AgentAddRequest(
        string? DisplayName,
        string? Type,
        bool? Enabled,
        string? ExecutablePath,
        string? PresetId,
        string? DefaultModel,
        string? ArgsOverride,
        string? LaunchMode);

    /// <summary>The body for POST /settings/agents/{id}/enabled.</summary>
    public sealed record EnabledRequest(bool Enabled);

    /// <summary>The body for POST /settings/agents/reorder: the entry ids in the desired order.</summary>
    public sealed record ReorderRequest(IReadOnlyList<string>? Ids);

    /// <summary>The body for POST /settings/agents/detect: which built-in type to resolve, and an
    /// optional caller-supplied path to honor (the modal's typed path).</summary>
    public sealed record DetectRequest(string? Type, string? Path);

    /// <summary>The body for POST /settings/agents/quick-check: which built-in type, at which path.</summary>
    public sealed record QuickCheckRequest(string? Type, string? Path);

    /// <summary>The body for POST /settings/agents/command-line: the configuration to resolve. The
    /// shape mirrors an entry's launch-affecting fields (the rest do not change the command line).</summary>
    public sealed record CommandLineRequest(
        string? Type,
        string? ExecutablePath,
        string? PresetId,
        string? DefaultModel,
        string? ArgsOverride,
        string? LaunchMode);

    public static void Map(IEndpointRouteBuilder app, AgentOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var detector = new ToolDetectionService();

        // ===== Library: the agent.entries array =====

        app.MapGet("/settings/agents", () =>
        {
            FileLog.Write("[AgentsEndpoint] GET /settings/agents");
            var entries = AgentEntryStore.LoadEntries(options);
            return Results.Json(new { agents = entries.Select(ToDto).ToList() });
        });

        app.MapGet("/settings/agents/{id}", (string id) =>
        {
            FileLog.Write($"[AgentsEndpoint] GET /settings/agents/{id}");
            var entry = AgentEntryStore.LoadEntries(options).FirstOrDefault(e => e.Id == id);
            if (entry is null)
                return Results.NotFound(new { error = $"no agent with id: {id}" });
            return Results.Json(ToDto(entry));
        });

        app.MapPost("/settings/agents", (AgentAddRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents: type={body?.Type ?? "(default)"}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required" });

            if (!TryParseType(body.Type, out var type, out var typeError))
                return Results.BadRequest(new { error = typeError });

            var entry = new AgentEntry { Type = type };
            ApplyType(entry, type);
            entry.DisplayName = body.DisplayName?.Trim() ?? ToolDisplayName(type);
            if (body.Enabled is { } enabled) entry.Enabled = enabled;
            if (body.ExecutablePath is not null) entry.ExecutablePath = body.ExecutablePath.Trim();
            if (body.PresetId is not null) entry.PresetId = body.PresetId;
            if (body.DefaultModel is not null) entry.DefaultModel = body.DefaultModel;
            if (body.ArgsOverride is not null) entry.ArgsOverride = body.ArgsOverride;
            if (!TryApplyLaunchMode(entry, body.LaunchMode, out var modeError))
                return Results.BadRequest(new { error = modeError });

            var entries = AgentEntryStore.LoadEntries(options);
            entries.Add(entry);
            AgentEntryStore.SaveEntries(entries);
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents: added id={entry.Id}, type={entry.Type}");
            return Results.Json(ToDto(entry), statusCode: StatusCodes.Status201Created);
        });

        app.MapMethods("/settings/agents/{id}", new[] { "PATCH" }, (string id, AgentPatchRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] PATCH /settings/agents/{id}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required" });

            var entries = AgentEntryStore.LoadEntries(options);
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
                return Results.NotFound(new { error = $"no agent with id: {id}" });

            if (body.Type is not null)
            {
                if (!TryParseType(body.Type, out var type, out var typeError))
                    return Results.BadRequest(new { error = typeError });
                entry.Type = type;
            }
            if (!TryApplyLaunchMode(entry, body.LaunchMode, out var modeError))
                return Results.BadRequest(new { error = modeError });

            if (body.DisplayName is not null) entry.DisplayName = body.DisplayName.Trim();
            if (body.Enabled is { } enabled) entry.Enabled = enabled;
            if (body.ExecutablePath is not null) entry.ExecutablePath = body.ExecutablePath.Trim();
            if (body.PresetId is not null) entry.PresetId = body.PresetId;
            if (body.DefaultModel is not null) entry.DefaultModel = body.DefaultModel;
            if (body.ArgsOverride is not null) entry.ArgsOverride = body.ArgsOverride;

            AgentEntryStore.SaveEntries(entries);
            FileLog.Write($"[AgentsEndpoint] PATCH /settings/agents/{id}: updated");
            return Results.Json(ToDto(entry));
        });

        app.MapDelete("/settings/agents/{id}", (string id) =>
        {
            FileLog.Write($"[AgentsEndpoint] DELETE /settings/agents/{id}");
            var entries = AgentEntryStore.LoadEntries(options);
            var removed = entries.RemoveAll(e => e.Id == id);
            if (removed == 0)
                return Results.NotFound(new { error = $"no agent with id: {id}" });

            AgentEntryStore.SaveEntries(entries);
            FileLog.Write($"[AgentsEndpoint] DELETE /settings/agents/{id}: removed");
            return Results.Json(new { removed = id, agents = entries.Select(ToDto).ToList() });
        });

        app.MapPost("/settings/agents/{id}/enabled", (string id, EnabledRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/{id}/enabled: enabled={body?.Enabled}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required: { \"enabled\": true|false }" });

            var entries = AgentEntryStore.LoadEntries(options);
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
                return Results.NotFound(new { error = $"no agent with id: {id}" });

            entry.Enabled = body.Enabled;
            AgentEntryStore.SaveEntries(entries);
            return Results.Json(ToDto(entry));
        });

        app.MapPost("/settings/agents/reorder", (ReorderRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/reorder: count={body?.Ids?.Count ?? 0}");
            if (body?.Ids is null)
                return Results.BadRequest(new { error = "request body is required: { \"ids\": [\"...\", ...] }" });

            var entries = AgentEntryStore.LoadEntries(options);
            var byId = entries.ToDictionary(e => e.Id);

            if (body.Ids.Count != entries.Count
                || body.Ids.Distinct().Count() != body.Ids.Count
                || body.Ids.Any(reqId => !byId.ContainsKey(reqId)))
            {
                return Results.BadRequest(new
                {
                    error = "ids must be a permutation of the current agent ids (every existing id exactly once)",
                    expected = entries.Select(e => e.Id).ToList(),
                });
            }

            var reordered = body.Ids.Select(reqId => byId[reqId]).ToList();
            AgentEntryStore.SaveEntries(reordered);
            FileLog.Write("[AgentsEndpoint] POST /settings/agents/reorder: applied");
            return Results.Json(new { agents = reordered.Select(ToDto).ToList() });
        });

        // ===== Parity sub-routes: Detect, Quick check, command line, catalog =====

        app.MapPost("/settings/agents/detect", async (DetectRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/detect: type={body?.Type}, path={body?.Path ?? "(none)"}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required: { \"type\": \"ClaudeCode\", \"path\"?: \"...\" }" });
            if (!TryParseType(body.Type, out var type, out var typeError))
                return Results.BadRequest(new { error = typeError });

            // Custom commands have nothing to detect - say so, mirroring the modal's Detect behavior.
            if (!ToolDetectionService.SupportedTools.Contains(type))
            {
                return Results.Json(new
                {
                    type = type.ToString(),
                    detectable = false,
                    found = false,
                    message = "Detect is only available for the built-in agent types; a custom command has nothing to detect.",
                });
            }

            var result = await Task.Run(() => detector.DetectTool(type, options, body.Path));
            return Results.Json(new
            {
                type = type.ToString(),
                detectable = true,
                found = result.Found,
                configuredPath = result.ConfiguredPath,
                resolvedPath = result.ResolvedPath,
                source = result.Source,
                message = result.Message,
            });
        });

        app.MapPost("/settings/agents/quick-check", async (QuickCheckRequest? body, CancellationToken ct) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/quick-check: type={body?.Type}, path={body?.Path ?? "(none)"}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required: { \"type\": \"ClaudeCode\", \"path\": \"...\" }" });
            if (!TryParseType(body.Type, out var type, out var typeError))
                return Results.BadRequest(new { error = typeError });
            if (!ToolDetectionService.SupportedTools.Contains(type))
                return Results.BadRequest(new { error = $"Quick check is only available for the built-in agent types; {type} is not one." });

            var result = await detector.TestToolAsync(type, body.Path?.Trim() ?? "", ct);
            // Persist the validation status exactly as the Agents tab does (issue #584 parity).
            CcDirectorConfigService.MergePatch(ToolDetectionService.BuildValidationPatch(result));
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/quick-check: type={type}, ok={result.Ok}, persisted validation");
            return Results.Json(new
            {
                type = type.ToString(),
                ok = result.Ok,
                path = result.Path,
                version = result.Version,
                message = result.Message,
            });
        });

        app.MapPost("/settings/agents/command-line", (CommandLineRequest? body) =>
        {
            FileLog.Write($"[AgentsEndpoint] POST /settings/agents/command-line: type={body?.Type}");
            if (body is null)
                return Results.BadRequest(new { error = "request body is required: { \"type\": \"ClaudeCode\", ... }" });
            if (!TryParseType(body.Type, out var type, out var typeError))
                return Results.BadRequest(new { error = typeError });
            if (!TryParseLaunchMode(body.LaunchMode, body.ArgsOverride, out var launchMode, out var modeError))
                return Results.BadRequest(new { error = modeError });

            var config = new AgentToolConfig
            {
                Tool = type,
                PresetName = body.PresetId ?? "",
                DefaultModel = body.DefaultModel ?? "",
                ArgsOverride = body.ArgsOverride?.Trim() ?? "",
                LaunchMode = launchMode,
            };

            // Mirror the Agents tab preview: when no executable is supplied, the type's bare command
            // name stands in (AgentEditorDialog.RefreshPreview).
            var exe = body.ExecutablePath?.Trim() ?? "";
            if (exe.Length == 0)
                exe = ToolDisplayName(type).ToLowerInvariant();

            var args = config.ResolveEffectiveCommandLineArguments();
            var commandLine = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
            return Results.Json(new
            {
                type = type.ToString(),
                executable = exe,
                arguments = args,
                commandLine,
            });
        });

        app.MapGet("/settings/agents/catalog", () =>
        {
            FileLog.Write("[AgentsEndpoint] GET /settings/agents/catalog");
            var types = TypeOptions.Select(option =>
            {
                var driver = AgentDrivers.For(option.Type);
                var supportsModel = driver.Capabilities.HasFlag(DriverCapabilities.ModelSelection);
                // Built-in types expose the catalog presets; a custom command has no catalog
                // entry, so it shows the same single "Custom (use args below)" placeholder the
                // modal's preset dropdown shows for an uncatalogued type (AgentEditorDialog).
                var presets = AgentToolCatalog.Contains(option.Type)
                    ? AgentToolCatalog.GetEntry(option.Type).Presets
                        .Select(p => new { name = p.Name, arguments = p.Arguments }).ToList()
                    : new[] { new { name = "Custom (use args below)", arguments = "" } }.ToList();

                var models = supportsModel
                    ? driver.KnownModels.Select(m => new
                    {
                        id = m.Id,
                        displayName = m.DisplayName,
                        description = m.Description,
                        badge = m.Badge,
                    }).ToList<object>()
                    : new List<object>();

                return new
                {
                    type = option.Type.ToString(),
                    displayName = option.Label,
                    detectable = ToolDetectionService.SupportedTools.Contains(option.Type),
                    supportsModelSelection = supportsModel,
                    modelFlag = driver.ModelFlag,
                    detectedDefaultModel = supportsModel ? driver.ReadConfiguredDefaultModel() : null,
                    presets,
                    models,
                };
            }).ToList();

            return Results.Json(new { types });
        });
    }

    // ----------------------------------------------------------------------------------------
    // Shaping helpers
    // ----------------------------------------------------------------------------------------

    /// <summary>The selectable agent types, matching the Agents tab modal's list exactly
    /// (AgentEditorDialog.TypeOptions): the full set including the Custom (RawCli) command.</summary>
    private static readonly IReadOnlyList<(AgentKind Type, string Label)> TypeOptions = new[]
    {
        (AgentKind.ClaudeCode, "Claude Code"),
        (AgentKind.Codex, "Codex"),
        (AgentKind.Gemini, "Gemini"),
        (AgentKind.Pi, "Pi"),
        (AgentKind.OpenCode, "OpenCode"),
        (AgentKind.Cursor, "Cursor"),
        (AgentKind.Grok, "Grok"),
        (AgentKind.Copilot, "GitHub Copilot"),
        (AgentKind.RawCli, "Custom"),
    };

    /// <summary>The wire shape of one agent entry: every value the Agents tab modal edits, plus the
    /// stable id, in camelCase like the rest of the Control API.</summary>
    private static object ToDto(AgentEntry e) => new
    {
        id = e.Id,
        displayName = e.DisplayName,
        type = e.Type.ToString(),
        enabled = e.Enabled,
        executablePath = e.ExecutablePath,
        presetId = e.PresetId,
        defaultModel = e.DefaultModel,
        argsOverride = e.ArgsOverride,
        launchMode = e.LaunchMode.ToString(),
    };

    /// <summary>Parse the requested type string against <see cref="AgentKind"/>; a missing or
    /// unrecognized value is a caller error (no silent default for an explicit bad value).</summary>
    private static bool TryParseType(string? raw, out AgentKind type, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            type = AgentKind.ClaudeCode;
            error = "type is required (one of: " + string.Join(", ", TypeOptions.Select(o => o.Type)) + ")";
            return false;
        }
        if (Enum.TryParse(raw, ignoreCase: true, out type) && Enum.IsDefined(type))
        {
            error = "";
            return true;
        }
        error = $"unrecognized type: {raw} (expected one of: {string.Join(", ", TypeOptions.Select(o => o.Type))})";
        return false;
    }

    /// <summary>Apply a launch-mode string to an entry, rejecting an unrecognized value. A null
    /// value leaves the entry's current mode unchanged (PATCH semantics).</summary>
    private static bool TryApplyLaunchMode(AgentEntry entry, string? raw, out string error)
    {
        if (raw is null)
        {
            error = "";
            return true;
        }
        if (!TryParseLaunchMode(raw, entry.ArgsOverride, out var mode, out error))
            return false;
        entry.LaunchMode = mode;
        return true;
    }

    /// <summary>Parse a launch-mode string against <see cref="LaunchMode"/>. An empty/whitespace
    /// value resolves via the same migration rule the store uses (override present => Custom); a
    /// non-empty unrecognized value is a caller error.</summary>
    private static bool TryParseLaunchMode(string? raw, string? argsOverride, out LaunchMode mode, out string error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            mode = AgentToolConfig.ParseLaunchMode("", argsOverride ?? "");
            error = "";
            return true;
        }
        if (Enum.TryParse(raw, ignoreCase: true, out mode) && Enum.IsDefined(mode))
        {
            error = "";
            return true;
        }
        mode = LaunchMode.Guided;
        error = $"unrecognized launchMode: {raw} (expected Guided or Custom)";
        return false;
    }

    /// <summary>Seed an added entry's preset and default model from the catalog for built-in types,
    /// matching what the Agents tab pre-populates when a user picks a type in the Add modal.</summary>
    private static void ApplyType(AgentEntry entry, AgentKind type)
    {
        if (!AgentToolCatalog.Contains(type))
            return;
        var catalog = AgentToolCatalog.GetEntry(type);
        entry.PresetId = catalog.DefaultPreset.Name;
        entry.DefaultModel = catalog.DefaultModel;
    }

    private static string ToolDisplayName(AgentKind type) =>
        TypeOptions.FirstOrDefault(o => o.Type == type).Label is { Length: > 0 } label
            ? label
            : type.ToString();
}
