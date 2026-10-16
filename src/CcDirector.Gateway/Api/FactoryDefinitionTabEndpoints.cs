using System.IO.Compression;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE DEFINITION TAB OF A FACTORY'S PAGE (Factory Definition mission, phase 6, section 5.7): what the owner's screen
/// reads, each view folded once by <see cref="FactoryDefinitionTabFold"/> (critical rule 7), and the zip the tab's
/// Export downloads and its Import uploads.
///
///   GET  /gateway/factories/{factory}/definition                     -> FactoryDefinitionTabViewDto
///   GET  /gateway/factories/{factory}/definition/history             -> FactoryDefinitionHistoryViewDto (?before=N)
///   GET  /gateway/factories/{factory}/definition/versions/{version}  -> FactoryDefinitionVersionViewDto
///   GET  /gateway/factories/{factory}/definition/export              -> a zip of the folder (?version=N)
///   POST /gateway/factories/{factory}/definition/import              <- a zip of a folder; stored as a DRAFT, never published
///
/// The owner's, like the Seats tab: a session key is refused here and by SessionKeyGuard. A session reads and writes the
/// definition through <c>/factory-definitions</c> with the command line. Publish and restore are NOT here: the tab sends
/// them to <see cref="FactoryDefinitionEndpoints"/>, where a factory's own session may do neither, and every refusal is
/// worded.
///
/// THE ZIP IS THE FOLDER. Export writes exactly the files <c>GET /factory-definitions/{factory}/export</c> answers, in
/// path order with one fixed time on every entry, so two exports of one version are the same bytes. Import reads every
/// file in the zip as UTF-8 text - nothing is skipped; a file the definition does not take is refused by the check, by
/// name - and sends it through the SAME checked import as the command line.
/// </summary>
internal static class FactoryDefinitionTabEndpoints
{
    /// <summary>The largest zip an import reads. A definition is at most <see cref="DefinitionPaths.MaxVersionBytes"/>
    /// of text, and a zip of it is smaller; the room above that is for a zip that stores its files uncompressed.</summary>
    internal const int MaxZipBytes = DefinitionPaths.MaxVersionBytes + 1024 * 1024;

    /// <summary>The one time every entry of an exported zip carries, so the bytes depend only on the files.</summary>
    internal static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources,
        FactoryDefinitionStore store, FactoryPublishApply apply, Func<TenantId, string, FactoryLiveParts?> liveOf,
        Func<string, SessionFactoryLookup> sessionFactoryOf, FactoryDefinitionEndpoints.Account account,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(liveOf);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(nowUtc);

        const string route = FactoriesScreenEndpoints.Prefix + "/{factory}/definition";

