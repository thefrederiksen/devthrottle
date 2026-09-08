using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of the reference's <c>origin.py</c> (Development Mentor harness, origin classification v1):
/// every prompt-log USER record gets one <c>origin</c> from an ordered set of passes, and the rule that
/// decided it is kept beside it as <c>origin_rule</c>. The four classes:
///
///     human        the developer's own typed or spoken words
///     agent        text another session sent (a fleet message, an ask, a broadcast delivery)
///     framework    text the product authored itself, or text the agent tool wrote into its own transcript
///     unresolved   a record with no stamp, no framing and no ledger event that names a driver
///                  (the product gap thefrederiksen/devthrottle#2639)
///
/// The passes, each over the week's user records in time order; the first pass that takes a record decides
/// it (the reference's module docstring is the specification; the numbered comments below are copied from
/// it so this port and the reference are read against the same words):
///
///     0. strip       a framing the agent tool PREPENDS to the developer's own words (<see cref="Strips"/>:
///                    the tool's [Image #N] markers; a skill document the tool substituted for a slash
///                    command, when its '## Input' heading is there) is cut off the record. The cut is
///                    bounded to the FRAMING'S OWN CHARACTERS and nothing else: the marker run - the markers
///                    and the one separator between two markers in a run - never the whitespace after the
///                    last marker, never a word after it; the expansion through the end of its last
///                    '## Input' line, that line's newline included, never a character after it. Nothing is
///                    trimmed before or after either cut, and a record that begins with whitespace is not cut
///                    at all. So the stored text is the cut plus the text, character for character, always.
///                    A record with no WORD left is framework, rule "framing-only:name", and claims no
///                    event. NO PATH IS EVER STRIPPED, on any record, whatever its stamp (the Architect's
///                    ruling on inspection 4 finding 1; thefrederiksen/devthrottle#2771).
///     1. envelope    a product framing from <see cref="Envelopes"/>, stamped or not. Stamped:
///                    "envelope-over-stamp:name", claims no event. Unstamped: "envelope:name", claims the
///                    nearest unclaimed UNSTAMPED event.
///     2. agent-tool  a framing the agent tool writes into its transcript (<see cref="AgentToolFramings"/>),
///                    EXCEPT the command-message entry, is framework, stamped or not; neither claims an
///                    event. A command-message record goes to pass 4.
///     3. stamped     modality typed or voice on the record itself is human, surface the record's own; the
///                    record claims its nearest unclaimed turn-submitted event within
///                    <see cref="LedgerJoinSeconds"/>.
///     4. ledger      what is left, command-message records FIRST against the nearest unclaimed STAMPED
///                    event (human, "ledger-origin", else framework); then every other record against its
///                    nearest unclaimed event of any kind: ledger-origin, ledger-framework, ledger-agent,
///                    a Delivery with no origin stops the run, ledger-userinput-2639, ledger-unstamped,
///                    no-ledger-event.
///
/// Every regular expression here is the reference's, translated so that .NET agrees with Python's <c>re</c>
/// on every string the reference's tests use: Python's <c>\s</c> is <see cref="PyText.SpaceClass"/>, its
/// <c>\S</c> is <see cref="PyText.NonSpaceClass"/>, and the tests in OriginStripTests pin the .NET behaviour
/// to the Python one, never the other way round. ASCII only. No fallbacks: an unknown token stops the run
/// naming the place.
/// </summary>
public static class Origin
{
    public static readonly string[] Classes = { "human", "agent", "framework", "unresolved" };
    public static readonly string[] Modalities = { "typed", "voice" };
    public static readonly string[] Surfaces = { "desktop", "phone", "cockpit", "unknown" };

    /// <summary>Measured on the owner's 2026-W35 stamped records: the 99th percentile of the distance to the
    /// nearest turn-submitted event, rounded up to a whole second (the reference's PROOF.md).</summary>
    public const int LedgerJoinSeconds = 23;

