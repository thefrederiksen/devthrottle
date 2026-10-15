using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Records;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>One journal entry of the seat, with its text, as the kit lists it.</summary>
public sealed record FactoryKitJournalEntry(Guid Id, string EntryDate, string Text, string AuthorKind, string? AuthorId,
    DateTime WrittenAtUtc);

/// <summary>One attachment of the factory, as the kit lists it. Its bytes are never in the kit (A5).</summary>
public sealed record FactoryKitAttachment(Guid Id, string Name, string? Seat, long Bytes, DateTime WrittenAtUtc);

/// <summary>
/// Everything a kit is built from: one published version's content and its parsed definition, the seat, the kind, the
/// moment, and - for a seat kit - the seat's own journal entries (newest first), the factory's working documents and
/// its attachments. A rules-only kit reads no records; the route passes empty lists for it.
/// </summary>
public sealed record FactoryKitSource(
    string Factory,
    int Version,
    IReadOnlyDictionary<string, string> Content,
    FactoryDefinition Definition,
    string Seat,
    string Kind,
    DateTime BuiltAtUtc,
    IReadOnlyList<FactoryKitJournalEntry> Journal,
    int JournalTotal,
    IReadOnlyList<FactoryDocumentSummary> Documents,
    IReadOnlyList<FactoryKitAttachment> Attachments,
    int AttachmentsTotal);

/// <summary>
/// THE KIT A SEAT STARTS WITH, COMPOSED ON THE GATEWAY (Factory Definition, phase 5, design section 4). One pure
/// function: a version's content, its parsed definition and the seat's records in, the list of files out. It reads no
/// database, no clock and no disk, so every row of the design's table is pinned by a test of this function alone.
///
/// The Gateway composes and the Director writes (rule 7): the Director never decides what a kit holds, so two
/// Directors of different ages write the same kit for the same version.
///
///   kit.json                  factory, seat, version, kind, built at, the seat's secret names (seat kit only), and for
///                             each file where it came from
///   rules.json                the seat's rule sets merged: deny and ask unioned in rule-set order, duplicates removed,
///                             allow NEVER (a definition only adds limits, R13)
///   brief.md                  seat kit only
///   grants.md                 seat kit only: each grant the seat uses, what it allows, its limits
///   files/...                 seat kit only: the definition files the seat names, as stored
///   records/journal.md        seat kit only: the seat's own journal, newest first, the last 30 or 256 KB, whichever is less
///   records/documents.md      seat kit only: the working documents and how to read each; the attachments, listed
///   claude-plugin/...         seat kit only, Claude Code only, only when the seat names a factory skill
/// </summary>
public static class FactoryKitComposer
{
    public const string KitFile = FactoryRunKit.KitFileName;
    public const string RulesFile = FactoryRunKit.RulesFileName;
    public const string BriefFile = "brief.md";
    public const string GrantsFile = "grants.md";
    public const string JournalFile = "records/journal.md";
    public const string DocumentsFile = "records/documents.md";

    /// <summary>Where the seat's factory skills go: a Claude Code plugin folder, passed with <c>--plugin-dir</c> (phase 5 P4).</summary>
    public const string PluginFolder = FactoryRunKit.PluginFolderName;

    /// <summary>The plugin's name. A skill reaches the session as <c>factory-kit:&lt;skill&gt;</c>.</summary>
    public const string PluginName = "factory-kit";

    /// <summary>The most journal entries a kit carries.</summary>
    public const int MaxJournalEntries = 30;