        app.MapGet(route, (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory definition tab {factory}", tenant =>
            {
                if (SubjectOf(sources, tenant, factory) is not { } registered) return NotRegistered(factory);
                var dto = Tab(tenant, registered, store, apply, liveOf, sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryDefinitionTabEndpoints] GET tab: {registered.Factory} published={dto.Published?.Version.ToString() ?? "none"}, draft={dto.Draft?.Version.ToString() ?? "none"}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/history", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory definition history {factory}", tenant =>
            {
                if (SubjectOf(sources, tenant, factory) is not { } registered) return NotRegistered(factory);
                int? before = null;
                if (ctx.Request.Query.TryGetValue("before", out var beforeSent))
                {
                    // Refused in words, never quietly replaced: a page that started somewhere else than asked would read
                    // as the page that was asked for.
                    if (!int.TryParse(beforeSent.ToString(), out var b) || b < 1)
                        return Results.BadRequest(new
                        {
                            error = $"before is the version a page of the history starts below: a whole number from 1, not '{beforeSent}'",
                        });
                    before = b;
                }
                var standing = store.Standing(tenant, registered.Factory);
                var page = store.History(tenant, registered.Factory, before, FactoryDefinitionTabFold.HistoryPage);
                // Each Restore confirm names what the restore changes in the published version, which is read and
                // checked once for the whole page.
                Func<int, string>? restoreMakes = null;
                if (standing.Published is { } live)
                {
                    var liveFiles = (store.Find(tenant, registered.Factory, live.Version)
                                     ?? throw new InvalidOperationException($"{registered.Factory} names definition version {live.Version} as published, and the store has no such version")).Files;
                    var liveReadable = FactoryPublishApply.FirstFormatError(liveFiles) is null;
                    restoreMakes = version => FactoryDefinitionTabFold.RestoreMakes(version, live.Version, liveFiles, liveReadable,
                        (store.Find(tenant, registered.Factory, version)
                         ?? throw new InvalidOperationException($"{registered.Factory} has no definition version {version}, which its history listed")).Files);
                }
                var dto = FactoryDefinitionTabFold.History(registered.Factory, registered.Title, standing, page,
                    firstPage: before is null, sources.Activity.TimeZone(tenant), nowUtc(), restoreMakes);
                FileLog.Write($"[FactoryDefinitionTabEndpoints] GET history: {registered.Factory} before={before?.ToString() ?? "none"}, rows={dto.Rows.Count} of {page.Total}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/versions/{version}", (HttpContext ctx, string factory, string version) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory definition version {factory}/{version}", tenant =>
            {
                if (SubjectOf(sources, tenant, factory) is not { } registered) return NotRegistered(factory);
                if (!int.TryParse(version, out var wanted) || wanted < 1)
                    return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{version}'" });
                var found = store.Find(tenant, registered.Factory, wanted);
                if (found is null)
                    return Results.Json(new { error = $"version {wanted} of the definition of {registered.Factory} does not exist" },
                        statusCode: StatusCodes.Status404NotFound);
                var dto = FactoryDefinitionTabFold.Version(registered.Factory, found, store.Standing(tenant, registered.Factory),
                    sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryDefinitionTabEndpoints] GET version: {registered.Factory} v{wanted} files={dto.Files.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/export", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory definition export {factory}", tenant =>
            {
                if (SubjectOf(sources, tenant, factory) is not { } registered) return NotRegistered(factory);
                int? asked = null;
                if (ctx.Request.Query.TryGetValue("version", out var versionSent))
                {
                    if (!int.TryParse(versionSent.ToString(), out var v) || v < 1)
                        return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{versionSent}'" });
                    asked = v;
                }
                var folded = registered.Factory;
                var standing = store.Standing(tenant, folded);
                IReadOnlyDictionary<string, string> files;
                string name;
                if (asked is not null || standing.Newest is not null)
                {
                    var chosen = asked ?? standing.Published?.Version ?? standing.Newest!.Version;
                    var found = store.Find(tenant, folded, chosen);
                    if (found is null)
                        return Results.Json(new { error = $"version {chosen} of the definition of {folded} does not exist" },
                            statusCode: StatusCodes.Status404NotFound);
                    files = found.Files;
                    name = $"{folded}-definition-v{chosen}.zip";
                }
                else
                {
                    if (liveOf(tenant, folded) is not { } live)
                        return NotRegistered(factory);
                    files = FactoryDefinitionExport.FromLive(live).Files;
                    name = $"{folded}-definition-from-today.zip";
                }
                var zip = Zip(files);
                FileLog.Write($"[FactoryDefinitionTabEndpoints] GET export: {folded} {name}, files={files.Count}, bytes={zip.Length}");
                return Results.File(zip, "application/zip", name);
            }));

        app.MapPost(route + "/import", async (HttpContext ctx, string factory) =>
        {
            // The zip is read before the gate below so the gate's handler stays synchronous; a session key never gets
            // here (SessionKeyGuard refuses it first), and the read stops at the cap.
            var (zip, tooLarge) = await ReadBody(ctx);
            return FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"POST factory definition import {factory}", tenant =>
            {
                if (SubjectOf(sources, tenant, factory) is not { } registered) return NotRegistered(factory);
                if (tooLarge)
                    return Refused($"The file is larger than {FactoryBriefPanelSize(MaxZipBytes)}, more than a definition can hold. Nothing was imported.");
                if (!TryUnzip(zip, out var content, out var refusal)) return Refused(refusal!);

                // The tab is one factory's: a folder for another factory is imported on that factory's page, or with the
                // command line when it has none yet.
                if (FactoryNamedIn(content) is { } named && FactoryNames.TryFactory(named, out var namedFolded, out _)
                    && !string.Equals(namedFolded, registered.Factory, StringComparison.Ordinal))
                    return Refused($"This folder's factory.yaml names the factory '{namedFolded}', not '{registered.Factory}'. " +
                                   $"Import it on the page of '{namedFolded}', or with {FactoryNewFold.NewFactoryLabel} on the Factories screen " +
                                   "when that factory has no page yet. Nothing was imported.");

                var note = ctx.Request.Query.TryGetValue("note", out var noteSent) && !string.IsNullOrWhiteSpace(noteSent.ToString())
                    ? noteSent.ToString()
                    : "imported from a zip on the factory page";
                FileLog.Write($"[FactoryDefinitionTabEndpoints] POST import: {registered.Factory} files={content.Count}, bytes={zip.Length}");
                return FactoryDefinitionEndpoints.Import(ctx, new ImportFactoryDefinitionRequest { Content = content, Note = note },
                    store, resolveTenant, sessionFactoryOf, account, nowUtc,
                    saved: dto => Results.Json(new FactoryDefinitionImportedDto
                    {
                        Factory = dto.Factory,
                        Version = dto.Version,
                        DoneText = FactoryDefinitionTabFold.ImportedText(dto.Factory, dto.Version, dto.FileCount, dto.Created == true),
                        Findings = dto.Findings,
                    }));
            });
        });

        // NEW FACTORY on the Factories screen (phase 6b): the first draft of a factory that has no page yet, through the
        // SAME unzip and the SAME checked import as the tab's Import. The owner's, like the tab: a session key is refused
        // here and by SessionKeyGuard, which lets no session post to the Factories screen.
        app.MapPost(FactoryNewFold.NewFactoryHref, async (HttpContext ctx) =>
        {
            var (zip, tooLarge) = await ReadBody(ctx);
            return FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, "POST new factory", tenant =>
            {
                if (tooLarge)
                    return Refused($"The file is larger than {FactoryBriefPanelSize(MaxZipBytes)}, more than a definition can hold. Nothing was imported.");
                if (!TryUnzip(zip, out var content, out var refusal)) return Refused(refusal!);
                if (NewFactoryRefusal(sources, tenant, content) is { } taken)
                {
                    FileLog.Write($"[FactoryDefinitionTabEndpoints] POST new factory: the id is taken");
                    return Refused(taken, StatusCodes.Status409Conflict);
                }

                FileLog.Write($"[FactoryDefinitionTabEndpoints] POST new factory: files={content.Count}, bytes={zip.Length}");
                // A folder that names no factory, or a spelling that cannot be one, is refused by the import itself,
                // with the check's findings - the same answer the tab and the command line give.
                return FactoryDefinitionEndpoints.Import(ctx, new ImportFactoryDefinitionRequest { Content = content, Note = "imported with New factory" },
                    store, resolveTenant, sessionFactoryOf, account, nowUtc,
                    saved: dto => Results.Json(new FactoryDefinitionImportedDto
                    {
                        Factory = dto.Factory,
                        Version = dto.Version,
                        DoneText = FactoryDefinitionTabFold.ImportedText(dto.Factory, dto.Version, dto.FileCount, dto.Created == true),
                        Findings = dto.Findings,
                        OpenHref = FactoryDefinitionTabFold.TabHref(dto.Factory),
                    }));
            });
        });

        FileLog.Write($"[FactoryDefinitionTabEndpoints] mapped {route} and its history, versions, export and import, and {FactoryNewFold.NewFactoryHref}");
    }

    /// <summary>
    /// WHOSE DEFINITION THE TAB SHOWS: a registered factory, or one New factory made that is not published yet (phase
    /// 6b), whose title is its newest draft's. Null when the account has neither under that id.
    /// </summary>
    internal sealed record Subject(string Factory, string Title);

    internal static Subject? SubjectOf(FactoriesScreenSources sources, TenantId tenant, string factory)
    {
        if (sources.Registry.Find(tenant, factory) is { } registered) return new Subject(registered.Factory, registered.Title);
        return FactoriesScreenEndpoints.UnpublishedOne(sources, tenant, factory) is { } draft ? new Subject(draft.Factory, draft.Title) : null;
    }

    /// <summary>The tab, read from the store and folded.</summary>
    internal static FactoryDefinitionTabViewDto Tab(TenantId tenant, Subject registered, FactoryDefinitionStore store,
        FactoryPublishApply apply, Func<TenantId, string, FactoryLiveParts?> liveOf, TimeZoneInfo zone, DateTime nowUtc)
    {
        var folded = registered.Factory;
        var standing = store.Standing(tenant, folded);
        var published = standing.Published is { } p ? store.Find(tenant, folded, p.Version) : null;
        var newest = standing.Newest is { } n ? store.Find(tenant, folded, n.Version) : null;
        IReadOnlyList<(string, string)> leavesOut = Array.Empty<(string, string)>();
        if (newest is null && liveOf(tenant, folded) is { } live)
            leavesOut = FactoryDefinitionExport.FromLive(live).Reported.Select(r => (r.File, r.Message)).ToList();
        return FactoryDefinitionTabFold.Tab(folded, registered.Title, standing, published, newest,
            version => FactoryDefinitionEndpoints.PlanOf(apply, tenant, version, standing.Published?.Version),
            leavesOut, zone, nowUtc);
    }

    // ---------- the zip ----------

    /// <summary>The folder as a zip: every file in ordinal path order, as UTF-8 with no byte-order mark, one fixed time on
    /// every entry. The same files always make the same bytes.</summary>
    internal static byte[] Zip(IReadOnlyDictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in files.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = EntryTime;
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(files[path]);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Every file in a zip, by its path with forward slashes, as UTF-8 text - nothing skipped but the folder entries.
    /// A zip of a folder made by the operating system holds the folder itself; when every file sits under one folder and
    /// none is at the top, that one folder is the definition's folder and its name is taken off every path.
    /// </summary>
    internal static bool TryUnzip(byte[] zip, out Dictionary<string, string> content, out string? refusal)
    {
        content = new Dictionary<string, string>(StringComparer.Ordinal);
        refusal = null;
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var read = new List<(string Path, string Text)>();
        long total = 0;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.EndsWith('/')) continue; // a folder, not a file
                total += entry.Length;
                if (total > DefinitionPaths.MaxVersionBytes)
                {
                    refusal = $"The files in the zip come to more than {FactoryBriefPanelSize(DefinitionPaths.MaxVersionBytes)}, " +
                              "the most a definition holds. Nothing was imported.";
                    return false;
                }
                using var stream = entry.Open();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                try
                {
                    read.Add((path, strict.GetString(bytes.ToArray())));
                }
                catch (DecoderFallbackException)
                {
                    refusal = $"The file {path} in the zip is not UTF-8 text. Every file in a definition is text. Nothing was imported.";
                    return false;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            refusal = $"The file is not a zip that can be read ({ex.Message}). Export makes one, and so does compressing a folder. Nothing was imported.";
            return false;
        }

        if (read.Count == 0)
        {
            refusal = "The zip holds no files, so there is no definition to import. Nothing was imported.";
            return false;
        }

        var tops = read.Select(r => r.Path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        var oneFolder = tops.Count == 1 && read.All(r => r.Path.Contains('/'));
        foreach (var (path, text) in read)
            content[oneFolder ? path[(tops[0].Length + 1)..] : path] = text;
        return true;
    }

    private static async Task<(byte[] Bytes, bool TooLarge)> ReadBody(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int got;
        while ((got = await ctx.Request.Body.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, got);
            if (buffer.Length > MaxZipBytes) return (Array.Empty<byte>(), true);
        }
        return (buffer.ToArray(), false);
    }

    /// <summary>The factory id factory.yaml names, or null when it names none (the check then says why).</summary>
    private static string? FactoryNamedIn(IReadOnlyDictionary<string, string> content)
    {
        if (!content.TryGetValue(FactoryDefinitionCheck.FactoryFile, out var text)) return null;
        var (root, _) = DefinitionDocument.ParseYaml(FactoryDefinitionCheck.FactoryFile, text);
        return root is DefinitionMap map && map.Find("factory")?.Value is DefinitionScalar { Kind: DefinitionScalarKind.Text } id
               && !string.IsNullOrWhiteSpace(id.Value)
            ? id.Value
            : null;
    }

    /// <summary>
    /// WHY NEW FACTORY CANNOT TAKE THIS FOLDER, or null when it can: the id its factory.yaml names is a factory already -
    /// registered and live, archived, or made by New factory and not published yet. Each says where the folder can go
    /// instead. A folder that names no factory is the import's to refuse.
    /// </summary>
    internal static string? NewFactoryRefusal(FactoriesScreenSources sources, TenantId tenant, IReadOnlyDictionary<string, string> content)
    {
        if (FactoryNamedIn(content) is not { } named || !FactoryNames.TryFactory(named, out var folded, out _)) return null;
        if (sources.Registry.Find(tenant, folded) is { } registered)
            return registered.ArchivedAtUtc is null ? FactoryNewFold.AlreadyRegistered(registered) : FactoryNewFold.Archived(registered);
        return FactoriesScreenEndpoints.UnpublishedOne(sources, tenant, folded) is { } draft ? FactoryNewFold.AlreadyDrafted(draft) : null;
    }

    /// <summary>
    /// Why a folder cannot be a NEW factory created from the command line (phase 6c,
    /// <see cref="FactoryDefinitionEndpoints.MapCreate"/>): the same three cases as <see cref="NewFactoryRefusal"/>, read
    /// the same way, worded for a session that runs commands. Null when the id is free.
    /// </summary>
    internal static string? CreateRefusal(FactoriesScreenSources sources, TenantId tenant, IReadOnlyDictionary<string, string> content)
    {
        if (FactoryNamedIn(content) is not { } named || !FactoryNames.TryFactory(named, out var folded, out _)) return null;
        if (sources.Registry.Find(tenant, folded) is { } registered)
            return registered.ArchivedAtUtc is null ? FactoryNewFold.CreateAlreadyRegistered(registered) : FactoryNewFold.CreateArchived(registered);
        return FactoriesScreenEndpoints.UnpublishedOne(sources, tenant, folded) is { } draft ? FactoryNewFold.CreateAlreadyDrafted(draft) : null;
    }

    private static string FactoryBriefPanelSize(int bytes) => $"{bytes / (1024 * 1024)} MB";

    private static IResult Refused(string sentence, int status = StatusCodes.Status400BadRequest)
    {
        FileLog.Write($"[FactoryDefinitionTabEndpoints] import REFUSED ({status}) - {sentence}");
        return Results.Json(new { error = sentence, outcome = "NotImported" }, statusCode: status);
    }

    private static IResult NotRegistered(string factory) =>
        Results.Json(new { error = $"There is no factory '{factory}' in this account." }, statusCode: StatusCodes.Status404NotFound);
}