    public const string TurnEventType = "turn-submitted";

    // The product's payload-file wrapper, in the three parts the class is anchored to (see the payload-file
    // entry of Envelopes). The dated NAME is LargeInputHandler.cs's input_yyyyMMdd_HHmmss_<six characters>.txt
    // as the terminal capture leaves it (7 or 8 digits; 2 to 10 suffix characters). The HEADS are the only
    // things the capture has ever left in front of the name over the whole raw snapshot. '.temp' must START
    // within PayloadTempWindow characters AFTER the name ends.
    public const string PayloadName = @"\d{7,8}_\d{6}_[a-z0-9]{2,10}\.txt";
    public const string PayloadHeads = @"(?:Read file input_Read file input_\d{7,8}_\d{6}_|Read file input_|put_)?";
    public const int PayloadTempWindow = 120;

    /// <summary>A product framing: (name, kind, pattern, class, citation). kind is "prefix" (literal, anchored at
    /// the start), "exact" (the whole record), or "regex" (anchored). A framing the product does not write is
    /// not in this table.</summary>
    public sealed record Envelope(string Name, string Kind, string Pattern, string Class, string Citation);

    /// <summary>The product's framings, read from the product source (the reference's ENVELOPES table, cited entry by entry there).</summary>
    public static readonly Envelope[] Envelopes =
    {
        new("fleet-message", "prefix", "Message [message from ", "agent",
            "CcDirector.Gateway.Contracts/FleetMessaging.cs BuildFramedMessage returns 'Message ' + header + ' ' + text."),
        new("handover-desktop", "regex", "^@" + PyText.NonSpaceClass + @"+ This is a handover document from a previous session\. ", "framework",
            "CcDirector.Avalonia/MainWindow.axaml.cs InjectHandoverPromptAsync, sent with SendSource.Framework."),
        new("handover-api", "prefix",
            "You are picking up an in-progress session that was running in another instance. Here is the context.",
            "framework",
            "CcDirector.Core/Claude/SummaryBuilder.cs FormatAsHandoverPrompt; sent with SendSource.Framework."),
        new("handover-command", "exact", "/handover", "framework",
            "CcDirector.Avalonia/MainWindow.axaml.cs BtnHandover_Click sends '/handover' with SendSource.Framework."),
        // The CLASS is anchored to the PRODUCT'S SHAPE, not to co-occurrence (the Architect's ruling on
        // inspection 4 finding 2): the dated payload file name sits at the RECORD START or is preceded ONLY by
        // one of the heads the capture is known to leave, and '.temp' starts within PayloadTempWindow
        // characters AFTER the name. Any other prefix - the developer's own words included - is not the class.
        new("payload-file", "regex",
            "^" + PayloadHeads + PayloadName + @"(?=[\s\S]{0," + PayloadTempWindow + @"}\.temp)",
            "framework",
            "CcDirector.Core/Drivers/TerminalSubmit.cs SubmitViaInstructionFileAsync submits 'Read file <name> in the .temp directory. Path: ...' in place of a long prompt."),
    };