    /// <summary>The most journal text, in UTF-8 bytes, a kit carries.</summary>
    public const int MaxJournalBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// The kit's files for <paramref name="source"/>, in ordinal order of their paths. Throws when the seat is not in
    /// the definition or the kind is not one of the two: the route refuses both in words before it gets here.
    /// </summary>
    public static IReadOnlyList<FactoryKitFileDto> Compose(FactoryKitSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!FactoryKits.IsKnown(source.Kind))
            throw new ArgumentException($"A kit is '{FactoryKits.Seat}' or '{FactoryKits.RulesOnly}', not '{source.Kind}'.", nameof(source));
        var seat = source.Definition.Seats.FirstOrDefault(s => string.Equals(s.Seat, source.Seat, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The definition has no seat '{source.Seat}'.", nameof(source));
        var seatKit = source.Kind == FactoryKits.Seat;
        FileLog.Write($"[FactoryKitComposer] Compose: factory={source.Factory}, version={source.Version}, seat={seat.Seat}, kind={source.Kind}");

        // (kit path, text, where it came from). kit.json is added last, because it lists the others.
        var files = new List<(string Path, string Text, string Source, IReadOnlyList<string> From)>();
        var seatFile = $"seats/{seat.Seat}.yaml";

        var ruleFiles = seat.Rules.Select(r => $"rules/{r}.json").ToList();
        files.Add((RulesFile, Rules(source.Definition, seat), "definition", ruleFiles));

        if (seatKit)
        {
            files.Add((BriefFile, Brief(seat), "definition", new[] { seatFile }));
            files.Add((GrantsFile, Grants(source.Definition, seat), "definition",
                seat.Grants.Count == 0 ? Array.Empty<string>() : new[] { FactoryDefinitionCheck.GrantsFile }));

            foreach (var path in seat.Files.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!source.Content.TryGetValue(path, out var text))
                    throw new ArgumentException($"The seat names '{path}', which this version does not hold.", nameof(source));
                files.Add((path, text, "definition", new[] { path }));
            }

            files.Add((JournalFile, Journal(source.Factory, seat.Seat, source.Journal, source.JournalTotal), "records", Array.Empty<string>()));
            files.Add((DocumentsFile, Documents(source.Factory, source.Documents, source.Attachments, source.AttachmentsTotal),
                "records", Array.Empty<string>()));

            var agent = Enum.TryParse<AgentKind>(seat.AgentOrDefault(source.Definition.Defaults), ignoreCase: false, out var kind)
                ? kind
                : (AgentKind?)null;
            if (agent == AgentKind.ClaudeCode && seat.FactorySkills.Count > 0)
                files.AddRange(Plugin(source, seat));
        }

        files.Add((KitFile, KitJson(source, seat, files), "kit", Array.Empty<string>()));

        var result = files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new FactoryKitFileDto { Path = f.Path, Text = f.Text })
            .ToList();
        FileLog.Write($"[FactoryKitComposer] Compose: factory={source.Factory}, seat={seat.Seat}, kind={source.Kind}, files={result.Count}");
        return result;
    }

    /// <summary>
    /// The seat's rule sets merged: <c>deny</c> and <c>ask</c> unioned in the order the seat names its rule sets, each
    /// rule kept where it first appears, and <c>allow</c> left out (see <see cref="FactoryDefinitionCheck.AllowNotApplied"/>).
    /// </summary>
    internal static string Rules(FactoryDefinition definition, SeatDefinition seat)
    {
        var deny = new List<string>();
        var ask = new List<string>();
        var seenDeny = new HashSet<string>(StringComparer.Ordinal);
        var seenAsk = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in seat.Rules)
        {
            var set = definition.RuleSets.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
                ?? throw new ArgumentException($"The seat names the rule set '{name}', which this version does not hold.");
            foreach (var rule in set.Deny)
                if (seenDeny.Add(rule)) deny.Add(rule);
            foreach (var rule in set.Ask)
                if (seenAsk.Add(rule)) ask.Add(rule);
        }
        var doc = new Dictionary<string, object>
        {
            ["permissions"] = new Dictionary<string, object> { ["deny"] = deny, ["ask"] = ask },
        };
        return JsonSerializer.Serialize(doc, JsonOpts) + "\n";
    }

    private static string Brief(SeatDefinition seat)
    {
        var text = seat.Brief.ReplaceLineEndings("\n");
        return text.EndsWith('\n') ? text : text + "\n";
    }

    private static string Grants(FactoryDefinition definition, SeatDefinition seat)
    {
        var sb = new StringBuilder();
        sb.Append("# The grants of the seat ").Append(seat.Seat).Append("\n\n");
        if (seat.Grants.Count == 0)
        {
            sb.Append("This seat uses no grant.\n");
            return sb.ToString();
        }
        sb.Append("What this seat is allowed to do beyond its work, and the limits on each. A grant is a promise the owner made; keep inside its limits.\n");
        foreach (var id in seat.Grants)
        {
            var grant = definition.Grants.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal))
                ?? throw new ArgumentException($"The seat names the grant '{id}', which this version does not hold.");
            sb.Append("\n## ").Append(grant.Id).Append("\n\n");
            sb.Append("- Allows: ").Append(OneLine(grant.Allows)).Append('\n');
            sb.Append("- Limits: ").Append(grant.Limits is null ? "none written" : OneLine(grant.Limits)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The seat's own journal, newest first (<paramref name="total"/> is how many entries the seat has in all): at most <see cref="MaxJournalEntries"/> entries and
    /// <see cref="MaxJournalBytes"/> of their text, whichever is reached first. An entry is never cut in the middle: the
    /// first entry that would pass the byte limit ends the list, and the file says how many were left out.
    /// </summary>
    internal static string Journal(string factory, string seat, IReadOnlyList<FactoryKitJournalEntry> entries, int total)
    {
        var sb = new StringBuilder();
        sb.Append("# The journal of the seat ").Append(seat).Append("\n\n");
        sb.Append("Your own journal entries, newest first, as they stood when this session started. This is a copy: editing it changes nothing.\n\n");
        sb.Append("- Add an entry: `cc-devthrottle factory journal add --factory ").Append(factory).Append(" --seat ").Append(seat).Append(" --file <file>`\n");
        sb.Append("- Every entry: `cc-devthrottle factory journal list --factory ").Append(factory).Append(" --seat ").Append(seat).Append("`\n");

        var shown = new List<FactoryKitJournalEntry>();
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (shown.Count == MaxJournalEntries) break;
            var size = Encoding.UTF8.GetByteCount(entry.Text);
            if (bytes + size > MaxJournalBytes) break;
            bytes += size;
            shown.Add(entry);
        }

        if (entries.Count == 0)
        {
            sb.Append("\nThis seat has no journal entries yet.\n");
            return sb.ToString();
        }
        foreach (var entry in shown)
        {
            var by = string.IsNullOrEmpty(entry.AuthorId) ? entry.AuthorKind : $"{entry.AuthorKind} {entry.AuthorId}";
            sb.Append("\n## ").Append(entry.EntryDate).Append("\n\n");
            sb.Append("Entry ").Append(entry.Id).Append(", written ").Append(Utc(entry.WrittenAtUtc)).Append(" UTC by ").Append(by).Append(".\n\n");
            var text = entry.Text.ReplaceLineEndings("\n");
            sb.Append(text);
            if (!text.EndsWith('\n')) sb.Append('\n');
        }
        var left = Math.Max(total, entries.Count) - shown.Count;
        if (left > 0)
            sb.Append("\n").Append(left).Append(left == 1 ? " older entry is" : " older entries are")
              .Append(" not shown here; a kit carries the last ").Append(MaxJournalEntries).Append(" entries or ")
              .Append(MaxJournalBytes / 1024).Append(" KB, whichever is less. Read them with `cc-devthrottle factory journal get`.\n");
        return sb.ToString();
    }

    /// <summary>The factory's working documents with the command that reads each, and its attachments, listed only (A5).</summary>
    internal static string Documents(string factory, IReadOnlyList<FactoryDocumentSummary> documents,
        IReadOnlyList<FactoryKitAttachment> attachments, int attachmentsTotal)
    {
        var sb = new StringBuilder();
        sb.Append("# The working documents of the factory ").Append(factory).Append("\n\n");
        sb.Append("The documents as they stood when this session started. Their text is not copied here: read the current version with the command beside each, and write with `cc-devthrottle factory doc write`, stating the version you read.\n\n");
        if (documents.Count == 0)
            sb.Append("This factory has no working documents yet.\n");
        else
        {
            sb.Append("| Document | Version | Size (bytes) | Read it with |\n");
            sb.Append("|---|---|---|---|\n");
            foreach (var d in documents.OrderBy(d => d.Name, StringComparer.Ordinal))
                sb.Append("| ").Append(Cell(d.Name)).Append(" | ").Append(d.Version).Append(" | ").Append(d.TextBytes)
                  .Append(" | `cc-devthrottle factory doc get --factory ").Append(factory).Append(" --name ").Append(Cell(d.Name)).Append("` |\n");
        }

        sb.Append("\n## Attachments\n\n");
        sb.Append("The files the factory keeps. They are listed, never downloaded into the kit: fetch one with the command beside it when you need it.\n\n");
        if (attachments.Count == 0)
        {
            sb.Append("This factory keeps no attachments yet.\n");
            return sb.ToString();
        }
        sb.Append("| Attachment | Kept by | Size (bytes) | Written (UTC) | Fetch it with |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var a in attachments)
            sb.Append("| ").Append(Cell(a.Name)).Append(" | ").Append(a.Seat is null ? "the factory" : Cell(a.Seat)).Append(" | ")
              .Append(a.Bytes).Append(" | ").Append(Utc(a.WrittenAtUtc)).Append(" | `cc-devthrottle factory attach get --factory ")
              .Append(factory).Append(" --id ").Append(a.Id).Append(" --out <file>` |\n");
        if (attachmentsTotal > attachments.Count)
            sb.Append("\n").Append(attachmentsTotal - attachments.Count).Append(" more attachments are not listed; see them all with `cc-devthrottle factory attach list --factory ")
              .Append(factory).Append("`.\n");
        return sb.ToString();
    }

    private static IEnumerable<(string Path, string Text, string Source, IReadOnlyList<string> From)> Plugin(FactoryKitSource source, SeatDefinition seat)
    {
        var manifest = new Dictionary<string, object>
        {
            ["name"] = PluginName,
            ["description"] = $"The factory skills of the seat {seat.Seat} of {source.Factory}, from version {source.Version} of its definition.",
            ["version"] = $"{source.Version}.0.0",
        };
        yield return ($"{PluginFolder}/.claude-plugin/plugin.json", JsonSerializer.Serialize(manifest, JsonOpts) + "\n", "kit", Array.Empty<string>());

        foreach (var name in seat.FactorySkills.OrderBy(n => n, StringComparer.Ordinal))
        {
            var prefix = $"skills/{name}/";
            var paths = source.Content.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (!paths.Contains(prefix + DefinitionPaths.SkillMainFile))
                throw new ArgumentException($"The seat names the factory skill '{name}', which this version does not hold.", nameof(source));
            foreach (var path in paths)
                yield return ($"{PluginFolder}/{path}", source.Content[path], "definition", new[] { path });
        }
    }

    private static string KitJson(FactoryKitSource source, SeatDefinition seat,
        List<(string Path, string Text, string Source, IReadOnlyList<string> From)> files)
    {
        var doc = new Dictionary<string, object?>
        {
            ["factory"] = source.Factory,
            ["seat"] = seat.Seat,
            ["version"] = source.Version,
            ["kind"] = source.Kind,
            ["builtAtUtc"] = source.BuiltAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        // The seat's secret names travel on a seat kit only: phase 5 P4 checks each one on the machine before the agent
        // starts. A child is not the seat and needs none of them. The names only - a value never reaches the Gateway.
        if (source.Kind == FactoryKits.Seat)
            doc["secrets"] = seat.Secrets.ToList();
        doc["files"] = files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new Dictionary<string, object> { ["path"] = f.Path, ["source"] = f.Source, ["from"] = f.From })
            .ToList();
        return JsonSerializer.Serialize(doc, JsonOpts) + "\n";
    }

    private static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();

    private static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
