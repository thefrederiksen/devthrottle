using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The Director's shared session-DTO mapper and the pure helpers behind it.
///
/// This class used to map the Director's HTTP Control API routes. The Remove-the-network-port
/// mission deleted that listener - the Director accepts nothing inbound; every caller reaches it
/// through the Gateway, down the tunnel the Director itself dials out. What remains here is the
/// code those routes shared with the tunnel executors, kept in place so nothing about the wire
/// shape drifted when the routes went:
///
///   - <see cref="Map"/>, the ONE Session-to-SessionDto mapper. The stream client's snapshots and
///     deltas and every tunnel executor build their rows through it, so a pushed row equals what
///     any other path would have produced for the same session.
///   - The path-containment resolvers (<see cref="ResolveSessionFile"/>, <see cref="ResolveScreenshot"/>,
///     <see cref="ListDirectory"/>) the up-stream file and screenshot verbs decide with.
///   - The transcript helpers (<see cref="BuildTurnWidgetsFromHistory"/>, <see cref="ComputeTurnCount"/>)
///     and the repo-path canonicalizers the catalog reads share.
///
/// The class name is kept because it names the wire contract these helpers preserve, and because
/// the executors and their tests reference it throughout.
/// </summary>
internal static class ControlEndpoints
{

    /// <summary>Folder name of a repo path, for display fallback. Empty path -> "Unknown Project".</summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string ProjectNameOf(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath))
            return "Unknown Project";
        return Path.GetFileName(repoPath.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Canonical form for repo-path comparison across endpoints (?repo= filters, overview
    /// grouping): full path, trailing separators trimmed, lowercased (Windows paths are
    /// case-insensitive). Callers must filter null/empty first; Path.GetFullPath throws
    /// only on empty or embedded-NUL input, which the global error envelope surfaces loudly.
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string NormalizeRepoPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// List sub-directories of <paramref name="path"/> for the remote folder browser, contained to
    /// <paramref name="allowedRoots"/> - the working directories of the sessions this Director hosts.
    /// A null or empty path lists the allowed roots themselves; a named path must resolve INSIDE one
    /// of them or the listing is refused. The pre-hardening version listed the drive roots and any
    /// directory on the machine ("solo-tailnet: no path sandboxing"); that stood only while a single
    /// owner held every key, and on a hosted Gateway it handed any active device key a full remote
    /// directory browse. Same containment discipline as <see cref="ResolveSessionFile"/> and
    /// <see cref="ResolveScreenshot"/>: resolve fully, then refuse anything outside an allowed root.
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's fs-list core (the
    // list-directory read, lifted from GET /fs/list) calls the SAME helper - no drift, no duplication.
    internal static DirectoryListingDto ListDirectory(string? path, IEnumerable<string> allowedRoots)
    {
        // Normalize the roots once: full paths, trailing separators trimmed, duplicates collapsed.
        var roots = new List<string>();
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try { fullRoot = NormalizeDirectoryPath(Path.GetFullPath(root)); }
            catch (ArgumentException) { continue; } // a label, not a local directory (remote-thread sessions)
            if (!roots.Contains(fullRoot, StringComparer.FromComparison(PathContainmentComparison)))
                roots.Add(fullRoot);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var rootEntries = roots
                .Select(r => new DirEntryDto
                {
                    Name = Path.GetFileName(r) is { Length: > 0 } name ? name : r,
                    Path = r,
                    IsDrive = false,
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DirectoryListingDto { CurrentPath = null, ParentPath = null, Entries = rootEntries };
        }

        var full = NormalizeDirectoryPath(Path.GetFullPath(path));
        var containingRoot = roots.FirstOrDefault(r =>
            string.Equals(full, r, PathContainmentComparison)
            || full.StartsWith(ContainmentPrefix(r), PathContainmentComparison));
        if (containingRoot is null)
            throw new UnauthorizedAccessException($"directory is outside this Director's session working directories: {full}");

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"directory not found: {full}");

        // The containment test above is LEXICAL, and Path.GetFullPath never touches the filesystem, so a
        // link or junction planted under an allowed root keeps a legal prefix while the enumeration
        // below follows it anywhere on disk. Decide again on the REAL filesystem identity of both the
        // requested directory and the allowed roots (a root may itself be reached through a link), and
        // refuse an identity that cannot be established rather than following an unresolvable reparse
        // point.
        var realFull = ResolveRealPath(full);
        if (realFull is null)
            throw new UnauthorizedAccessException($"directory could not be resolved to a real path: {full}");
        var realFullTrimmed = NormalizeDirectoryPath(realFull);
        var containedForReal = roots
            .Select(ResolveRealPath)
            .Where(r => r is not null)
            .Select(r => NormalizeDirectoryPath(r!))
            .Any(r => string.Equals(realFullTrimmed, r, PathIdentityComparison)
                      || realFullTrimmed.StartsWith(ContainmentPrefix(r), PathIdentityComparison));
        if (!containedForReal)
            throw new UnauthorizedAccessException($"directory is outside this Director's session working directories: {full}");

        // Never offer navigation above the containing root: the root's own parent is not browsable.
        var parent = string.Equals(full, containingRoot, PathContainmentComparison)
            ? null
            : Directory.GetParent(full)?.FullName;

        var entries = Directory.EnumerateDirectories(full)
            .Select(d =>
            {
                try
                {
                    // Skip hidden / system directories so the picker stays clean.
                    var attr = File.GetAttributes(d);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                        return null;
                }
                catch { return null; }
                return new DirEntryDto { Name = Path.GetFileName(d), Path = d, IsDrive = false };
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DirectoryListingDto { CurrentPath = full, ParentPath = parent, Entries = entries };
    }

    /// <summary>
    /// The session's ENTIRE terminal buffer, ANSI stripped, for the "Ask the Wingman"
    /// answer path. Unlike WingmanContextBuilder's tail (capped at 4000 chars for a
    /// one-shot prompt), this returns everything: the read-only session writes it to a
    /// snapshot file and reads only as much as it needs, so "read me the whole article"
    /// can reach content that scrolled past a tail. Read-only inspection; never mutates.
    /// </summary>
    internal static string ReadFullCleanedBuffer(Session session)
    {
        try
        {
            var bytes = session.Buffer?.DumpAll();
            if (bytes is null || bytes.Length == 0) return "";
            return AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ReadFullCleanedBuffer FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>The session driver's capability flags as names, for UIs to render
    /// action buttons from (no per-agent special cases client-side).</summary>
    private static List<string> CapabilityNames(Session s)
    {
        var caps = s.Driver.Capabilities;
        return Enum.GetValues<Core.Drivers.DriverCapabilities>()
            .Where(f => f != Core.Drivers.DriverCapabilities.None && caps.HasFlag(f))
            .Select(f => f.ToString())
            .ToList();
    }

    /// <summary>
    /// Map a session to its DTO. Issue #335: machineName, user, tailnetEndpoint, and viewUrl
    /// are now populated by the Director itself (not patched in by the Gateway), so every
    /// consumer of the Director-local /sessions and /sessions/{sid} endpoints always sees
    /// the four identity fields. The Gateway aggregation pass still enriches DTOs from OLD
    /// Directors that send empty fields (back-compat for mixed-version fleets) but must NOT
    /// overwrite non-empty Director-supplied values.
    /// </summary>
    // Issue #1176 (Phase 1a): internal so the Director's stream client builds its pushed snapshots and
    // deltas through the EXACT same mapper the local /sessions endpoint uses (review #6), rather than a
    // second, divergent builder. Callers outside /sessions pass only (session, directorId); the Gateway
    // aggregator stamps machine/user/tailnet/view-url during aggregation, for pushed and pulled alike.
    internal static SessionDto Map(Session s, string directorId, TurnSummaryCache? cache = null,
        string machineName = "", string user = "", string tailnetEndpoint = "", string? gatewayUrl = null)
    {
        // Phase 3: StatusColor and LastStatusReason are owned by the SessionStatusWingman
        // and live on the Session itself. Map() reads them directly - no derivation, no
        // recomputation from TurnSummaryCache, no fallback. The `cache` argument is kept for
        // other endpoints that surface raw summaries; it is not consulted for color.
        // Idle clock source. Most agents go byte-silent when idle, so the raw last-write time is
        // the honest "how long since output" measure. Agents with an animated idle footer (Grok)
        // never go byte-silent - their footer repaints forever - so the raw clock would read ~0
        // even when the agent is done and waiting. For those, measure idle from the last screen-
        // BODY change instead (Session.LastBodyActivityAtUtc), matching the detector's content rule
        // so the idle seconds and the WaitingForInput badge agree.
        DateTime lastActivity;
        if (s.Driver.EmitsContinuousIdleOutput)
        {
            lastActivity = s.LastBodyActivityAtUtc;
        }
        else
        {
            var lastWrite = s.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
            lastActivity = lastWrite == DateTime.MinValue ? s.CreatedAt.UtcDateTime : lastWrite;
        }
        var idleSeconds = Math.Max(0, (DateTime.UtcNow - lastActivity).TotalSeconds);

        // Issue #335: build the viewUrl from the resolved tailnetEndpoint and the configured
        // gatewayUrl. Format: {tailnetEndpoint}/sessions/{sid}/view?gw={gatewayBase}
        // Only set when a real (non-empty) tailnetEndpoint resolved - never a loopback lie.
        // The prompt-delivery ledger for this session, read once for the row (issue internal#811).
        var deliveryTally = CcDirector.Core.Input.PromptDeliveryFailures.Tally(s.Id);

        var viewUrl = "";
        if (!string.IsNullOrEmpty(tailnetEndpoint))
        {
            var sidStr = s.Id.ToString();
            var base64 = tailnetEndpoint.TrimEnd('/');
            viewUrl = !string.IsNullOrEmpty(gatewayUrl)
                ? $"{base64}/sessions/{sidStr}/view?gw={Uri.EscapeDataString(gatewayUrl)}"
                : $"{base64}/sessions/{sidStr}/view";
        }

        return new()
        {
            SessionId = s.Id.ToString(),
            DirectorId = directorId,
            Agent = s.AgentKind.ToString(),
            GroupId = s.GroupId?.ToString(),
            GroupRole = s.GroupRole,
            RepoPath = s.RepoPath,
            Status = s.Status.ToString(),
            ActivityState = s.ActivityState.ToString(),
            // Issue #959: the raw crash fact. ActivityState says only "Exited", so without this the fold
            // cannot tell a crash from a clean exit. This ONE mapper feeds both the Gateway wire and the
            // desktop rail's own fold input, so stamping it here restores the deep-red on the rail, the
            // Cockpit and the phone together.
            Crashed = s.Crashed,
            // SessionDto.AssessedState is intentionally left null (defaults null): the Gateway-pushed
            // "assessed state" display annotation (issue #186) was retired with the Director's overlay
            // fold (issue #1177, Phase 2.3). Readers still do "AssessedState ?? ActivityState", so a null
            // here means they fall through to the raw ActivityState - the documented steady state.
            CreatedAt = s.CreatedAt.UtcDateTime,
            TotalBufferBytes = s.Buffer?.TotalBytesWritten ?? 0,
            IsAlternateScreen = s.IsAlternateScreen,
            ClaudeSessionId = s.ClaudeSessionId,
            ClaudeTranscriptPath = s.ClaudeTranscriptPath,
            BackendType = s.BackendType.ToString(),
            DriverCapabilities = CapabilityNames(s),
            Name = s.CustomName,
            Number = s.Number,
            // Mission attachment (fleet.html): the link + cached display name flow
            // straight through the Gateway aggregation on the SessionDto. Both values were STAMPED by what
            // the create or attach verb carried - the Mission record itself lives at the Gateway, and this
            // Director holds no mission store to resolve one from (issue #2629).
            MissionId = s.MissionId,
            MissionName = s.MissionName,
            // Workflow seat (Workflows mission, phase 5b): the run this session executes, with its
            // cached workflow id + pinned version - stamped at spawn from the Gateway-validated
            // create request.
            WorkflowRunId = s.WorkflowRunId,
            WorkflowId = s.WorkflowId,
            WorkflowVersion = s.WorkflowVersion,
            SortOrder = s.SortOrder,
            StatusColor = s.StatusColor,
            LastStatusReason = s.LastStatusReason,
            BriefingState = s.BriefingState.ToString(),
            RailLine = s.LatestBriefRailLine,
            LastActivityAt = lastActivity,
            IdleSeconds = idleSeconds,
            QuietThresholdSeconds = CcDirector.Core.Wingman.TerminalStateDetector.QuietThreshold.TotalSeconds,
            VoiceMode = s.VoiceMode,
            // The display mirror the Gateway wrote down to us, echoed back. The Gateway OVERWRITES this in
            // its fold from its own registry without reading it, so this is reported for the loopback
            // readers (the desktop) and not because anyone upstream believes it. A Director does not
            // decide hold.
            HoldState = s.HoldState.ToString(),
            // One of the two facts this Director contributes to hold - the other is ActivityState above.
            // Only a Director can see this: desktop typing never leaves the machine, and the origin is
            // known only at the input choke points. The Gateway rules on what it means.
            LastOwnerTurnAtUtc = s.LastOwnerTurnAtUtc,
            // Who started the current work (the Message Load mission): the Gateway's working edge spares an
            // armed snooze for agent-origin work only. A fact, like the one above; the Gateway rules on it.
            WorkingOrigin = s.WorkingOrigin,
            // Prompts that did not go (issue internal#811). Only this machine can see a delivery fail, so
            // the counts and the unresolved flag are reported here as FACTS; the Gateway folds the words.
            // Before this they existed solely as a line in a Director log file, which is how two spoken
            // prompts were lost on 2026-07-15 and nobody found out for two days.
            FailedPromptDeliveries = deliveryTally.FailedDeliveries,
            ComposerEchoMisses = deliveryTally.ComposerEchoMisses,
            LastPromptDeliveryFailureAtUtc = deliveryTally.LastFailureAtUtc,
            LastPromptDeliveryFailureReason = deliveryTally.LastFailureReason,
            PromptDeliveryUnresolved = deliveryTally.Unresolved,
            PendingDeletion = s.PendingDeletion,
            DeletionReason = s.DeletionReason,
            WingmanEnabled = s.WingmanEnabled,
            // Scheduled-run auto-dismiss (issue #1200): the flag + the agent's parsed verdict ride the
            // snapshot/delta path up to the Gateway's auto-dismiss sweep, which closes the session over the
            // stream on "done". Reported straight from the Session; the Gateway is the actor.
            AutoDismiss = s.AutoDismiss,
            DismissVerdict = s.DismissVerdict,
            // Raw local facts for the Gateway color fold (issue #1177, Phase 2). Reported straight from
            // the Session; the Director does NOT fold them into a color here (StatusColor is unchanged).
            IsBrandNew = s.IsBrandNew,
            IsControlled = s.IsControlled,
            ControllerSessionId = s.ControllerSessionId?.ToString(),
            // Session origin and lineage (devthrottle_internal issue #982): the birth facts, reported
            // straight from the Session. They ride every push, not just the first, because the durable
            // history row is written on FIRST SIGHT and a push that omitted them would be the one that
            // created the row. Nothing paints from these - they are the record, and the tree.
            OriginKind = s.OriginKind,
            OriginSurface = s.OriginSurface,
            ParentSessionId = s.ParentSessionId?.ToString(),
            // Automatic session roles (chunk 2.5): the sticky explicit role, so the Gateway aggregation can
            // apply the explicit-wins precedence. The RESOLVED SessionRole is computed at the aggregation.
            ExplicitRole = s.ExplicitRole,
            // Defect 5: the resolved role the GATEWAY computed from the whole fleet and stamped back down
            // onto this Director (Session.GatewayResolvedRole, written only by the set-resolved-role verb).
            // The Director carries it; it does NOT compute it - "is this session's controller still alive?"
            // needs the whole fleet, and the controller may be on another machine.
            //
            // THIS LINE IS WHY THE DESKTOP AGREES WITH THE PHONE. The rail's fold input comes from this same
            // mapper (SessionViewModel.FoldInput), so before this the field was always null on the desktop
            // and SessionOrdering's red-suppression could never fire: a live Worker read slate "Sub-agent"
            // on the phone and red "Needs you" on the rail at the same instant.
            //
            // Null when no Gateway has ever stamped one - the standalone-desktop floor, where a Worker's red
            // surfaces because nothing authoritative has said otherwise. That is the honest answer; a local
            // guess would be the defect. The value also rides back UP to the Gateway on the next delta,
            // where PushedSessionStore DISCARDS it at ingest so this echo can never be mistaken for an
            // authority. (docs/new_architecture/sessions.html, defect 5.)
            SessionRole = s.GatewayResolvedRole,
            // The other half of the Gateway's answer. Without it the desktop folds every session as
            // unsupervised and every held worker goes red on one screen while the phone shows it held.
            HasLiveSupervisor = s.GatewayResolvedHasLiveSupervisor,
            // The Gateway's folded DISPLAY STATE, stamped back down onto this Director (Session.Gateway*,
            // written only by the set-display-state verb) and echoed here for the loopback reader that
            // cannot ask upstream: the desktop rail. The rail renders these VERBATIM instead of re-folding
            // from local facts it cannot see (dictation, transcription, voice generation, the snooze clock) -
            // which is exactly why a snoozed session read red "Needs you" on the desktop while the phone and
            // the Cockpit read "Snoozed". Null until a Gateway stamps them (the standalone-desktop floor);
            // the rail shows a neutral waiting-for-gateway placeholder rather than guessing. Like SessionRole
            // these ride back UP on the next delta, where the Gateway OVERWRITES them from its own fold, so
            // the echo can never be mistaken for an authority. (docs/new_architecture/sessions.html.)
            EffectiveColor = s.GatewayEffectiveColor,
            StateLabel = s.GatewayStateLabel,
            TriageBucket = s.GatewayTriageBucket,
            NeedsYouSince = s.GatewayNeedsYouSince,
            SnoozeUntil = s.GatewaySnoozeUntil,
            SnoozeExpired = s.GatewaySnoozeExpired,
            // Message Load mission, slice 4: the Gateway's row line, echoed for the rail like the fold above.
            InboxLine = s.GatewayInboxLine,
            // Chunk 3: the auto-vs-explicit name marker (a future auto-rename gates on it).
            IsAutoNamed = s.IsAutoNamed,
            IsBackgroundRunning = s.IsBackgroundRunning,
            // Issue #1177 (Phase 2): the two Director-baked overlays that previously reached the Gateway
            // ONLY via the cooked StatusColor. Now reported as raw facts so the Gateway fold reproduces
            // them (desktop-dictation orange, legacy auto-explain yellow) without reading StatusColor.
            IsTranscribing = s.IsTranscribing,
            IsAutoExplaining = s.IsExplaining,
            // DevThrottle Stats: the per-session input tally, taken at the choke point. Null when empty so a
            // fresh session does not bloat every snapshot; the Gateway aggregates the non-null tallies.
            InputStats = s.InputStats.IsEmpty ? null : s.InputStats.Snapshot(),
            // The model producer (issue #1637): the driver-reported model this agent is currently
            // using, stamped at turn-end by SessionRecordsWatcher. Null until a read succeeds.
            CurrentModel = s.CurrentModel,
            // The token producer (issue #1637): this session's cumulative token spend, stamped at the
            // same turn-end from the same records as CurrentModel. Null until a read succeeds or on a
            // driver without the TokenUsage capability. The lean totals only - the on-demand usage view
            // keeps its own per-turn command.
            TokenTotals = s.TokenTotals,
            RemoteRepo = s.RemoteRepo ?? "",
            RemoteThreadUrl = s.RemoteThreadUrl ?? "",
            RemoteRunUrl = s.RemoteRunUrl ?? "",
            RemoteRunStatus = s.RemoteRunStatus ?? "",
            // DevThrottle Stats: the "owner/repo" repo name of this local checkout (GitHub or Azure DevOps),
            // resolved here on the Director because the git repo lives on this machine. Cached per path so
            // this roster mapper never forks git twice for the same checkout; "" when the checkout is on no
            // host we recognize, in which case the Gateway groups it by its folder name instead.
            RepoName = GitHubUrls.ResolveRepoNameCached(s.RepoPath),
            // How many files are changed in this session's working tree, measured on this machine by
            // SessionGitStatusMonitor - the number behind the desktop rail's amber "N chg" badge. Null
            // until a git probe succeeds, and null is UNKNOWN, never "clean": a failed probe leaves the
            // last known count in place rather than reporting a zero every reader would render as a clean
            // tree (issue 516). Nothing is derived here - the Gateway passes the count through and the
            // clients share one formatter.
            UncommittedCount = s.UncommittedCount,
            // Supervision facts (internal#625 Phase 1): the completed-turn counter and the honest
            // waiting clock, kept by the Session at the activity flip. Raw facts; the Gateway
            // passes them through and the clients share one formatter. Always reported by this
            // Director - only an older build leaves them null on the wire.
            TurnCount = s.TurnCount,
            WaitingSince = s.WaitingSince,
            CumulativeIdleSeconds = s.CumulativeIdleSeconds,
            // The interruption COUNT beside the seconds (devthrottle_internal issue #982): how many
            // times this session started waiting on the user. The pair is what makes the clock
            // readable - an hour of waiting spread over twelve interruptions is a different session
            // from one that waited once.
            WaitingStretchCount = s.WaitingStretchCount,
            // Issue #335: identity fields populated by the Director (not patched by the Gateway).
            MachineName = machineName,
            User = user,
            TailnetEndpoint = tailnetEndpoint,
            ViewUrl = viewUrl,
        };
    }

    /// <summary>
    /// Convert the agent-agnostic conversation history into the legacy turn widget shape consumed by
    /// the Gateway Wingman voice path. Assistant text must stay Kind="Text": that is the contract
    /// WingmanTranslator uses to find the latest reply to summarize and speak.
    /// </summary>
    // Internal (not private) so the extracted SessionReadExecutor.Turns core can call the SAME widget
    // builder the REST route used, keeping the tunnel verb and the route byte-identical (Gateway Cleanup
    // mission, Phase 0).
    internal static List<TurnWidgetDto> BuildTurnWidgetsFromHistory(ConversationHistory history)
    {
        var widgets = new List<TurnWidgetDto>();
        foreach (var message in history.Messages)
        {
            foreach (var part in message.Parts)
            {
                var text = part.Text?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                widgets.Add(part.Kind switch
                {
                    ConversationPartKind.Text when message.Role == ConversationRole.Assistant => new TurnWidgetDto
                    {
                        Kind = "Text",
                        Header = "Agent",
                        Content = text,
                    },
                    ConversationPartKind.Text => new TurnWidgetDto
                    {
                        Kind = "UserMessage",
                        Header = "You",
                        Content = text,
                    },
                    ConversationPartKind.Thinking => new TurnWidgetDto
                    {
                        Kind = "Thinking",
                        Header = "Thinking",
                        Content = text,
                    },
                    ConversationPartKind.ToolUse => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = part.ToolName ?? "Tool",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    ConversationPartKind.ToolResult => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = "Tool result",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    _ => new TurnWidgetDto
                    {
                        Kind = part.Kind.ToString(),
                        Header = part.Kind.ToString(),
                        Content = text,
                    },
                });
            }
        }

        return widgets;
    }

    /// <summary>
    /// Compute the current turn count from the session's linked JSONL file.
    /// Returns 0 if the session isn't linked or the file isn't there yet.
    /// Used by the recap endpoints to compute the IsStale flag.
    /// </summary>
    // Gateway Cleanup Phase 0: internal so the shared SessionReadExecutor.Recap core (which the tunnel
    // recap verb and the re-pointed GET /sessions/{sid}/recap route both call) reads the SAME turn count.
    internal static int ComputeTurnCount(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return 0;
        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return 0;
            var messages = StreamMessageParser.ParseFile(jsonl);
            return WidgetBuilder.BuildFromMessages(messages).Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ComputeTurnCount FAILED: {ex.Message}");
            return 0;
        }
    }

    // ===== Screenshots gallery helpers =====

    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Newest-N cap applied to GET /screenshots when the client sends no ?count. The gallery
    /// only ever shows the newest few screenshots; older files are deliberately never listed.
    /// Internal (not private) so the endpoint tests assert against the same number.
    /// </summary>
    internal const int DefaultScreenshotCount = 100;

    /// <summary>Image files in the screenshots folder, newest first. Empty if the folder is missing.</summary>
    /// <summary>
    /// All image files in the screenshots folder, newest first, in ONE filesystem pass.
    /// DirectoryInfo.EnumerateFiles yields FileInfo objects pre-populated from the directory
    /// enumeration data, so sorting and reading LastWriteTime/Length costs no extra stat
    /// calls. The previous string-path version stat'ed every file twice (sort + FileInfo),
    /// which took ~100ms on a 1400-file folder; this takes ~1-2ms.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshots-list byte verb enumerates the same folder this
    // REST route does - one enumerator, no drift.
    internal static List<FileInfo> ScreenshotFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<FileInfo>();
        return new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => ScreenshotExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Resolve a client-supplied screenshot name to an absolute path INSIDE the screenshots
    /// folder, or null if it escapes the folder, is not a bare file name, has a non-image
    /// extension, or does not exist. Defends GET/DELETE /screenshots/file against traversal.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshot-file stream verb resolves the same traversal-safe
    // path this REST route does - one resolver, no drift.
    internal static string? ResolveScreenshot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // Reject anything that isn't a bare file name (no separators, no "..").
        if (Path.GetFileName(name) != name)
            return null;
        if (!ScreenshotExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return null;

        var dir = CcStorage.Screenshots();
        var full = Path.GetFullPath(Path.Combine(dir, name));
        // Confirm the resolved path is still under the screenshots folder.
        var dirFull = NormalizeDirectoryPath(Path.GetFullPath(dir));
        if (!full.StartsWith(ContainmentPrefix(dirFull), PathContainmentComparison))
            return null;
        if (!File.Exists(full))
            return null;

        // The bare-name gate above stops a traversal SPELLING, but it cannot stop a symbolic link
        // PLANTED inside the screenshots folder under an innocent image name - the name is bare, the
        // extension is allowed, and the lexical prefix is legal, while the file itself is somewhere
        // else entirely. Decide on the real identity, and refuse when it cannot be established.
        var realFull = ResolveRealPath(full);
        var realDir = ResolveRealPath(dirFull);
        if (realFull is null || realDir is null)
            return null;
        var realDirTrimmed = NormalizeDirectoryPath(realDir);
        if (!realFull.StartsWith(ContainmentPrefix(realDirTrimmed), PathIdentityComparison))
            return null;

        // And the same last gate as the session-file read: an in-folder HARD LINK named
        // "innocent.png" passes the bare-name test, the extension test and both prefix tests while
        // naming a file that also lives outside the screenshots folder (M03-I2-01).
        return RefuseUnlessSingleName(full);
    }

    /// <summary>
    /// Resolve a client-supplied path for the session file read (the Local Files viewer) to an absolute
    /// path INSIDE the session's own working directory, or null when it escapes it - an absolute path
    /// elsewhere on the machine, a traversal that resolves outside, or an invalid path. This is
    /// <see cref="ResolveScreenshot"/>'s twin for the read-file stream verb: resolve fully first, then
    /// refuse anything outside the allowed root. The allowed root is the session's working directory -
    /// the one directory the session is about - so a session-scoped file read can never reach
    /// credentials, tokens, or any other file elsewhere on the machine.
    /// </summary>
    internal static string? ResolveSessionFile(string? workingDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(path))
            return null;

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(workingDirectory);
            // A relative path resolves against the session's working directory; an absolute path stays
            // itself. Either way the containment check below runs on the fully resolved result.
            full = Path.GetFullPath(path, root);
        }
        catch (ArgumentException)
        {
            // Invalid characters, an embedded NUL, or a relative working directory (a session with no
            // real local checkout, e.g. a remote-thread session) - nothing here is safe to serve.
            return null;
        }

        var rootTrimmed = NormalizeDirectoryPath(root);
        if (!full.StartsWith(ContainmentPrefix(rootTrimmed), PathContainmentComparison))
            return null;

        // The prefix test above is necessary but NOT sufficient. Path.GetFullPath is pure string
        // normalization - it never touches the filesystem - so a symbolic link or directory junction
        // planted UNDER the working directory keeps a perfectly legal lexical prefix while the read
        // that follows walks out to anywhere on disk. Decide again on the REAL filesystem identity.
        // BOTH sides are resolved: the working directory may itself be reached through a link, and
        // resolving only the candidate would then make every legal file look out-of-root.
        var realRoot = ResolveRealPath(rootTrimmed);
        var realFull = ResolveRealPath(full);
        if (realRoot is null || realFull is null)
            return null; // identity not establishable (an unresolvable reparse point, a cycle) - refuse
        var realRootTrimmed = NormalizeDirectoryPath(realRoot);
        if (!realFull.StartsWith(ContainmentPrefix(realRootTrimmed), PathIdentityComparison))
            return null;

        // Everything above decides where the NAME is. This decides whether the name is the only one
        // (M03-I2-01). A hard link is a second directory entry for the same file object and carries
        // no reparse-point attribute, so nothing above can see it: the in-root alias resolves to
        // itself, passes containment, and the read serves a file that also lives outside the root.
        // A file with more than one name cannot be proven to be inside anything, so it is refused,
        // and so is a file whose name count cannot be established.
        return RefuseUnlessSingleName(full);
    }

    /// <summary>
    /// The last gate of the file-containment decision: return <paramref name="full"/> only when the
    /// filesystem says this file has exactly ONE name. See <see cref="FilesystemIdentity"/> for why a
    /// second name defeats every path-based containment test ever written.
    /// </summary>
    private static string? RefuseUnlessSingleName(string full)
    {
        // A path with no file behind it has no identity to alias and serves nothing; the caller's own
        // existence check answers it as an ordinary not-found. Asking the filesystem how many names a
        // missing file has would turn every not-found into a containment refusal, which is a worse
        // answer to debug and would deny a session file the moment it is deleted.
        if (!File.Exists(full))
            return full;

        var singleName = FilesystemIdentity.HasExactlyOneName(full);
        if (singleName is null)
        {
            FileLog.Write($"[ControlEndpoints] REFUSED {full}: the number of names this file has could not be established");
            return null;
        }
        if (singleName == false)
        {
            FileLog.Write($"[ControlEndpoints] REFUSED {full}: the file has more than one name, so it cannot be shown to live inside the allowed root");
            return null;
        }
        return full;
    }

    /// <summary>
    /// A directory path with its trailing separators removed - EXCEPT when the path IS a filesystem
    /// root, where the separator is part of the name and removing it changes the meaning completely.
    ///
    /// This is not a tidiness helper, it is a correctness one. On Windows <c>D:\</c> trimmed becomes
    /// <c>D:</c>, which is DRIVE-RELATIVE: it resolves to that drive's current directory, not to the
    /// drive's root. On Unix <c>/</c> trimmed becomes the empty string, which resolves to the process
    /// current directory. Either way a session whose working directory is a filesystem root would have
    /// its containment decided against a completely different directory, and every file under it
    /// refused - which is exactly the regression the second inspection found (M03-I2B-04).
    /// </summary>
    internal static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        var pathRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(pathRoot) && string.Equals(path, pathRoot, StringComparison.Ordinal))
            return path;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    /// <summary>
    /// The prefix every path INSIDE <paramref name="directory"/> begins with: the directory followed
    /// by exactly one separator. A filesystem root already ends in its own separator, so appending a
    /// second one would build a prefix that no real path can ever match.
    /// </summary>
    internal static string ContainmentPrefix(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    /// <summary>
    /// Comparison for path-containment decisions. Case-insensitive ONLY on Windows, where the
    /// filesystem itself compares names case-insensitively; ordinal (case-sensitive) everywhere
    /// else. On Linux paths are case-sensitive, so an ignore-case prefix test would accept
    /// "/ROOT/x" as inside "/root" - two genuinely different directories. macOS is deliberately
    /// treated as case-sensitive as well: its default volumes compare case-insensitively, but
    /// case-sensitive APFS volumes are common on developer machines, and mis-classifying one
    /// OPENS the boundary while the reverse only refuses a case-variant spelling of a legal
    /// path - fail closed. Exposed through <see cref="PathContainmentComparisonFor"/> so both
    /// branches of the decision are testable on any one build machine.
    /// </summary>
    internal static StringComparison PathContainmentComparison { get; } =
        PathContainmentComparisonFor(OperatingSystem.IsWindows());

    /// <summary>The pure decision behind <see cref="PathContainmentComparison"/>; see there.</summary>
    internal static StringComparison PathContainmentComparisonFor(bool windowsFileSystem) =>
        windowsFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Comparison for the containment decision made on RESOLVED paths - always ordinal, on every
    /// platform, because both sides have already been folded to the spelling the filesystem itself
    /// uses (<see cref="FilesystemIdentity.CanonicalPath"/>).
    ///
    /// This is the second half of inspection finding M03-I2-01. The platform-wide ignore-case rule
    /// above is right about the DEFAULT Windows filesystem and wrong about a directory carrying the
    /// NTFS per-directory case-sensitive flag, where "repo" and "REPO" are two different directories
    /// and an ignore-case prefix test accepts the wrong one as inside. Comparing canonical spellings
    /// ordinally decides correctly under either rule, and needs no guess about which rule applies:
    /// on a case-insensitive parent every spelling folds onto the one real name, and on a
    /// case-sensitive parent the two names fold to themselves and stay distinct.
    ///
    /// <see cref="PathContainmentComparison"/> is kept for the cheap LEXICAL pre-filter that runs
    /// before anything touches the filesystem. That pre-filter is deliberately the more permissive
    /// of the two on Windows: it only has to avoid resolving obvious nonsense, and the resolved
    /// comparison below is what actually decides.
    /// </summary>
    internal static StringComparison PathIdentityComparison => StringComparison.Ordinal;

    /// <summary>
    /// Resolve <paramref name="path"/> to its REAL filesystem identity: every symbolic link,
    /// junction, or other resolvable reparse point in every EXISTING component - intermediate
    /// directories included, which a single <see cref="File.ResolveLinkTarget(string, bool)"/>
    /// call does not cover - is followed to its final target, and a resolved target is walked
    /// again in full so a target that itself sits behind links is also resolved. Returns null
    /// when the identity cannot be established: a reparse point .NET cannot interpret, a link
    /// cycle, or a component whose attributes cannot be read. Callers must REFUSE on null -
    /// never fall back to the lexical answer. Components that do not exist cannot hide a link,
    /// so a non-existent suffix is kept lexically (the caller's own existence checks handle it).
    /// </summary>
    internal static string? ResolveRealPath(string path)
    {
        var resolved = ResolveLinkChain(path);
        if (resolved is null)
            return null;

        // Fold the result to the spelling the filesystem itself uses. On Windows that is what makes
        // the containment comparison ORDINAL and therefore correct on a per-directory case-sensitive
        // parent, which the platform supports and the old ignore-case rule got wrong (M03-I2-01).
        // Everywhere else this returns the path unchanged.
        return FilesystemIdentity.CanonicalPath(resolved);
    }

    /// <summary>
    /// The link-following half of <see cref="ResolveRealPath"/>: every existing component walked,
    /// every resolvable reparse point followed. See there for the contract.
    /// </summary>
    private static string? ResolveLinkChain(string path)
    {
        // More link hops than this is a cycle (a link pointing back at its own ancestor) or an
        // absurd chain; either way the identity is not establishable - refuse.
        const int maxLinkResolutions = 40;
        var resolutions = 0;

        string current;
        try { current = Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }

        var followedLink = true;
        while (followedLink)
        {
            followedLink = false;
            var pathRoot = Path.GetPathRoot(current);
            if (string.IsNullOrEmpty(pathRoot))
                return null; // not an absolute path - no real identity to establish
            var components = current[pathRoot.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var prefix = pathRoot;
            for (var i = 0; i < components.Length; i++)
            {
                var candidate = Path.Combine(prefix, components[i]);

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(candidate);
                }
                catch (FileNotFoundException)
                {
                    // Nothing exists from this component down, so nothing below can be a link;
                    // keep the remainder lexically.
                    return Path.Combine(prefix, string.Join(Path.DirectorySeparatorChar, components[i..]));
                }
                catch (DirectoryNotFoundException)
                {
                    return Path.Combine(prefix, string.Join(Path.DirectorySeparatorChar, components[i..]));
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (++resolutions > maxLinkResolutions)
                        return null;

                    FileSystemInfo linkInfo = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(candidate)
                        : new FileInfo(candidate);
                    FileSystemInfo? target;
                    try { target = linkInfo.ResolveLinkTarget(returnFinalTarget: true); }
                    catch (IOException) { return null; }
                    catch (UnauthorizedAccessException) { return null; }
                    if (target is null)
                        return null; // a reparse point whose target cannot be read - refuse, never follow

                    var remainder = components[(i + 1)..];
                    current = remainder.Length == 0
                        ? target.FullName
                        : Path.Combine(target.FullName, Path.Combine(remainder));
                    try { current = Path.GetFullPath(current); }
                    catch (ArgumentException) { return null; }

                    // The target's own path may run through further links; walk it again in full.
                    followedLink = true;
                    break;
                }

                prefix = candidate;
            }

            if (!followedLink)
                current = prefix;
        }

        return current;
    }

    internal static string ScreenshotContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The single extension -> HTTP content-type map shared by the top-level <c>GET /file</c> and the
    /// session-scoped <c>GET /sessions/{sid}/file</c> (Local Files mission). One map, two routes, so a
    /// new type is added in exactly one place. The octet-stream fallback means an unknown extension is
    /// served as an opaque download, never guessed as text or an image.
    /// </summary>
    internal static string FileContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".pdf"            => "application/pdf",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".svg"            => "image/svg+xml",
        ".css"            => "text/css; charset=utf-8",
        ".js"             => "text/javascript; charset=utf-8",
        ".json"           => "application/json; charset=utf-8",
        ".csv"            => "text/csv; charset=utf-8",
        ".md" or ".txt" or ".log" => "text/plain; charset=utf-8",
        _                 => "application/octet-stream",
    };
}

/// <summary>
/// The body of POST /fleet/machines/{machine}/launch. Either <see cref="Path"/> or <see cref="App"/> says
/// what to start - a path directly, or a name resolved against that machine's own catalogue.
/// </summary>
internal sealed class MachineLaunchRequest
{
    public string? Path { get; init; }
    public string? App { get; init; }
    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }

    /// <summary>
    /// Tenant-boundary hardening (CR-5): the explicit confirmation the Gateway's launch relay now requires
    /// on every launch (the restart/stop slot-guard flag, applied to program starts). Forwarded through this
    /// Director hop verbatim, so a caller that wants a program started must say so explicitly end to end.
    /// </summary>
    public bool ConfirmProtected { get; init; }
}