    /// <summary>The AGENT TOOL's own transcript framings (name, kind, pattern, why). These never crossed the
    /// product's send choke point, so the ledger has no event for them; a stamp on one is the ingest's join
    /// artefact. The command-message entry is the EXCEPTION: it is tried against the ledger first (pass 4).</summary>
    public static readonly (string Name, string Kind, string Pattern, string Why)[] AgentToolFramings =
    {
        ("task-notification", "prefix", "<task-notification>", "the tool's wrapper for a finished background task's result"),
        ("command-message", "prefix", "<command-message>", "the tool's wrapper for a slash command; the developer may have typed the command"),
        ("command-name", "prefix", "<command-name>", "the tool's slash-command wrapper when the command-message element is absent"),
        ("local-command-stdout", "prefix", "<local-command-stdout>", "the tool's capture of a local command's output"),
        ("local-command-caveat", "prefix", "<local-command-caveat>", "the tool's caveat ahead of local-command output"),
        ("environment-context", "prefix", "<environment_context>", "the tool's environment block injected as a user turn"),
        ("system-reminder", "prefix", "<system-reminder>", "the tool's reminder block injected as a user turn"),
        ("request-interrupted", "prefix", "[Request interrupted by user", "the tool's marker written when the developer interrupted a turn"),
        ("image-marker", "prefix", "[Image: ", "the tool's marker for an image pasted into the composer"),
        ("continuation-summary", "prefix", "This session is being continued from a previous", "the tool's summary before a compaction, replayed as the first user turn"),
        ("skill-injection", "prefix", "Base directory for this skill: ", "the tool's skill loader"),
        ("agents-md-instructions", "prefix", "# AGENTS.md instructions for ", "the tool's injection of a repository's AGENTS.md instructions"),
        ("autonomous-loop-tick", "prefix", "# Autonomous loop tick", "the tool's own loop wake-up, written as a user turn"),
        ("autonomous-loop-check", "prefix", "# Autonomous loop check", "the tool's own loop check, written as a user turn"),
        ("bash-input", "prefix", "<bash-input>", "the tool's wrapper for a shell command the developer ran in the tool's shell mode"),
    };
    public const string CommandMessageEntry = "command-message";

    // attachment-path: [Image #N] markers at the record start, and ONLY those. The cut is the marker RUN
    // and nothing else: the markers and the ONE whitespace character between two markers in a run - never
    // the whitespace after the last marker, never a word. A run whose markers sit more than one character
    // apart is cut through the last marker the single separators reach, and the rest stays in the text.
    public const string ImageMarkerRe = @"\[Image #\d+\]";
    public static readonly Regex ImageMarkersRe = new("^" + ImageMarkerRe + "(?:" + PyText.SpaceClass + ImageMarkerRe + ")*", RegexOptions.CultureInvariant);

    // expanded-command: the record BEGINS with the skill's own document (its title line '# /<name> ...') AND
    // carries a '## Input' heading the tool appends; the developer's input is what follows the LAST such
    // heading. The cut is the expansion through the end of its last '## Input' line, that line's newline
    // included, and NOTHING after it.
    public static readonly Regex ExpandedCommandRe = new("^# /[A-Za-z][A-Za-z0-9_-]*(?=" + PyText.SpaceClass + ")", RegexOptions.CultureInvariant);
    public const string InputHeading = "\n## Input\n";

    /// <summary>A pass-0 strip: the matcher takes the record text AS STORED and answers the exact length of the
    /// framing prefix to cut (in UTF-16 units of the text), or null.</summary>
    public sealed record Strip(string Name, Func<string, int?> Matcher, string Citation);

    public static int? StripAttachmentPath(string head)
    {
        var markers = ImageMarkersRe.Match(head);
        return markers.Success ? markers.Length : null;
    }

    public static int? StripExpandedCommand(string head)
    {
        if (!ExpandedCommandRe.IsMatch(head)) return null;
        var at = head.LastIndexOf(InputHeading, StringComparison.Ordinal);
        if (at < 0) return null;
        return at + InputHeading.Length;
    }

    private static readonly Strip[] DefaultStrips =
    {
        new("attachment-path", StripAttachmentPath,
            "The agent tool's own [Image #N] placeholder for an image pasted into its composer, as observed in the stored "
            + "records. The product inserts a dropped or uploaded file's path at the caret and does not record that it did "
            + "(thefrederiksen/devthrottle#2771); until it does NO PATH IS STRIPPED, on any record, whatever its stamp."),
        new("expanded-command", StripExpandedCommand,
            "The agent tool's expansion of a user-invoked skill: the skill document first (title '# /<name> - ...'), then "
            + "'## Input' and the developer's input - whatever the record's stamp says. The heading is REQUIRED."),
    };

