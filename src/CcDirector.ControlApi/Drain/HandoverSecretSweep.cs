using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.ControlApi.Drain;

/// <summary>One pattern the sweep looks for, with the known-bad string that proves it still fires.</summary>
/// <param name="Name">What the pattern is called in a finding. Never the secret.</param>
/// <param name="Pattern">The regular expression.</param>
/// <param name="Control">A FAKE line this pattern must match. It is the instrument's own test weight, and
/// it lives beside the pattern so the two can never drift apart.</param>
public sealed record SecretPattern(string Name, Regex Pattern, string Control);

/// <summary>One thing the sweep found, said WITHOUT saying the secret.</summary>
/// <param name="File">The document it is in.</param>
/// <param name="Line">The 1-based line number.</param>
/// <param name="PatternName">Which pattern fired.</param>
/// <param name="RedactedExcerpt">The line with the matched region replaced. A finding is read by whoever
/// is deciding what to do about it, and a report that quotes the secret has copied it somewhere new.</param>
public sealed record SecretFinding(string File, int Line, string PatternName, string RedactedExcerpt);

/// <summary>
/// The result of proving the sweep works: which patterns fired on their own control, and whether the
/// clean control stayed clean.
/// </summary>
/// <param name="PatternsProved">How many patterns fired on their own known-bad control.</param>
/// <param name="PatternsTotal">How many patterns there are.</param>
/// <param name="Failures">The patterns that did NOT fire, or that fired on the clean control. Empty on a
/// valid instrument.</param>
public sealed record SweepProof(int PatternsProved, int PatternsTotal, IReadOnlyList<string> Failures)
{
    /// <summary>True only when every pattern fired on its own control and none fired on clean prose.</summary>
    public bool Valid => Failures.Count == 0 && PatternsProved == PatternsTotal && PatternsTotal > 0;
}

/// <summary>
/// Sweeps handover documents for secrets before a restart.
///
/// A CLEAN SWEEP MEANS NOTHING UNTIL THE SWEEP HAS BEEN PROVED ABLE TO FAIL. A zero from a broken
/// instrument reads exactly like a zero from clean documents, and only one of those is good news. This is
/// not a hypothetical: the first handover ever produced this way carried a virtual machine's
/// administrator password, and "no secrets" had been in the instruction that produced it.
///
/// So the proof is not advice in a document - it is enforced here. <see cref="Sweep"/> throws unless
/// <see cref="Prove"/> has returned a valid result in this process, and <see cref="Prove"/> checks every
/// pattern individually against its own known-bad control. Proving them collectively would let one dead
/// pattern hide behind nine live ones, which is the same fail-open shape one level down.
///
/// It also checks that no pattern fires on clean prose, because a pattern that matches everything passes
/// a fire-on-bad-input test and then buries the real findings in noise.
/// </summary>
public static class HandoverSecretSweep
{
    private static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Ordinary handover prose that must produce NO hits. It deliberately contains the WORDS a careless
    /// pattern would fire on - password, token, key, secret - without any assigned value, because that is
    /// exactly what a real handover says when it is being careful.
    /// </summary>
    public const string CleanControl =
        "The Gateway session key is read from the environment, never written down. No passwords, tokens " +
        "or keys are in this document. The credentials file is named in the repository rules; the secret " +
        "itself stays there. Authorization is by device key.";

