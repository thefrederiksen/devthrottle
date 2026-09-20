using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Git;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the CATALOG and DIRECTOR-LEVEL READ area of the tunnel command
/// surface. It owns the reads that are not addressed to one session (git status is per-session but grouped
/// here with the catalogs). Worker R2 filled it with: <c>git-status</c>, <c>coaching-categories</c>,
/// <c>claude-sessions</c>, <c>interrupted-list</c>, and <c>fs-list</c> (the list-directory read).
///
/// Each core is extracted verbatim from that read's Director REST lambda. That REST route now calls this
/// SAME core, so the tunnel verb and the route cannot drift (the core is the single source of truth); Phase 1
/// deletes the routes and leaves the cores reached only over the tunnel. A preserved try/catch is kept ONLY
/// where the source route had one (<c>fs-list</c>), so behaviour is byte-identical.
///
/// Gateway Cleanup Phase 0 (wave 3): the two reads R2 originally left out are now lifted, because the shared
/// dependency they needed was threaded through <see cref="SessionCommandServices"/>: <c>facts</c> stamps the
/// Director version (<see cref="SessionCommandServices.DirectorVersion"/>); <c>repos-list</c> reads the live
/// <see cref="RepositoryRegistry"/> (<see cref="SessionCommandServices.Repositories"/>). The <c>facts</c>
/// lift shipped with that dependency change; the wave-3 lift PR now adds <c>repos-list</c>, which reads that
/// same registry and lists nothing when it is absent (exactly as the REST route returned with no registry).
/// </summary>
internal sealed class CatalogReadExecutor : ISessionCommandArea
{
    /// <summary>
    /// The git-status provider, ONE shared instance (its own ten-second cache), exactly as the REST route
    /// held a single provider across requests. The REST route now reaches this same instance through the
    /// core, so both callers share the cache.
    /// </summary>
    private static readonly GitStatusProvider GitProvider = new();

    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "git-status",
        "coaching-categories",
        "claude-sessions",
        "interrupted-list",
        "fs-list",
        "facts",
        "repos-list",
        // Issue #1497: the machine's configured, enabled agents (one per kind) for the Cockpit New
        // Session dialog's agent picker - the remote counterpart of the desktop dialog's agent radios.
        "agents-list",
        // Gateway Cleanup CUT RESTORATION (SB-4a): the enriched per-repo overview the Repositories page reads.
        // Migrated late (the cut deleted the un-migrated leftover); it reads the live registry AND aggregates
        // live/history/claude/handover activity per repo. The core reproduces the old REST lambda verbatim so
        // the (Phase-4-DEFERRED) REST path and this tunnel verb share one core and cannot drift.
        "repos-overview",
        // Gateway Cleanup Phase 0 (Wave 4a): the two saved-handover-DOCUMENT reads. These read the saved
        // handover documents on this machine (DISTINCT from the per-session "handover" info verb). Each core
        // reproduces its old REST lambda verbatim, so the REST route and the tunnel verb share one core and
        // cannot drift.
        "handovers-list",
        "handovers-content",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return command.Verb switch
        {
            "git-status" => await GitStatus(context.SessionManager, command, cancellationToken),
            "coaching-categories" => CoachingCategories(),
            "claude-sessions" => ClaudeSessions(command),
            "interrupted-list" => InterruptedList(),
            "fs-list" => FsList(context.SessionManager, command),
            "facts" => Facts(context.DirectorId, context.Services?.DirectorVersion),
            "repos-list" => ReposList(context.Services?.Repositories),
            "agents-list" => AgentsList(context.SessionManager.Options),
            "repos-overview" => ReposOverview(context.SessionManager, context.Services?.Repositories),
            "handovers-list" => HandoversList(command),
            "handovers-content" => HandoversContent(command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the catalog read area"),
        };
    }