    /// <summary>
    /// Framings PREPENDED to the developer's own words (pass 0), in order. Settable ONLY so a test can widen
    /// an entry in memory and watch the no-path property go red, exactly as the reference's inspectors did;
    /// nothing in the product assigns it.
    /// </summary>
    public static Strip[] Strips { get; internal set; } = DefaultStrips;

    /// <summary>The table as shipped, so a test that widened it can put it back.</summary>
    public static Strip[] ShippedStrips => DefaultStrips;

    public static readonly HashSet<string> SendSources = new(StringComparer.Ordinal) { "UserInput", "Delivery", "Agent", "Framework" };

    // ------------------------------------------------------------------ matching

    private sealed record Compiled(string Name, string Kind, string Pattern, Regex? Regex);

    private static Compiled[] Compile(IEnumerable<(string Name, string Kind, string Pattern)> entries)
    {
        var compiled = new List<Compiled>();
        foreach (var (name, kind, pattern) in entries)
        {
            if (kind == "regex")
                compiled.Add(new Compiled(name, kind, pattern, new Regex(pattern, RegexOptions.CultureInvariant)));
            else if (kind is "prefix" or "exact")
                compiled.Add(new Compiled(name, kind, pattern, null));
            else
                throw new MentorDataException("Origin table entry '" + name + "' has an unknown kind '" + kind + "'.");
        }
        return compiled.ToArray();
    }

    private static readonly Compiled[] CompiledEnvelopes = Compile(Envelopes.Select(e => (e.Name, e.Kind, e.Pattern)));
    private static readonly Compiled[] CompiledAgentTool = Compile(AgentToolFramings.Select(e => (e.Name, e.Kind, e.Pattern)));
    private static readonly Dictionary<string, string> EnvelopeClass = Envelopes.ToDictionary(e => e.Name, e => e.Class, StringComparer.Ordinal);

    private static string? Match(string text, Compiled[] compiled)
    {
        var head = PyText.LStrip(text);
        foreach (var entry in compiled)
        {
            if (entry.Kind == "prefix" && head.StartsWith(entry.Pattern, StringComparison.Ordinal)) return entry.Name;
            if (entry.Kind == "exact" && PyText.Strip(head) == entry.Pattern) return entry.Name;
            if (entry.Kind == "regex" && entry.Regex!.IsMatch(head)) return entry.Name;
        }
        return null;
    }

    /// <summary>The Envelopes entry name a text starts with, or null.</summary>
    public static string? EnvelopeOf(string text) => Match(text, CompiledEnvelopes);

    /// <summary>The AgentToolFramings entry name a text starts with, or null.</summary>
    public static string? AgentToolFramingOf(string text) => Match(text, CompiledAgentTool);

    /// <summary>
    /// (Strips entry name, index in <paramref name="text"/> where the developer's part starts) for a text
    /// that starts with a prepended framing, or (null, 0). The text alone decides; it is matched AS STORED
    /// and the index is exactly the framing's length.
    /// </summary>
    public static (string? Name, int Cut) StripOf(string text)
    {
        foreach (var strip in Strips)
        {
            var cut = strip.Matcher(text);
            if (cut is not null) return (strip.Name, cut.Value);
        }
        return (null, 0);
    }

    /// <summary>The product's own rule for wordCount: split on whitespace (ConversationIngestor.cs:209).</summary>
    public static int CountWords(string text) => PyText.CountWords(text);

    // ------------------------------------------------------------------ ledger

    /// <summary>'typed/desktop' becomes (typed, desktop); null stays null; anything else stops the run.</summary>
    public static (string Modality, string Surface)? ParseInputOrigin(string? token, string where)
    {
        if (token is null) return null;
        var slash = token.IndexOf('/');
        if (slash < 0)
            throw new MentorDataException("InputOrigin '" + token + "' at " + where + " is not '<modality>/<surface>'.");
        var modality = token.Substring(0, slash);
        var surface = token.Substring(slash + 1);
        if (!Modalities.Contains(modality))
            throw new MentorDataException("InputOrigin modality '" + modality + "' at " + where + " is not typed or voice.");
        if (!Surfaces.Contains(surface))
            throw new MentorDataException("InputOrigin surface '" + surface + "' at " + where + " is not one of " + string.Join(", ", Surfaces) + ".");
        return (modality, surface);
    }