    /// <summary>
    /// Every pattern, each carrying its own fake known-bad control line.
    ///
    /// The controls are invented strings in the shapes real credentials take. None of them is a real
    /// credential and none of them has ever been one.
    /// </summary>
    public static IReadOnlyList<SecretPattern> Patterns { get; } = new List<SecretPattern>
    {
        new("assigned-password",
            new Regex(@"\b(?:password|passwd|pwd)\b\s*[:=]\s*(?<v>\S{6,})", Opts),
            "password: NotARealOne123!"),

        new("assigned-token-or-key",
            new Regex(@"\b(?:api[_-]?key|apikey|access[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|secret[_-]?key)\b\s*[:=]\s*(?<v>\S{8,})", Opts),
            "api_key = abcd1234efgh5678ijkl"),

        new("bearer-header",
            new Regex(@"\bauthorization\s*:\s*bearer\s+(?<v>\S{8,})", Opts),
            "Authorization: Bearer abcdefghijklmnop0123"),

        new("private-key-block",
            new Regex(@"-----BEGIN(?<v>[A-Z ]*)PRIVATE KEY-----", RegexOptions.CultureInvariant),
            "-----BEGIN RSA PRIVATE KEY-----"),

        new("aws-access-key-id",
            new Regex(@"\b(?<v>(?:AKIA|ASIA)[0-9A-Z]{16})\b", RegexOptions.CultureInvariant),
            "AKIAQQQQWWWWEEEERRRR"),

        new("github-token",
            new Regex(@"\b(?<v>gh[pousr]_[A-Za-z0-9]{20,})\b", RegexOptions.CultureInvariant),
            "ghp_aaaabbbbccccddddeeeeffffgggghhhh"),

        new("slack-token",
            new Regex(@"\b(?<v>xox[abpsr]-[A-Za-z0-9-]{12,})\b", RegexOptions.CultureInvariant),
            "xoxb-1111111111-2222222222-abcdefghijkl"),

        new("openai-style-key",
            new Regex(@"\b(?<v>sk-[A-Za-z0-9_-]{20,})\b", RegexOptions.CultureInvariant),
            "sk-aaaabbbbccccddddeeeeffffgggg"),

        new("json-web-token",
            new Regex(@"\b(?<v>eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b", RegexOptions.CultureInvariant),
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NX0.aaaabbbbccccdddd"),

        new("connection-string-password",
            new Regex(@"\b(?:pwd|password)\s*=\s*(?<v>[^;\s]{4,})\s*;", Opts),
            "Server=db;User Id=sa;Password=Zzzz9999;"),
    };

    /// <summary>
    /// Run the instrument against its own known-bad controls, and against clean prose. Call this before
    /// believing any clean result; <see cref="Sweep"/> refuses to run until it has passed.
    /// </summary>
    public static SweepProof Prove() => Prove(Patterns, CleanControl);

    /// <summary>
    /// The proof, over any pattern set. THIS OVERLOAD IS THE ONLY WAY THE CHECKER ITSELF CAN BE RUN
    /// AGAINST A KNOWN-BAD INPUT: a test hands it a deliberately dead pattern, and a deliberately greedy
    /// one, and watches the proof report each. Without it, "the proof passes" would be a claim about an
    /// instrument nobody had ever seen fail, which is the exact shape it exists to forbid.
    /// </summary>
    /// <param name="patterns">The patterns to prove.</param>
    /// <param name="cleanControl">Prose that must produce no hits.</param>
    internal static SweepProof Prove(IReadOnlyList<SecretPattern> patterns, string cleanControl)
    {
        var failures = new List<string>();
        var proved = 0;

        foreach (var p in patterns)
        {
            if (p.Pattern.IsMatch(p.Control)) proved++;
            else failures.Add($"pattern '{p.Name}' did not fire on its own known-bad control");

            if (p.Pattern.IsMatch(cleanControl))
                failures.Add($"pattern '{p.Name}' fired on clean prose, so it cannot separate a secret from a mention of one");
        }

        return new SweepProof(proved, patterns.Count, failures);
    }

    /// <summary>
    /// Sweep one document's text.
    /// </summary>
    /// <param name="file">The document's path, recorded on every finding.</param>
    /// <param name="text">The whole document.</param>
    /// <exception cref="InvalidOperationException">The instrument failed its own proof. Nothing is
    /// reported clean by a sweep that has not been shown able to fail.</exception>
    public static IReadOnlyList<SecretFinding> Sweep(string file, string? text)
        => Sweep(file, text, Patterns, CleanControl);

    /// <summary>The sweep over any pattern set, so a test can watch it REFUSE on a broken instrument.</summary>
    /// <param name="file">The document's path.</param>
    /// <param name="text">The whole document.</param>
    /// <param name="patterns">The patterns to sweep with.</param>
    /// <param name="cleanControl">Prose that must produce no hits.</param>
    internal static IReadOnlyList<SecretFinding> Sweep(
        string file, string? text, IReadOnlyList<SecretPattern> patterns, string cleanControl)
    {
        var proof = Prove(patterns, cleanControl);
        if (!proof.Valid)
            throw new InvalidOperationException(
                "The secret sweep failed its own proof and will not report a result: " +
                string.Join("; ", proof.Failures) +
                ". Fix the patterns - a zero from a broken sweep reads exactly like a clean document.");

        var findings = new List<SecretFinding>();
        if (string.IsNullOrEmpty(text)) return findings;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Redact the line ONCE, against EVERY pattern, before any finding quotes any of it. Redacting
            // only the match that fired would leave the context either side of it in the clear - and on a
            // line carrying two credentials ("password: ... api_key=...") each finding's context would
            // then publish the OTHER one, verbatim, into a record stored off this machine.
            var redacted = Redact(lines[i], patterns);

            foreach (var p in patterns)
            {
                if (!p.Pattern.IsMatch(lines[i])) continue;
                findings.Add(new SecretFinding(file, i + 1, p.Name, redacted));
            }
        }
        return findings;
    }

