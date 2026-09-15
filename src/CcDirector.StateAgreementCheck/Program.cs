using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// THE CROSS-SURFACE AGREEMENT CHECK, run against the LIVE fleet - the mission's proof
/// (specification section 6): "read the live fleet and assert that every session's desktop answer equals
/// its phone answer equals its Cockpit answer." It reported SIX disagreements out of THIRTEEN when it was
/// written. The comparison itself is <see cref="AgreementCheck"/>; this is the live harness around it.
///
/// Run it:  dotnet run --project src/CcDirector.StateAgreementCheck -- [repoRoot]
/// Exit code 0 = every check ran on every session and found nothing. 1 = real disagreements. 2 = the
/// harness could not run at all (never a zero). There is no 3: it meant "no disagreements AND I could not
/// grade everything", which had exactly one producer - the indeterminate row - and that is gone (gap 5).
/// See AgreementCheck.Summary.ExitCode for the arithmetic that makes it unreachable.
///
/// READ <see cref="AgreementCheck.ToDesktopInput"/> BEFORE QUOTING ANY NUMBER THIS PRINTS. It states what
/// this check can and cannot see, and why the obvious HTTP-only version of it would have been a
/// fabricated zero.
/// </summary>
public static class Program
{
    /// <summary>Seconds between the two reads. A session flipping Working &lt;-&gt; WaitingForInput between
    /// them is a FALSE disagreement, and one false positive discredits the whole check - so every
    /// disagreement is re-read before it is reported.</summary>
    private const int ReReadDelaySeconds = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var repoRoot = args.FirstOrDefault(a => !a.StartsWith('-')) ?? FindRepoRoot();
            Console.WriteLine("CROSS-SURFACE AGREEMENT CHECK - specification section 6");
            Console.WriteLine("Calls the REAL SessionOrdering fold and the REAL palettes. Re-implements nothing.");
            Console.WriteLine(new string('=', 78));

            var clientPalette = ClientPalette.Read(repoRoot);
            Console.WriteLine($"Client palette: {clientPalette.Count} names read from {ClientPalette.RelativePath}");

            // THE OWNERSHIP TREE, BEFORE ANYTHING TOUCHES THE NETWORK. It is a pure-function comparison
            // against the shared answers both languages are measured by, so it needs no fleet and no
            // token - and running it first means this tool still says something useful on a machine with
            // no Director configured, instead of throwing at ReadGatewayConfig with the tree unchecked.
            //
            // It is not folded into the per-session comparison below and could not be: that one compares
            // a stamped answer on the wire, and the Gateway does not stamp the tree (a later mission). The
            // tree's two implementations are held together by the file, not by the roster.
            var tree = TreeAgreement.Check(repoRoot);
            Console.WriteLine($"Ownership tree: {tree.Cases} case(s) over {tree.Sessions} session(s) from {TreeAgreement.RelativePath}");
            foreach (var f in tree.Findings) Console.WriteLine(f.ToString());
            Console.WriteLine(tree.Findings.Count == 0
                ? "  PASS - the C# fold answers exactly what the shared file says, on every case."
                : $"  FAIL ({tree.Findings.Count}) - the C# fold and the shared answers differ.");
            Console.WriteLine("  The TypeScript half of this agreement is asserted by tree.agreement.test.ts against the");
            Console.WriteLine("  SAME file, and is NOT run here - a green line above says nothing about the browser fold.");

            var (url, token) = ReadGatewayConfig();
            Console.WriteLine($"Gateway: {url}  (token read from config.json; never printed)");
            Console.WriteLine();

            var first = await ReadRosterAsync(url, token);
            Console.WriteLine($"Live fleet: {first.Count} session(s).");
            Console.WriteLine();

            var findings = AgreementCheck.Compare(first, clientPalette).ToList();

            // THE FINDINGS AND THE FLEET THEY ARE REPORTED AGAINST MUST BE ONE SNAPSHOT. This used to
            // confirm findings against a SECOND read and then print them beside the FIRST read's row
            // table, exposure count and graded denominator. Sessions come and go between two reads of a
            // live fleet - one of them gains a dictation, one exits - so the report could pair
            // second-snapshot findings with a first-snapshot denominator and call the result measured.
            // Internally false output, produced by the path whose whole job is to avoid false positives.
            // Found by inspection of pull request 1606.
            var reportRoster = first;