    /// <summary>The turn-submitted events per session, sorted, with a claimed flag per event.</summary>
    public sealed class Ledger
    {
        public sealed class Row
        {
            public required DateTime Ts { get; init; }
            public string? Source { get; init; }
            public (string Modality, string Surface)? Origin { get; init; }
            public bool Claimed { get; set; }
            public required string Where { get; init; }
        }

        private readonly Dictionary<string, List<Row>> _bySession = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DateTime>> _times = new(StringComparer.Ordinal);

        public Ledger(IEnumerable<MentorEvent> events)
        {
            foreach (var e in events)
            {
                if (e.Type != TurnEventType) continue;
                var source = e.SendSource;
                if (source is not null && !SendSources.Contains(source))
                    throw new MentorDataException("turn-submitted event " + e.Where + " has an unknown SendSource '" + source + "'.");
                var origin = ParseInputOrigin(e.InputOrigin, e.Where);
                if (!_bySession.TryGetValue(e.Session, out var rows))
                    _bySession[e.Session] = rows = new List<Row>();
                rows.Add(new Row { Ts = e.Ts, Source = source, Origin = origin, Claimed = false, Where = e.Where });
            }
            foreach (var (session, rows) in _bySession)
            {
                var sorted = rows.OrderBy(r => r.Ts).ToList();      // a stable sort, as Python's list.sort is
                rows.Clear();
                rows.AddRange(sorted);
                _times[session] = rows.Select(r => r.Ts).ToList();
            }
        }

        /// <summary>The nearest unclaimed event of the session within <see cref="LedgerJoinSeconds"/>, or null.
        /// want null accepts any event; "stamped" only events with an input origin; "unstamped" only events without.</summary>
        public Row? NearestUnclaimed(string session, DateTime ts, string? want = null)
        {
            if (!_bySession.TryGetValue(session, out var rows) || rows.Count == 0) return null;
            var times = _times[session];
            var i = BisectLeft(times, ts);
            Row? best = null;
            double? bestDelta = null;
            // Walk outward from the insertion point in both directions until past the tolerance.
            var lo = i - 1;
            var hi = i;
            while (lo >= 0 || hi < rows.Count)
            {
                var candidates = new List<int>(2);
                if (lo >= 0) candidates.Add(lo);
                if (hi < rows.Count) candidates.Add(hi);
                var progressed = false;
                foreach (var j in candidates)
                {
                    var delta = Math.Abs((rows[j].Ts - ts).TotalSeconds);
                    if (delta > LedgerJoinSeconds) continue;
                    progressed = true;
                    var row = rows[j];
                    var acceptable = !row.Claimed
                        && (want is null
                            || (want == "stamped" && row.Origin is not null)
                            || (want == "unstamped" && row.Origin is null));
                    if (acceptable && (bestDelta is null || delta < bestDelta))
                    {
                        best = row;
                        bestDelta = delta;
                    }
                }
                if (!progressed) break;
                lo--;
                hi++;
            }
            return best;
        }