    /// <summary>
    /// The <c>facts</c> verb (director-level, no session): this machine's cc-tool inventory (names, categories,
    /// versions, built-state) plus the launcher presence fact. Mirrors the Director's <c>GET /facts</c>
    /// lambda exactly - always a 200 - and returns a serialized <see cref="DirectorFactsDto"/>. The Director
    /// version is the one dependency the tunnel command surface did not carry before; it now rides in
    /// <see cref="SessionCommandServices.DirectorVersion"/>, and the producing Director stamps its own version
    /// exactly as the REST route stamped <c>ControlApiHost._version</c>, so the value is identical on both paths.
    /// </summary>
    internal static DirectorCommandResult Facts(string directorId, string? version)
    {
        FileLog.Write("[CatalogReadExecutor] facts");
        var catalog = new ToolCatalogService();
        var tools = ToolInventory.Build(catalog, AboutInfo.InstalledComponents());
        var launcher = LauncherDiscovery.Read();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new DirectorFactsDto
        {
            DirectorId = directorId,
            MachineName = Environment.MachineName,
            Version = version ?? string.Empty,
            Tools = tools.Select(t => new ToolInventoryItemDto
            {
                Name = t.Name,
                Category = t.Category,
                Version = t.Version,
                IsBuilt = t.IsBuilt,
            }).ToList(),
            Launcher = new LauncherFactDto
            {
                Installed = launcher.Installed,
                Running = LauncherDiscovery.IsRunning(launcher),
                Version = launcher.Version,
                Error = launcher.Error,
            },
        }));
    }

    /// <summary>
    /// The <c>repos-list</c> verb (director-level, no session): the recent-repository picker list. Mirrors the
    /// Director's <c>GET /repos</c> lambda - always a 200 - and returns a serialized
    /// <see cref="List{RepositoryDto}"/> ordered by last-used descending. The live registry is the one
    /// dependency the tunnel command surface did not carry before wave 3; it rides in
    /// <see cref="SessionCommandServices.Repositories"/>. A null registry lists nothing (an empty array),
    /// exactly as the REST route returned when no registry was wired.
    ///
    /// A repository whose stored name is empty falls back to its folder name, and that name is read from the
    /// PATH rather than from whichever machine happens to be running this code -
    /// <see cref="RepositoryPaths.FolderName"/>. <c>Path.GetFileName</c> honours only the host's own
    /// separator, so a Windows path in a Director's registry read on macOS or Linux answers with the whole
    /// path where the reader expects a folder name. This list is the one the Cockpit, the phone and (from
    /// phase 3 of the one-repository-list mission) the Gateway's ordered union are all built out of, so a
    /// name that is wrong here is wrong on every screen at once.
    /// </summary>
    internal static DirectorCommandResult ReposList(RepositoryRegistry? repositories)
    {
        FileLog.Write("[CatalogReadExecutor] repos-list");
        if (repositories is null)
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(Array.Empty<RepositoryDto>()));

        var repos = repositories.Repositories
            .Select(r => new RepositoryDto
            {
                Name = string.IsNullOrEmpty(r.Name) ? RepositoryPaths.FolderName(r.Path) : r.Name,
                Path = r.Path,
                LastUsed = r.LastUsed,
            })
            .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
            .ToList();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(repos));
    }

    /// <summary>
    /// The <c>agents-list</c> verb (director-level, no session): this machine's configured, ENABLED agents,
    /// one per kind, for the Cockpit New Session dialog's agent picker (issue #1497). This is the remote
    /// counterpart of the desktop New Session dialog, which shows a radio per enabled configured agent and
    /// launches it with that agent's own default model - there is no model picker. Reads the same configured
    /// library the desktop dialog reads (<see cref="AgentEntryStore.LoadEntries"/>, first-run seeded by tool
    /// detection), keeps only enabled entries, and de-duplicates by kind because the create request selects an
    /// agent by KIND (<see cref="NewSessionRequest.Agent"/>) and the Director launches the first enabled entry
    /// of that kind - so a second entry of the same kind is a choice create cannot honor. Each choice carries a
    /// friendly model label resolved from the driver's known-models list, so the Cockpit can show which model
    /// the agent will use. Always a 200 (an empty list is a valid answer). Ordered by the configured order.
    /// </summary>
    internal static DirectorCommandResult AgentsList(AgentOptions options)
    {
        FileLog.Write("[CatalogReadExecutor] agents-list");
        var choices = new List<AgentChoiceDto>();
        var seenKinds = new HashSet<AgentKind>();
        foreach (var entry in AgentEntryStore.LoadEntries(options))
        {
            if (!entry.Enabled) continue;
            if (!seenKinds.Add(entry.Type)) continue; // one row per kind: create launches the first enabled of a kind
            choices.Add(new AgentChoiceDto
            {
                Type = entry.Type.ToString(),
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Type.ToString() : entry.DisplayName,
                DefaultModel = entry.DefaultModel ?? "",
                ModelLabel = ResolveModelLabel(entry.Type, entry.DefaultModel),
            });
        }
        FileLog.Write($"[CatalogReadExecutor] agents-list: {choices.Count} enabled agent kind(s)");
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(choices));
    }

    /// <summary>
    /// A friendly one-line label for a configured default model id, resolved from the kind's driver
    /// known-models list (e.g. "opus" -&gt; "Opus 4.8"). Falls back to the raw id when the driver does not
    /// list it, and to an empty string when no model is configured (the agent uses its own built-in default).
    /// </summary>
    private static string ResolveModelLabel(AgentKind kind, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "";
        if (!AgentPluginRegistry.Contains(kind)) return modelId;
        var driver = AgentPluginRegistry.Get(kind).Driver;
        if (driver is null) return modelId;
        var match = driver.KnownModels.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayName ?? modelId;
    }

    /// <summary>
    /// The <c>repos-overview</c> verb (director-level, no session): the enriched per-repository overview the
    /// Repositories page reads. Mirrors the Director's <c>GET /repos/overview</c> lambda verbatim - a null
    /// registry -&gt; an empty array, otherwise one <see cref="RepoOverviewDto"/> per registered repo with live
    /// session names, resumable/history counts, last-session summary, git branch, and handover counts, ordered
    /// by last-used descending. The live registry rides in <see cref="SessionCommandServices.Repositories"/>
    /// (as <c>repos-list</c> reads it) and the live sessions come from the producing Director's own
    /// <see cref="SessionManager"/>, so the value is identical on the REST path and this tunnel verb.
    ///
    /// The empty-name fallback reads the folder name from the path itself, for the reason given on
    /// <see cref="ReposList"/>.
    /// </summary>
    internal static DirectorCommandResult ReposOverview(SessionManager sessionManager, RepositoryRegistry? repositories)
    {
        FileLog.Write("[CatalogReadExecutor] repos-overview");
        if (repositories is null)
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(Array.Empty<RepoOverviewDto>()));

        // Aggregate every per-repo data source once, keyed by normalized path.
        var liveByRepo = sessionManager.ListSessions()
            .Where(s => s.ActivityState != ActivityState.Exited)
            .GroupBy(s => ControlEndpoints.NormalizeRepoPath(s.RepoPath))
            .ToDictionary(g => g.Key, g => g.Select(s => s.CustomName ?? ControlEndpoints.ProjectNameOf(s.RepoPath)).ToList());

        var historyByRepo = new SessionHistoryStore().LoadAll()
            .Where(h => !string.IsNullOrEmpty(h.RepoPath))
            .GroupBy(h => ControlEndpoints.NormalizeRepoPath(h.RepoPath))
            .ToDictionary(g => g.Key, g => g.ToList());

        var claudeByRepo = ClaudeSessionReader.ScanAllProjects()
            .Where(m => !string.IsNullOrEmpty(m.ProjectPath))
            .GroupBy(m => ControlEndpoints.NormalizeRepoPath(m.ProjectPath!))
            .ToDictionary(g => g.Key, g => g.ToList());

        var handoversByRepo = HandoverScanner.ScanAll()
            .SelectMany(h => h.RepoPaths.Select(p => (Repo: ControlEndpoints.NormalizeRepoPath(p), Handover: h)))
            .GroupBy(x => x.Repo)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Handover).ToList());

        var overview = repositories.Repositories.Select(r =>
        {
            var key = ControlEndpoints.NormalizeRepoPath(r.Path);
            liveByRepo.TryGetValue(key, out var liveNames);
            historyByRepo.TryGetValue(key, out var history);
            claudeByRepo.TryGetValue(key, out var claude);
            handoversByRepo.TryGetValue(key, out var handovers);

            var latestHistory = history?.OrderByDescending(h => h.LastUsedAt).FirstOrDefault();
            var latestClaude = claude?.OrderByDescending(m => m.Modified).FirstOrDefault();

            var lastHistoryAt = latestHistory is null || latestHistory.LastUsedAt == default
                ? (DateTime?)null
                : latestHistory.LastUsedAt.UtcDateTime;
            var lastClaudeAt = latestClaude is null || latestClaude.Modified == DateTime.MinValue
                ? (DateTime?)null
                : latestClaude.Modified;

            // Most recent activity wins; its summary describes the last session.
            var lastSessionAt = (lastHistoryAt ?? DateTime.MinValue) >= (lastClaudeAt ?? DateTime.MinValue)
                ? lastHistoryAt ?? lastClaudeAt
                : lastClaudeAt;
            var lastSummary = lastSessionAt == lastClaudeAt
                ? latestClaude?.Summary ?? latestClaude?.FirstPrompt ?? latestHistory?.FirstPromptSnippet
                : latestHistory?.FirstPromptSnippet ?? latestClaude?.Summary ?? latestClaude?.FirstPrompt;

            return new RepoOverviewDto
            {
                Name = string.IsNullOrEmpty(r.Name) ? RepositoryPaths.FolderName(r.Path) : r.Name,
                Path = r.Path,
                LastUsed = r.LastUsed,
                PathExists = Directory.Exists(r.Path),
                LiveSessionCount = liveNames?.Count ?? 0,
                LiveSessionNames = liveNames ?? new List<string>(),
                ResumableSessionCount = claude?.Count ?? 0,
                HistorySessionCount = history?.Count ?? 0,
                LastSessionAtUtc = lastSessionAt,
                LastSessionSummary = lastSummary,
                GitBranch = claude?.OrderByDescending(m => m.Modified)
                    .FirstOrDefault(m => !string.IsNullOrEmpty(m.GitBranch))?.GitBranch,
                HandoverCount = handovers?.Count ?? 0,
                LastHandoverUtc = handovers?.Max(h => h.DateUtc),
            };
        })
        .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
        .ToList();

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(overview));
    }

    /// <summary>
    /// The <c>git-status</c> verb: a read-only source-control snapshot for a session's repo. Mirrors the
    /// Director's <c>GET /sessions/{sid}/git</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns a serialized <see cref="GitSnapshot"/>. The summary fields come from
    /// <see cref="WingmanService.GitSnapshotAsync"/>; when the repo reads "ok" the snapshot is additively
    /// enriched with the per-file staged/unstaged lists from <see cref="GitStatusProvider"/>, exactly as the
    /// route did. The source route had no try/catch (GitSnapshotAsync owns its own), so none is added here.
    /// </summary>
    internal static async Task<DirectorCommandResult> GitStatus(SessionManager sessionManager, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var snap = await WingmanService.GitSnapshotAsync(session.RepoPath, cancellationToken);
        if (snap.Status == "ok")
        {
            var files = await GitProvider.GetStatusAsync(session.RepoPath);
            if (files.Success)
                GitChangeMapper.Enrich(snap, files);
        }
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(snap));
    }

    /// <summary>
    /// The <c>coaching-categories</c> verb: the coaching quick-launch cards (Assistant / Coach) with their
    /// Director-resolved on-disk paths. Mirrors the Director's <c>GET /coaching/categories</c> lambda - a
    /// static two-entry list, always a 200 - and returns a serialized <see cref="List{CoachingCategoryDto}"/>.
    /// No target session and no host state, so this always succeeds.
    /// </summary>
    internal static DirectorCommandResult CoachingCategories()
    {
        FileLog.Write("[CatalogReadExecutor] coaching-categories");
        var cats = new List<CoachingCategoryDto>
        {
            new()
            {
                Key = "assistant",
                Label = "Assistant",
                Description = "Tasks, contacts, daily briefing",
                Path = CcStorage.CoachingCategory("assistant"),
            },
            new()
            {
                Key = "coach",
                Label = "Coach",
                Description = "Life coaching across all domains",
                Path = CcStorage.CoachingCategory("coach"),
            },
        };
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(cats));
    }

    /// <summary>
    /// The <c>claude-sessions</c> verb: the resumable Claude Code sessions (the Resume Session tab). Mirrors
    /// the Director's <c>GET /claude-sessions</c> lambda - merges the workspace history entries (which carry
    /// custom name/color) with the Claude session-index metadata, de-duplicates by Claude session id, applies
    /// the optional <c>repo</c> filter from the payload, orders by last-used descending, and returns a
    /// serialized <see cref="List{ClaudeSessionDto}"/>. Always a 200 (there is no session to miss).
    /// </summary>
    internal static DirectorCommandResult ClaudeSessions(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<ClaudeSessionsRequest>(command.PayloadJson);
        var repo = request?.Repo;
        FileLog.Write($"[CatalogReadExecutor] claude-sessions: repo={repo}");

        var claudeMeta = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
        foreach (var cm in ClaudeSessionReader.ScanAllProjects())
            claudeMeta.TryAdd(cm.SessionId, cm);

        var history = new SessionHistoryStore().LoadAll();
        var dtos = new List<ClaudeSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // History entries first (they carry custom name/color), enriched with Claude metadata.
        foreach (var entry in history)
        {
            if (string.IsNullOrEmpty(entry.ClaudeSessionId) || !seen.Add(entry.ClaudeSessionId))
                continue;
            claudeMeta.TryGetValue(entry.ClaudeSessionId, out var meta);
            dtos.Add(new ClaudeSessionDto
            {
                ClaudeSessionId = entry.ClaudeSessionId,
                RepoPath = entry.RepoPath,
                ProjectName = ControlEndpoints.ProjectNameOf(entry.RepoPath),
                CustomName = entry.CustomName,
                CustomColor = entry.CustomColor,
                MessageCount = meta?.MessageCount ?? 0,
                Summary = meta?.Summary ?? meta?.FirstPrompt ?? entry.FirstPromptSnippet,
                LastUsedUtc = entry.LastUsedAt == default ? meta?.Modified : entry.LastUsedAt.UtcDateTime,
            });
        }

        // Then any Claude sessions not tracked by a workspace history entry.
        foreach (var meta in claudeMeta.Values)
        {
            if (string.IsNullOrEmpty(meta.SessionId) || !seen.Add(meta.SessionId))
                continue;
            dtos.Add(new ClaudeSessionDto
            {
                ClaudeSessionId = meta.SessionId,
                RepoPath = meta.ProjectPath ?? string.Empty,
                ProjectName = ControlEndpoints.ProjectNameOf(meta.ProjectPath ?? string.Empty),
                MessageCount = meta.MessageCount,
                Summary = meta.Summary ?? meta.FirstPrompt,
                LastUsedUtc = meta.Modified == DateTime.MinValue ? null : meta.Modified,
            });
        }

        if (!string.IsNullOrWhiteSpace(repo))
        {
            var wanted = ControlEndpoints.NormalizeRepoPath(repo);
            dtos = dtos.Where(d => !string.IsNullOrEmpty(d.RepoPath)
                                   && ControlEndpoints.NormalizeRepoPath(d.RepoPath) == wanted).ToList();
        }

        var ordered = dtos
            .OrderByDescending(d => d.LastUsedUtc ?? DateTime.MinValue)
            .ToList();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ordered));
    }

    /// <summary>
    /// The <c>interrupted-list</c> verb: the pending crash-journal recoveries this Director found on startup
    /// (dead Directors and their recoverable session rosters). Mirrors the Director's <c>GET /interrupted</c>
    /// lambda - a read of <see cref="DirectorCrashJournal.ListPendingRecoveries"/>, always a 200 - and returns
    /// a serialized <see cref="IReadOnlyList{DirectorCrashJournalData}"/>.
    /// </summary>
    internal static DirectorCommandResult InterruptedList()
    {
        FileLog.Write("[CatalogReadExecutor] interrupted-list");
        var pending = DirectorCrashJournal.ListPendingRecoveries();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(pending));
    }

    /// <summary>
    /// The <c>fs-list</c> verb (the list-directory read): the remote folder browser, CONTAINED to the
    /// working directories of the sessions this Director hosts. A null/blank payload path lists those
    /// allowed roots; a named path must resolve inside one of them or the listing is refused (the
    /// pre-hardening version listed drive roots and any directory on the machine, which on a hosted
    /// Gateway handed any active device key a full remote directory browse). Returns a serialized
    /// <see cref="DirectoryListingDto"/>. The source route wrapped the listing in a try/catch that
    /// turned any fault (a missing directory, an access error, and now an out-of-root refusal) into a
    /// 400 with the message, so that try/catch is preserved here and surfaced as a
    /// <see cref="DirectorCommandStatus.BadRequest"/>.
    /// </summary>
    internal static DirectorCommandResult FsList(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<FsListRequest>(command.PayloadJson);
        var path = request?.Path;
        FileLog.Write($"[CatalogReadExecutor] fs-list: path={path}");
        try
        {
            var allowedRoots = sessionManager.ListSessions().Select(s => s.WorkingDirectory);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ControlEndpoints.ListDirectory(path, allowedRoots)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CatalogReadExecutor] fs-list FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
        }
    }

    /// <summary>
    /// The <c>handovers-list</c> verb (director-level, no session): the saved handover DOCUMENTS on this
    /// machine (the Handovers tab), NOT the per-session handover info. Mirrors the Director's
    /// <c>GET /handovers</c> lambda - a scan of <see cref="HandoverScanner.ScanAll"/>, optionally filtered to
    /// documents whose repo paths include the payload <c>repo</c> (normalized comparison), always a 200 - and
    /// returns a serialized <see cref="List{HandoverDto}"/>. The source route had no try/catch, so none is
    /// added here.
    /// </summary>
    internal static DirectorCommandResult HandoversList(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<HandoversListRequest>(command.PayloadJson);
        var repo = request?.Repo;
        FileLog.Write($"[CatalogReadExecutor] handovers-list: repo={repo}");

        var infos = HandoverScanner.ScanAll();
        if (!string.IsNullOrWhiteSpace(repo))
        {
            var wanted = ControlEndpoints.NormalizeRepoPath(repo);
            infos = infos.Where(h => h.RepoPaths.Any(p => ControlEndpoints.NormalizeRepoPath(p) == wanted)).ToList();
        }
        var dtos = infos.Select(h => new HandoverDto
        {
            Path = h.Path,
            Title = h.Title,
            DateDisplay = h.DateDisplay,
            DateUtc = h.DateUtc,
            RepoPath = h.RepoPath,
            RepoPaths = h.RepoPaths,
            SessionName = h.SessionName,
        }).ToList();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(dtos));
    }

    /// <summary>
    /// The <c>handovers-content</c> verb (director-level, no session): the raw content of one saved handover
    /// document. Mirrors the Director's <c>GET /handovers/content</c> lambda - a null / blank path -&gt;
    /// BadRequest - and returns a serialized <see cref="HandoverContentDto"/>. The source route wrapped the
    /// read in a try/catch that turned an <see cref="UnauthorizedAccessException"/> into a 400 (surfaced here
    /// as <see cref="DirectorCommandStatus.BadRequest"/>) and a <see cref="FileNotFoundException"/> into a 404
    /// (surfaced here as <see cref="DirectorCommandStatus.NotFound"/>), so that try/catch is preserved here and
    /// behaviour is byte-identical.
    /// </summary>
    internal static DirectorCommandResult HandoversContent(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<HandoverContentRequest>(command.PayloadJson);
        var path = request?.Path;
        FileLog.Write($"[CatalogReadExecutor] handovers-content: path={path}");
        if (string.IsNullOrWhiteSpace(path))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path is required");
        try
        {
            var content = HandoverScanner.ReadContent(path);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverContentDto { Path = path, Content = content }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
        }
        catch (FileNotFoundException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "handover not found");
        }
    }
}