    /// <summary>
    /// Redact one piece of text with the SHIPPED patterns, for anything that is stored somewhere rather
    /// than reported as a finding.
    ///
    /// The drain copies a seat's own prose onto a record that leaves this machine - a blocked reason, a
    /// restore why, an owner question - and those went through no redaction at all while the sweep
    /// carefully redacted its own excerpts. This is the same instrument, applied at the other door.
    /// </summary>
    /// <param name="text">The text to store.</param>
    /// <exception cref="InvalidOperationException">The instrument failed its own proof.</exception>
    public static string RedactLine(string text)
    {
        var proof = Prove();
        if (!proof.Valid)
            throw new InvalidOperationException(
                "The secret sweep failed its own proof and will not redact: " +
                string.Join("; ", proof.Failures));
        return Redact(text ?? "", Patterns);
    }

    /// <summary>
    /// Replace EVERY matched region on the line with a marker, so what is left is the surrounding words
    /// and nothing that any pattern recognised as a credential.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="patterns">Every pattern - not just the one that fired.</param>
    private static string Redact(string line, IReadOnlyList<SecretPattern> patterns)
    {
        const int MaxLine = 300;

        // Collect every span to hide, from every pattern.
        var spans = new List<(int Start, int End)>();
        foreach (var p in patterns)
            foreach (Match m in p.Pattern.Matches(line))
            {
                var g = m.Groups["v"].Success ? m.Groups["v"] : (Group)m;
                if (g.Length > 0) spans.Add((g.Index, g.Index + g.Length));
            }

        // MERGE OVERLAPPING SPANS FIRST. Two patterns can match regions that overlap - "password:
        // SECRET,ghp_aaaa..." matches both the password and the token, and the password match swallows
        // the token. Editing them independently, right to left, replaced the inner one first, which
        // SHORTENED the string and pushed the outer one past the end of it: its bounds check then failed,
        // it was skipped, and the first secret survived into every finding. Merging first means every
        // edit is over a region that is still there.
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                if (span.End > merged[^1].End) merged[^1] = (merged[^1].Start, span.End);
            }
            else
            {
                merged.Add(span);
            }
        }

        // Right to left, so the earlier indexes stay valid.
        var sb = new StringBuilder(line);
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var (start, end) = merged[i];
            var length = end - start;
            if (start < 0 || end > sb.Length) continue;
            sb.Remove(start, length);
            sb.Insert(start, $"[REDACTED {length} chars]");
        }

        var result = sb.ToString();
        return result.Length <= MaxLine ? result : result[..MaxLine] + "...";
    }
}