        private static int BisectLeft(List<DateTime> times, DateTime value)
        {
            var lo = 0;
            var hi = times.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (times[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    // ------------------------------------------------------------------ classify

    /// <summary>The Counter of (origin, rule) the reference answers: insertion ordered, one count per pair.</summary>
    public sealed class Counts
    {
        private readonly Dictionary<(string Origin, string Rule), int> _counts = new();
        private readonly List<(string Origin, string Rule)> _order = new();

        public void Add(string origin, string rule)
        {
            var key = (origin, rule);
            if (_counts.TryGetValue(key, out var n)) _counts[key] = n + 1;
            else { _counts[key] = 1; _order.Add(key); }
        }

        public int this[(string Origin, string Rule) key] => _counts.TryGetValue(key, out var n) ? n : 0;
        public IEnumerable<KeyValuePair<(string Origin, string Rule), int>> Items => _order.Select(k => new KeyValuePair<(string, string), int>(k, _counts[k]));
        public int Total => _counts.Values.Sum();
    }

    private static void Set(MentorRecord p, string? origin, string? rule, string? modality = null, string? surface = null)
    {
        p.Origin = origin;
        p.OriginRule = rule;
        p.OriginModality = modality;
        p.OriginSurface = surface;
    }

    private static bool Stamped(MentorRecord p) => p.Modality is not null && Modalities.Contains(p.Modality);

    /// <summary>
    /// Classify every user-role record in place; answer the Counter of (origin, rule). Records are processed
    /// in time order within each pass, and the passes run in the order of the class summary. Non-user records
    /// get origin null.
    /// </summary>
    public static Counts Classify(IReadOnlyList<MentorRecord> prompts, IEnumerable<MentorEvent> events)
    {
        var ordered = prompts.Where(p => p.Role == "user").OrderBy(p => p.Ts).ToList();
        var ledger = new Ledger(events);
        var counts = new Counts();

        void Take(MentorRecord p, string origin, string rule, string? modality = null, string? surface = null)
        {
            Set(p, origin, rule, modality, surface);
            counts.Add(origin, rule);
        }

        // Pass 0: a framing the agent tool prepended to the developer's words is cut off (Strips) and nothing
        // else: the remainder is kept as stored, so original == original[:chars] + text always holds. The
        // record's word count is re-counted the product's way and what came off is recorded beside it. No
        // word left: framework, no event claimed.
        var kept = new List<MentorRecord>();
        foreach (var p in ordered)
        {
            var original = p.Text;
            var (name, cut) = StripOf(original);
            if (name is null)
            {
                p.OriginStrip = null;
                p.OriginStripChars = 0;
                p.OriginStripWords = 0;
                kept.Add(p);
                continue;
            }
            var removed = original.Substring(0, cut);
            var remainder = original.Substring(cut);
            p.OriginStrip = name;
            p.OriginStripChars = PyText.Length(removed);          // Python's len: code points
            p.OriginStripWords = CountWords(removed);
            p.Text = remainder;
            p.Words = CountWords(remainder);
            if (p.Words == 0) Take(p, "framework", "framing-only:" + name);
            else kept.Add(p);
        }
        ordered = kept;

        // Pass 1: product envelopes, stamped or not. Stamped: "envelope-over-stamp:<name>", NO event claimed.
        // Unstamped: "envelope:<name>", the nearest unclaimed UNSTAMPED event claimed.
        var rest = new List<MentorRecord>();
        foreach (var p in ordered)
        {
            var name = EnvelopeOf(p.Text);
            if (name is null) { rest.Add(p); continue; }
            var origin = EnvelopeClass[name];
            if (Stamped(p))
            {
                Take(p, origin, "envelope-over-stamp:" + name);
            }
            else
            {
                Take(p, origin, "envelope:" + name);
                var evt = ledger.NearestUnclaimed(p.Session, p.Ts, "unstamped");
                if (evt is not null) evt.Claimed = true;
            }
        }

        // Pass 2: the agent tool's own framings except command-message, stamped or not: framework. Neither
        // claims an event. A command-message record is set aside for pass 4 whatever its stamp.
        var pending = new List<MentorRecord>();
        var commands = new List<MentorRecord>();
        foreach (var p in rest)
        {
            var name = AgentToolFramingOf(p.Text);
            if (name is null) pending.Add(p);
            else if (name == CommandMessageEntry) commands.Add(p);
            else if (Stamped(p)) Take(p, "framework", "agent-tool-over-stamp:" + name);
            else Take(p, "framework", "agent-tool:" + name);
        }

        // Pass 3: stamped modality is human, surface the record's own; the record claims its nearest
        // unclaimed event within tolerance so one keystroke event can never make two human prompts.
        rest = new List<MentorRecord>();
        foreach (var p in pending)
        {
            if (Stamped(p))
            {
                var surface = p.Surface is not null && Surfaces.Contains(p.Surface) ? p.Surface : "unknown";
                Take(p, "human", "stamped", p.Modality, surface);
                var evt = ledger.NearestUnclaimed(p.Session, p.Ts);
                if (evt is not null) evt.Claimed = true;
            }
            else
            {
                rest.Add(p);
            }
        }

        // Pass 4, command-message records first: a slash command the developer typed has a STAMPED
        // turn-submitted event; the nearest unclaimed one within tolerance makes the record human under
        // ledger-origin with the EVENT's modality and surface. No such event: the wrapper is the tool's.
        foreach (var p in commands)
        {
            var evt = ledger.NearestUnclaimed(p.Session, p.Ts, "stamped");
            if (evt is not null)
            {
                evt.Claimed = true;
                var (modality, surface) = evt.Origin!.Value;
                Take(p, "human", "ledger-origin", modality, surface);
            }
            else if (Stamped(p))
            {
                Take(p, "framework", "agent-tool-over-stamp:" + CommandMessageEntry);
            }
            else
            {
                Take(p, "framework", "agent-tool:" + CommandMessageEntry);
            }
        }

        // Pass 4, the rest: the nearest unclaimed event of any kind within tolerance decides.
        foreach (var p in rest)
        {
            var evt = ledger.NearestUnclaimed(p.Session, p.Ts);
            if (evt is null)
            {
                Take(p, "unresolved", "no-ledger-event");
                continue;
            }
            evt.Claimed = true;
            if (evt.Origin is not null)
            {
                var (modality, surface) = evt.Origin.Value;
                Take(p, "human", "ledger-origin", modality, surface);
            }
            else if (evt.Source == "Framework") Take(p, "framework", "ledger-framework");
            else if (evt.Source == "Agent") Take(p, "agent", "ledger-agent");
            else if (evt.Source == "Delivery")
                throw new MentorDataException("Prompt-log record " + p.Pid + " has no modality but its nearest turn-submitted "
                    + "event (" + evt.Where + ") is a Delivery with no InputOrigin: a data shape the "
                    + "design does not expect (a delivery carries a modality). Stop and report it.");
            else if (evt.Source == "UserInput") Take(p, "unresolved", "ledger-userinput-2639");
            else Take(p, "unresolved", "ledger-unstamped");
        }

        foreach (var p in prompts)
        {
            if (p.Role == "user") continue;
            Set(p, null, null);
            p.OriginStrip = null;
            p.OriginStripChars = 0;
            p.OriginStripWords = 0;
        }
        return counts;
    }

    /// <summary>{origin: count} from the (origin, rule) Counter, every class present, in class order.</summary>
    public static Dictionary<string, int> ClassCounts(Counts counts)
    {
        var output = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var origin in Classes) output[origin] = 0;
        foreach (var (key, n) in counts.Items) output[key.Origin] += n;
        return output;
    }

    /// <summary>One console line: counts per class, then per rule. Never any text.</summary>
    public static string SummaryLine(string label, Counts counts)
    {
        var byClass = ClassCounts(counts);
        var parts = Classes.Select(origin => origin + " " + byClass[origin]);
        var rules = counts.Items
            .OrderBy(kv => -kv.Value)
            .ThenBy(kv => kv.Key.Origin, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Rule, StringComparer.Ordinal)
            .Select(kv => kv.Key.Rule + "=" + kv.Value);
        return "origin " + label + ": " + string.Join(", ", parts) + " [" + string.Join(", ", rules) + "]";
    }
}
