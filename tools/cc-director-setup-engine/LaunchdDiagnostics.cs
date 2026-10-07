using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CcDirector.Setup.Engine;

/// <summary>
/// What launchd and our own log files say about the launcher, gathered at the moment an install step
/// fails. It exists because "launchd did not report which process is running the launcher" told a user
/// (and us) nothing: launchd had the answer the whole time - the job's state, how it last exited, whether
/// it could be spawned at all - and the installer read it and threw it away.
///
/// Pure parsing lives here so it is testable on any operating system; the caller supplies the text.
/// </summary>
public static class LaunchdDiagnostics
{
    /// <summary>The value of one "key = value" field, or null.</summary>
    internal static string? Field(string? launchctlPrintOutput, string key)
    {
        if (string.IsNullOrWhiteSpace(launchctlPrintOutput)) return null;
        foreach (var raw in launchctlPrintOutput.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// One plain sentence for the person at the screen: what launchd says happened to the launcher.
    /// Null when launchd said nothing we can put into words.
    /// </summary>
    internal static string? Explain(string? launchctlPrintOutput, bool jobLoaded)
    {
        if (!jobLoaded)
            return "macOS no longer has the launcher registered: it was unloaded after it was registered";

        var exitCode = Field(launchctlPrintOutput, "last exit code");
        var signal = Field(launchctlPrintOutput, "last terminating signal");
        var state = Field(launchctlPrintOutput, "state");
        var runs = Field(launchctlPrintOutput, "runs");
        var jobState = Field(launchctlPrintOutput, "job state");

        // "spawn failed" is launchd saying the program NEVER RAN: it could not set the job up (open its log
        // files, enter its folders, execute the file). The sentence used to say "started but did not stay
        // running", which sent a user looking at logs a program that never ran could not have written.
        // Exit 78 (EX_CONFIG) is the code launchd's spawner answers with when the job could not be set up, and it
        // is a refusal whatever "job state" says beside it: a Mac whose domain schedules a retry shows "spawn
        // scheduled", one whose domain does not shows "spawn failed", and the program ran in neither.
        var refused = string.Equals(jobState, "spawn failed", StringComparison.OrdinalIgnoreCase)
                      || (exitCode is not null && exitCode.StartsWith("78", StringComparison.Ordinal));
        if (refused)
        {
            var tried = string.IsNullOrEmpty(runs) ? "" : $" macOS tried {runs} time(s)";
            var code = string.IsNullOrEmpty(exitCode) || exitCode == "(never exited)" ? "" : $" ({exitCode})";
            var says = string.Equals(jobState, "spawn failed", StringComparison.OrdinalIgnoreCase) ? "\"spawn failed\"" : $"\"{jobState ?? state ?? "not running"}\"";
            return $"macOS could not start the launcher at all: launchd reports {says}{code}, so the program never ran a line.{tried}";
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(signal)) parts.Add($"it was stopped by {signal}");
        else if (!string.IsNullOrEmpty(exitCode) && exitCode != "(never exited)") parts.Add($"it exited with code {exitCode}");
        if (!string.IsNullOrEmpty(runs)) parts.Add($"macOS started it {runs} time(s)");
        if (!string.IsNullOrEmpty(state)) parts.Add($"its state is now \"{state}\"");
        return parts.Count == 0 ? null : "The launcher started but did not stay running: " + string.Join(", ", parts);
    }

    /// <summary>The last <paramref name="maxLines"/> non-empty lines of a text file, or null when the
    /// file does not exist or is empty.</summary>
    public static string? Tail(string path, int maxLines)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return null;
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of the newest <c>launcher-*.log</c> in
    /// <paramref name="launcherLogDir"/> - the launcher's own record, which says WHY it stopped when launchd
    /// only knows THAT it did (issue #3311, B4). Read with write sharing, because a launcher that is still
    /// running holds the file open. Null when there is no such log.
    /// </summary>
    internal static string? LauncherLogTail(string launcherLogDir, int maxLines)
    {
        if (!Directory.Exists(launcherLogDir)) return null;
        var newest = new DirectoryInfo(launcherLogDir).GetFiles("launcher-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return null;
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                if (line.Trim().Length > 0) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said in the report rather than thrown: one unreadable file must not cost the whole report.
            return $"{newest.Name}: could not be read ({ex.GetType().Name}: {ex.Message})";
        }
        if (lines.Count == 0) return null;
        return newest.Name + ":\n" + string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    /// <summary>
    /// One titled part of a report, and which end of it matters when it has to be cut: the END of a log
    /// (the refusal is the last thing written) or the START of a listing (the header and the first entries).
    /// </summary>
    internal sealed record Section(string Title, string Body, bool KeepEnd);

    /// <summary>
    /// The characters the engine's part of a report may use. The Gateway keeps the first
    /// <see cref="CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics"/> characters of a report's
    /// diagnostics and drops the rest WITHOUT a word, so anything past that line never existed as far as the
    /// reader is concerned. The setup wizard appends its own log after this text; the reserve keeps room for it.
    /// </summary>
    internal const int DiagnosticsBudget = CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics - WizardAppendixReserve;

    /// <summary>Room left after the engine's text for the wizard's exception and setup log tail.</summary>
    internal const int WizardAppendixReserve = 2500;

    /// <summary>No section is cut below this many characters: its first or last lines always survive.</summary>
    internal const int MinSectionChars = 240;

    /// <summary>Characters a cut note takes, so one cut lands under the budget instead of needing another.</summary>
    private const int CutNoteRoom = 80;

    /// <summary>
    /// Renders the sections as one text that fits <paramref name="budget"/>. When it does not fit, the
    /// longest section is cut first, from the end it cares least about, down to what is needed and never
    /// below <see cref="MinSectionChars"/>, and the cut is written into the section itself. A reader sees
    /// every title, every short answer whole, and exactly how much of a long one was left out - instead of a
    /// report that silently ends in the middle of the system log.
    /// </summary>
    internal static string Fit(IReadOnlyList<Section> sections, int budget)
    {
        var bodies = sections.Select(s => (s.Body ?? "").Replace("\r\n", "\n").Trim('\n')).ToArray();
        var removed = new int[sections.Count];
        var text = Render(sections, bodies, removed);
        while (text.Length > budget)
        {
            var longest = -1;
            for (var i = 0; i < bodies.Length; i++)
                if (bodies[i].Length > MinSectionChars && (longest < 0 || bodies[i].Length > bodies[longest].Length)) longest = i;
            if (longest < 0) break; // every section is at its floor; the hard cut below is all that is left

            var body = bodies[longest];
            var keep = Math.Max(MinSectionChars, body.Length - (text.Length - budget + CutNoteRoom));
            string cut;
            if (sections[longest].KeepEnd)
            {
                var start = body.Length - keep;
                var lineStart = body.IndexOf('\n', start);
                if (lineStart >= 0 && body.Length - (lineStart + 1) >= MinSectionChars / 2) start = lineStart + 1;
                cut = body[start..];
            }
            else
            {
                var end = keep;
                var lineEnd = body.LastIndexOf('\n', end - 1);
                if (lineEnd >= MinSectionChars / 2) end = lineEnd;
                cut = body[..end];
            }
            if (cut.Length >= body.Length) break;
            removed[longest] += body.Length - cut.Length;
            bodies[longest] = cut;
            text = Render(sections, bodies, removed);
        }
        if (text.Length > budget)
        {
            // Every section is at its floor and the text is still over: the contract holds anyway, and the
            // cut is said, so a reader knows the report ends here on purpose.
            const string note = "\n(cut to fit the report)";
            text = budget > note.Length ? text[..(budget - note.Length)] + note : text[..Math.Max(0, budget)];
        }
        return text;
    }

    private static string Render(IReadOnlyList<Section> sections, string[] bodies, int[] removed)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sections.Count; i++)
        {
            sb.Append(sections[i].Title).Append(":\n");
            if (removed[i] > 0 && sections[i].KeepEnd) sb.Append($"  ({removed[i]} earlier characters left out to fit the report)\n");
            sb.Append(bodies[i].Length == 0 ? "  (no output)" : "  " + bodies[i].Replace("\n", "\n  ")).Append('\n');
            if (removed[i] > 0 && !sections[i].KeepEnd) sb.Append($"  ({removed[i]} later characters left out to fit the report)\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The job's own scalar fields worth a report. Everything inside a nested block - arguments,
    /// environment, endpoints, event triggers - is left out whatever it is called.</summary>
    private static readonly string[] JobFields =
    [
        "state", "pid", "runs", "last exit code", "last exit reason", "last terminating signal", "job state",
        "spawn type", "immediate reason", "program", "path", "active count", "type", "domain", "minimum runtime",
        "exit timeout", "properties", "last spawn", "bundle id", "forks", "execs", "initialized", "trampolined",
        "started suspended", "proxy started suspended", "last exit status", "spawn flags", "run count",
    ];

    /// <summary>The user domain's own fields worth a report: what kind of domain it is and how it runs its jobs.</summary>
    private static readonly string[] DomainFields =
    [
        "type", "handle", "state", "active count", "on-demand count", "service count", "active service count",
        "created", "properties", "bootstrapper", "domain", "uid",
    ];

    /// <summary>
    /// Named facts from <c>launchctl print gui/&lt;uid&gt;/&lt;label&gt;</c>, nothing else. The job print lists
    /// its arguments, its environment and its endpoints; only the allowlisted scalar fields are sent, and no
    /// nested block ever is.
    /// </summary>
    internal static string JobFacts(string? jobPrint)
        => NamedFacts(jobPrint, JobFields, "(launchd gave no answer for the job)", "(no named job fields in launchd's answer)");

    /// <summary>
    /// Named facts from <c>launchctl print gui/&lt;uid&gt;</c>, nothing else. The domain print lists every
    /// service, every endpoint and the login environment; only its own header fields say anything about why a
    /// job is not started (a domain in on-demand-only mode starts nothing by itself), and those are allowlisted
    /// here by name. Anything not named is not sent.
    /// </summary>
    internal static string DomainFacts(string? domainPrint)
        => NamedFacts(domainPrint, DomainFields, "(launchd gave no answer for the domain)", "(no named domain fields in launchd's answer)",
            alsoKeep: key => key.Contains("on-demand", StringComparison.OrdinalIgnoreCase));

    private static string NamedFacts(string? print, string[] fields, string whenEmpty, string whenNoneNamed, Func<string, bool>? alsoKeep = null)
    {
        if (string.IsNullOrEmpty(print)) return whenEmpty;
        var kept = new List<string>();
        var depth = 0;
        foreach (var raw in print.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (depth > 0)
            {
                if (line.EndsWith('{')) depth++;
                else if (line == "}") depth--;
                continue;
            }
            if (line.EndsWith('{'))
            {
                // The print's own opening line (gui/501 = {, gui/501/label = {) is the frame, not a block to skip.
                if (line.StartsWith("gui/", StringComparison.Ordinal) || line.StartsWith("user/", StringComparison.Ordinal)
                    || line.StartsWith("system/", StringComparison.Ordinal) || line.StartsWith("pid/", StringComparison.Ordinal))
                    continue;
                depth = 1; // services, endpoints, environment, arguments, submitters: whole blocks, none of them wanted
                continue;
            }
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (fields.Contains(key) || (alsoKeep is not null && alsoKeep(key)))
                kept.Add(line);
        }
        return kept.Count == 0 ? whenNoneNamed : string.Join('\n', kept);
    }

    /// <summary>The property list keys sent as they are. Paths of ours and plain settings.</summary>
    private static readonly string[] PlistScalarKeys =
    [
        "Label", "ProcessType", "LimitLoadToSessionType", "StandardOutPath", "StandardErrorPath", "WorkingDirectory", "Program",
    ];

    /// <summary>
    /// Named facts from a launch agent property list, nothing else: the label, the Program key, the log paths,
    /// the start and keep-alive settings. ProgramArguments is counted when it is an array and no element of it
    /// is sent - not the first one either, a value a person typed into the file like any other; environment
    /// variables are counted when they are a dictionary and never sent; a value of the wrong kind is named by
    /// its kind and not sent; a key this does not know is named and not sent.
    /// </summary>
    internal static string PlistFacts(string plistXml)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(plistXml ?? ""),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return $"(not a readable property list: {ex.Message})";
        }
        var dict = doc.Root?.Element("dict");
        if (dict is null) return "(no dictionary in the property list)";
        var facts = new List<string>();
        foreach (var (key, value) in Pairs(dict))
        {
            var kind = value.Name.LocalName;
            if (PlistScalarKeys.Contains(key))
                facts.Add(kind == "string" ? $"{key} = {value.Value.Trim()}" : $"{key} = (a {kind}, not a string; not sent)");
            else if (PlistBooleanKeys.Contains(key))
                facts.Add(kind is "true" or "false" ? $"{key} = {kind}" : $"{key} = (a {kind}, not a boolean; not sent)");
            else if (key == "KeepAlive")
                facts.Add(kind == "dict"
                    ? $"KeepAlive = dict ({string.Join(", ", Pairs(value).Select(KeepAliveFact))})"
                    : kind is "true" or "false" ? $"KeepAlive = {kind}" : $"KeepAlive = (a {kind}; not sent)");
            else if (key == "ProgramArguments")
                facts.Add(kind == "array"
                    ? $"ProgramArguments = {value.Elements().Count()} entries (not sent)"
                    : $"ProgramArguments = (a {kind}, not an array; not sent)");
            else if (key == "EnvironmentVariables")
                facts.Add(kind == "dict"
                    ? $"EnvironmentVariables = present ({Pairs(value).Count()} entries, not sent)"
                    : $"EnvironmentVariables = (a {kind}, not a dictionary; not sent)");
            else
                facts.Add($"{key} = (not sent)");
        }
        return facts.Count == 0 ? "(an empty property list)" : string.Join('\n', facts);
    }

    /// <summary>The start settings whose value is a boolean element (<c>&lt;true/&gt;</c> or <c>&lt;false/&gt;</c>).</summary>
    private static readonly string[] PlistBooleanKeys = ["RunAtLoad", "Disabled", "EnableTransactions", "LaunchOnlyOnce"];

    /// <summary>The KeepAlive children launchd defines as booleans. Any other child is named and not sent: a
    /// dictionary an allowed key holds is not a licence to send what is inside it.</summary>
    private static readonly string[] KeepAliveBooleanKeys = ["SuccessfulExit", "Crashed", "AfterInitialDemand"];

    private static string KeepAliveFact((string Key, XElement Value) pair)
    {
        var kind = pair.Value.Name.LocalName;
        if (KeepAliveBooleanKeys.Contains(pair.Key))
            return kind is "true" or "false" ? $"{pair.Key} = {kind}" : $"{pair.Key} = (a {kind}, not a boolean; not sent)";
        return $"{pair.Key} = (not sent)";
    }

    private static IEnumerable<(string Key, XElement Value)> Pairs(XElement dict)
    {
        var nodes = dict.Elements().ToList();
        for (var i = 0; i + 1 < nodes.Count; i += 2)
            yield return (nodes[i].Value.Trim(), nodes[i + 1]);
    }
}