            if (findings.Count > 0)
            {
                // "finding(s)", not "disagreement(s)": an indeterminate row is a finding and is NOT a
                // disagreement, and this line runs before anything has worked out which is which.
                Console.WriteLine($"{findings.Count} candidate finding(s) - re-reading in {ReReadDelaySeconds}s to " +
                                  "rule out a session that changed state between reads...");
                await Task.Delay(TimeSpan.FromSeconds(ReReadDelaySeconds));
                var second = await ReadRosterAsync(url, token);
                var confirmed = AgreementCheck.Compare(second, clientPalette)
                    .Where(f => findings.Any(x => x.SessionId == f.SessionId && x.Kind == f.Kind))
                    .ToList();
                var transient = findings.Count - confirmed.Count;
                if (transient > 0)
                    Console.WriteLine($"  {transient} did not survive the re-read (a racing state change) - NOT reported.");
                findings = confirmed;
                // The confirmed findings came from `second`, so `second` is the fleet the report describes.
                reportRoster = second;
                if (second.Count != first.Count)
                    Console.WriteLine($"  The fleet changed between reads ({first.Count} -> {second.Count} session(s)); " +
                                      "everything below describes the SECOND read, which is where these findings were confirmed.");
            }

            Report(reportRoster, findings);
            // The exit decision is AgreementCheck.Summary.ExitCode - bound, tested, and the only place
            // that knows an indeterminate finding is not a disagreement. This used to be
            // `findings.Count == 0 ? 0 : 1` right here, which returned "disagreements" for a row the
            // check had merely been unable to read.
            var exitCode = AgreementCheck.Summarize(reportRoster, findings).ExitCode;
            // A tree disagreement is a real disagreement and must fail the run. It is ORed in here rather
            // than folded into Summarize because Summarize counts findings per LIVE SESSION, and the tree
            // check has no live session behind it - its rows are fixtures. Reporting fixture findings under
            // a per-session denominator would be the "narrow number under the broad name" defect this file
            // has already paid for twice.
            return tree.Findings.Count > 0 ? 1 : exitCode;
        }
        catch (Exception ex)
        {
            // Fail loudly. A check that swallows its own error and prints zero is the worst outcome
            // available here - it is a fabricated proof.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"AGREEMENT CHECK FAILED TO RUN: {ex.Message}");
            Console.Error.WriteLine("No number is reported. A check that cannot run reports NOTHING, never zero.");
            return 2;
        }
    }

    private static void Report(IReadOnlyList<SessionDto> roster, IReadOnlyList<AgreementCheck.Finding> findings)
    {
        Console.WriteLine(new string('-', 78));
        foreach (var row in roster)
            Console.WriteLine($"  {row.EffectiveColor,-11} {row.StateLabel,-18} {row.SessionRole,-11} " +
                              $"{StatusPalette.HexFor(row.EffectiveColor)}  {row.Name}");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine();

        // THE EXPOSURE COUNT. How many live sessions carry a Gateway-only fold input right now. A zero
        // measured over a fleet where none of them is in play has NOT exercised those arms, and saying so
        // is the difference between a proof and a green light. The arms themselves are exercised
        // deliberately, and watched failing, by AgreementCheckFaultInjectionTests.
        var exposed = roster.Count(r => r.DictationStatus is not null || r.Transcribing || r.VoiceGenerating || r.SnoozeExpired);
        Console.WriteLine($"Sessions carrying a Gateway-only fold input right now: {exposed} of {roster.Count}.");
        if (exposed == 0)
        {
            Console.WriteLine("  So the four inputs the desktop cannot see are NOT in play on this fleet at this instant,");
            Console.WriteLine("  and this run therefore does NOT exercise those arms. See the fault-injection tests.");
        }
        else
        {
            // This branch did not exist, and the sentence above printed unconditionally: the tool said
            // "2 of 16" and then told the reader the run had not exercised those arms. False output from
            // the instrument whose whole job is to be quotable. Found by inspection of pull request 1606,
            // which ran it live and read what it printed rather than what it meant.
            Console.WriteLine($"  So this run DID exercise those arms on {exposed} session(s) - they are live, not hypothetical.");
            Console.WriteLine("  The fault-injection tests exercise them deliberately and are watched failing.");
        }
        Console.WriteLine();

        foreach (var f in findings)
            // f.Label, never a literal. This was the word "DISAGREEMENT" hard-coded here, so a row the
            // check merely could not READ printed as "DISAGREEMENT [indeterminate]" - four lines above a
            // headline correctly reporting zero disagreements. The fifth consumer to decide for itself
            // what a kind string meant, and the last one caught. It asks now.
            Console.WriteLine($"{f.Label} [{f.Kind}] {f.Name}\n    {f.Detail}\n");

        // The arithmetic lives in AgreementCheck.Summarize so a test can reach it - see that method for
        // why, and for the two ways this counting was wrong before it was bindable.
        var sum = AgreementCheck.Summarize(roster, findings);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"AGREEMENT NUMBER: {sum.Disagreements} disagreement(s) over {sum.LiveSessions} live session(s).");
        // THE SCOPE OF A CHECK COMES FROM THE CHECK, NEVER FROM A CAUSE COUNT SITTING NEXT TO IT.
        // This line used to print Summary.DesktopNotGraded - which counts only INDETERMINATE rows - while
        // saying "the desktop comparison was not graded on N". An unstamped row stops that comparison too,
        // so on a fleet with one of each it announced "not graded on 1 row" where the truth was two, and
        // added that every other check ran on them, which was true of one row and false of the other. The
        // check-result table below was right the whole time; this sentence was reading the narrow number
        // under the broad name. Found by the eleventh inspection pass of pull request 1606.
        foreach (var line in sum.DesktopNotGradedLines())
            Console.WriteLine(line);
        Console.WriteLine("  (It was SIX out of THIRTEEN when the specification was written.)");
        Console.WriteLine();
        // EVERY VERDICT HERE IS READ FROM THE FINDINGS, NEVER ASSERTED OVER THEM.
        //
        // This paragraph used to be a fixed block of prose claiming all five checks passed - printed
        // AFTER the findings, unconditionally. So a run could list a broken law in detail and then tell
        // the reader, four lines later, that the law holds over every live session. The arithmetic above
        // it had already been made bindable and testable; the prose had not, so it stayed wrong in
        // exactly the way the numbers had been. Found by the eighth inspection pass of pull request 1606,
        // one level out from the seventh.
        //
        // Now each line is derived from a counted kind, and PASS is the one thing that cannot be said
        // without evidence: it means "this check found nothing", and nothing else.
        Console.WriteLine($"CHECK RESULTS, over {sum.LiveSessions} live session(s):");
        foreach (var check in sum.AllChecks)
            Console.WriteLine($"  {check.Name.PadRight(44, '.')} {check.Line}");
        if (sum.AllChecks.Any(c => c.NotGraded > 0))
        {
            Console.WriteLine();
            Console.WriteLine("  A lower-case 'pass on N of M' is NOT a pass over the fleet - it means the check ran on N");
            Console.WriteLine("  rows and found nothing, and never reached the rest. An unstamped row stops every check");
            Console.WriteLine("  after it: there is no stamped answer to compare. Absence of a finding from a check that");
            Console.WriteLine("  did not run is not evidence.");
        }
        Console.WriteLine("NOT MEASURED, and not to be claimed: the desktop's own SessionRole copy is not externally");
        Console.WriteLine("  observable (the Gateway's fleet pass overwrites the inbound role on every read), so a");
        Console.WriteLine("  STALE role on a Director cannot be seen from here. That push is proved in-process by");
        Console.WriteLine("  DesktopRoleStampWireProofTests, which hand-sets nothing.");
    }

    private static async Task<IReadOnlyList<SessionDto>> ReadRosterAsync(string url, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await http.GetStringAsync($"{url.TrimEnd('/')}/sessions");
        return JsonSerializer.Deserialize<List<SessionDto>>(body, Json)
               ?? throw new InvalidOperationException("The Gateway roster did not deserialize to a session list.");
    }

    /// <summary>
    /// The Gateway's address and device token, from the Director's config. The token is NEVER printed.
    ///
    /// The path comes from <c>CcStorage.Config()</c> and is NOT composed here. This file originally hand-built
    /// GetFolderPath(LocalApplicationData) + "cc-director" + "config", and StorageRootGuardTests failed it
    /// within the hour - correctly. A hand-composed root cannot be redirected by CC_DIRECTOR_ROOT, so anything
    /// reaching it reads and writes the REAL running Director's folders no matter how careful the caller is.
    /// Worth recording that the guard caught the agent auditing everyone else's fitness functions, in the same
    /// pass: care is not the control, the test is - which is this mission's whole thesis, applied to its own QA.
    /// </summary>
    private static (string Url, string Token) ReadGatewayConfig()
    {
        var path = Path.Combine(CcDirector.Core.Storage.CcStorage.Config(), "config.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"No Director config at '{path}', so the Gateway cannot be reached.", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("gateway", out var gw))
            throw new InvalidOperationException("config.json has no 'gateway' section.");
        var url = gw.TryGetProperty("url", out var u) ? u.GetString() : null;
        var token = gw.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "config.json's gateway section has no url or no token. Gateway endpoints are device-key " +
                "authenticated; without a token this check cannot read the live fleet, and it will not " +
                "pretend to by measuring something else.");
        return (url, token);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no 'packages' directory above this binary). " +
                   "Pass it as the first argument.");
    }
}
