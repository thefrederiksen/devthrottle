using System.Text.Json;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// THE COMPARISON - the mission's proof, in one testable place (specification section 6).
///
/// It is a library rather than a lump inside Main for one reason: <em>a check nobody has watched fail is
/// worth nothing.</em> This mission's own law is that a green test proves nothing until you have seen it
/// go red with the reported symptom, and the same standard has to apply to the thing doing the proving.
/// So the comparison is callable with a hand-made roster, and
/// <c>AgreementCheckFaultInjectionTests</c> feeds it the SIX ORIGINAL DISAGREEING SESSIONS and watches it
/// report them.
///
/// THE ONE RULE: it calls the REAL fold (<see cref="SessionOrdering"/>) and the REAL palettes
/// (<see cref="StatusPalette"/> for the rail, the parsed shipping table for the clients). It
/// re-implements NOTHING. A check that re-derives the ladder becomes fold number fifteen - internally
/// consistent, consistent with nothing else - which is the exact sin this mission exists to end,
/// committed by the tool built to prove the sin is gone.
/// </summary>
public static class AgreementCheck
{
    /// <summary>One disagreement, named by what it is rather than by a code.</summary>
    /// <param name="Kind">
    /// <c>unstamped</c> - the Gateway sent no answer (defect 15); <c>stamp-not-fold</c> - the stamped
    /// answer is not the shared fold's (defects 6 and 7); <c>law-broken</c> - a working session is not
    /// blue; <c>desktop-vs-gateway</c> - the rail's fold and the Gateway's answer differ (defect 5's
    /// family); <c>two-different-pixels</c> - both surfaces fold to the same NAME and paint different
    /// hexes (defect 18 - invisible to a check that compares the fold's answer);
    /// <c>palette-missing</c> - a surface cannot paint the colour it was given.
    ///
    /// THERE IS NO <c>indeterminate</c> KIND ANY MORE (gap 5). It meant "the row does not carry enough to
    /// judge", and it existed for exactly one shape - a Gateway that had overwritten the Director's
    /// BriefingState, destroying the fact the desktop comparison needs. The Gateway no longer writes that
    /// field, so every row is gradeable and every finding is a real disagreement. See the tombstone above
    /// ToDesktopInput before reintroducing either.
    /// </param>
    public sealed record Finding(string? SessionId, string Name, string Kind, string Detail)
    {
        /// <summary>
        /// The word this finding is printed under. One home, so no renderer can invent its own.
        ///
        /// IT IS A CONSTANT NOW, AND THAT IS THE POINT, NOT AN OVERSIGHT. This used to ask a
        /// FindingOutcome enum whether the finding was a Disagreement or NotGraded, because there were two
        /// kinds of answer. There is one: with the indeterminate kind gone, NotGraded had no producer, so
        /// the enum had one member and Outcome could only ever return it. An enum that cannot vary is not a
        /// classification, it is ceremony.
        ///
        /// The property stays even though the value cannot, because the DEFECT it was born from was a
        /// renderer deciding for itself: "DISAGREEMENT" was hard-coded in Program.cs, so a row the check
        /// had refused to grade printed as a disagreement. Keeping the word in one place costs a line and
        /// keeps the renderer asking. If a second kind of outcome ever returns, this is where it goes.
        /// </summary>
        public string Label => "DISAGREEMENT";
    }

    /// <summary>
    /// One check's verdict, and the ONLY honest way to state it: what it found, and how many rows it
    /// never got to look at.
    ///
    /// "FOUND NOTHING" AND "PASSED" ARE DIFFERENT CLAIMS, and collapsing them is the defect this type
    /// exists to make impossible. An unstamped row is terminal - Compare cannot compare a stamp that is
    /// not there, so it stops and the fold, law, desktop and palette checks never run on that row. The
    /// first version of this reported those four as PASS on such a fleet, because no finding came back
    /// from checks that had not executed. Absence of evidence, printed as evidence.
    ///
    /// So a verdict carries <see cref="NotGraded"/> and cannot claim the fleet without it.
    /// </summary>
    public sealed record CheckVerdict(string Name, int Failures, int NotGraded, int LiveSessions)
    {
        /// <summary>Rows this check actually ran on.</summary>
        public int Graded => LiveSessions - NotGraded;

        /// <summary>Found nothing WHERE IT RAN. Not the same as "holds over the fleet" - see Line.</summary>
        public bool Passed => Failures == 0;

        /// <summary>Found nothing AND ran on every live session. The only basis for an unqualified pass.</summary>
        public bool PassedEverywhere => Failures == 0 && NotGraded == 0;

        /// <summary>
        /// The verdict as printed. It states BOTH halves, always: what the check found, and what it never
        /// looked at. Neither half may be dropped because the other is interesting.
        ///
        /// The first version returned early on <c>Failures &gt; 0</c> and silently discarded
        /// <see cref="NotGraded"/>. So a check that failed on one row AND never ran on another printed a
        /// bare "FAIL (1)" beneath a header reading "over 2 live session(s)" - a complete-looking verdict
        /// over a fleet it had only half examined. Not the bare PASS of the previous bug; the same
        /// claim-without-evidence one step sideways, and it survived because the tests covered
        /// failure-only and not-graded-only and never both at once.
        ///
        /// The lesson that finally stuck, on the tenth pass: A PARTIAL TRUTH IS THE FAILURE MODE, not an
        /// acceptable summary of a complicated one. Every version of this bug has been some true half
        /// printed without its qualifier.
        /// </summary>
        public string Line =>
            (Failures, NotGraded) switch
            {
                (0, 0) => "PASS",
                (0, _) => $"pass on {Graded} of {LiveSessions} - NOT GRADED on {NotGraded}",
                (_, 0) => $"FAIL ({Failures})",
                _ => $"FAIL ({Failures}) on {Graded} of {LiveSessions} - NOT GRADED on {NotGraded}",
            };
    }

    /// <summary>
    /// The numbers the run publishes: how many real disagreements, over how many sessions, and each
    /// check's verdict - including the rows it never reached, so the prose reporting them cannot claim a
    /// check passed where it did not run.
    /// </summary>
    /// <remarks>
    /// IndeterminateRows IS GONE (gap 5). It counted rows the Gateway's BriefingState overwrite had made
    /// unreadable; the overwrite is deleted, so the count has no producer. Its hard-won lesson survives it
    /// and still binds every number in this record:
    ///
    /// PROSE IS RENDERED FROM <see cref="AllChecks"/> AND NOTHING ELSE. A cause count is an input to a
    /// verdict, never a thing to narrate from. IndeterminateRows was a CAUSE, while
    /// <c>DesktopAgreed.NotGraded</c> is a check's SCOPE - the two sat side by side under names close
    /// enough to swap, and the prose duly read the narrow one under the broad one's meaning, announcing
    /// "not graded on 1 row" on a fleet where it had not been graded on two. That was the eleventh finding
    /// on pull request 1606. <see cref="Unstamped"/> is a cause of exactly the same kind: do not narrate
    /// from it either.
    /// </remarks>
    public sealed record Summary(
        int Disagreements,
        int LiveSessions,
        int Unstamped,
        int StampNotFold,
        int LawBroken,
        int DesktopVsGateway,
        int TwoDifferentPixels,
        int PaletteMissing)
    {
        /// <summary>Check 1. It runs on every row - nothing can stop it, so it is never not-graded.</summary>
        public CheckVerdict StampPresent => new("the stamp is present", Unstamped, 0, LiveSessions);

        // Checks 2, 3 and 5 run on every row EXCEPT the ones check 1 stopped: an unstamped row has no
        // stamped answer to compare a fold against, no colour to test the law on, and no colour to look
        // up in a palette. They are not-graded there, never passed.
        public CheckVerdict StampIsFold => new("the stamped answer IS the shared fold's", StampNotFold, Unstamped, LiveSessions);
        public CheckVerdict Law => new("the LAW: working => blue", LawBroken, Unstamped, LiveSessions);
        public CheckVerdict SamePixels => new("every colour is the SAME HEX on both palettes",
            TwoDifferentPixels + PaletteMissing, Unstamped, LiveSessions);

        // Check 4 is stopped by BOTH: an unstamped row (nothing to compare) and an indeterminate one (the
        // Gateway destroyed the fact it needs). The only check with two ways to be ungradeable.
        public CheckVerdict DesktopAgreed => new("the desktop's fold == the Gateway's",
            DesktopVsGateway, Unstamped, LiveSessions);

        public IReadOnlyList<CheckVerdict> AllChecks => new[] { StampPresent, StampIsFold, Law, SamePixels, DesktopAgreed };

        /// <summary>
        /// The desktop check's not-graded explanation, as the exact lines the report prints - empty when
        /// it ran on everything.
        /// </summary>
        /// <remarks>
        /// THIS IS A FUNCTION BECAUSE THE PROSE KEPT BEING WRONG, and it kept being wrong because it was
        /// Console.WriteLine and no test could reach it. Four separate defects on this pull request lived
        /// in printed sentences: a paragraph claiming every check passed, a line claiming the desktop
        /// agreed while a row was unread, a verdict dropping its own not-graded half, and a headline that
        /// said "for two different reasons" while listing one. Each time the numbers underneath were
        /// right. Each time the fix was to move the claim somewhere a test could hold it.
        ///
        /// So the sentence is derived from the causes actually present, never asserted over them: two
        /// causes get "for 2 different reasons", one cause gets no such clause at all, because on that run
        /// there is ONE reason drawn from a set of two - and saying otherwise is the same overstatement
        /// this instrument exists to catch, committed by the instrument, in its own summary, on the
        /// twelfth pass.
        /// </remarks>
        /// <summary>
        /// THE MACHINE-READABLE VERDICT, and the one that matters most, because a caller reading an exit
        /// code cannot read the caveat underneath it.
        ///
        ///   0 - every check ran on every session and found nothing. The only clean answer.
        ///   1 - real disagreements were found.
        ///
        /// (2 is the harness's own failure - it could not run at all - and is raised by Main, not here.)
        ///
        /// EXIT 3 IS DELETED (gap 5). It meant "NO disagreements, and the check could not grade
        /// everything" - the third answer for a fleet the instrument had not fully read. It existed for
        /// exactly one producer: the indeterminate row. That is now impossible, and the proof is
        /// arithmetic rather than opinion:
        ///
        ///   * exit 3 required Disagreements == 0 AND some check reporting NotGraded > 0;
        ///   * NotGraded is now fed by <see cref="Unstamped"/> alone (see the AllChecks verdicts);
        ///   * an unstamped row ALWAYS yields an "unstamped" finding, and every finding is a
        ///     disagreement now;
        ///   * so NotGraded > 0 implies Disagreements > 0, which returns 1 before 3 can be reached.
        ///
        /// Unreachable code in a machine interface is worse than unreachable code anywhere else: a caller
        /// writing `if code == 3` is writing a branch that will never run, against a contract that says it
        /// might. So the code goes and the contract shrinks to what is true.
        ///
        /// THE LESSON THAT PUT IT HERE STILL STANDS, and it is why 1 and 0 are computed rather than
        /// assumed. Main used to return 1 whenever ANY finding existed, while the contract said 1 means
        /// disagreements and the Summary correctly said an indeterminate row was not one - so an
        /// indeterminate-only run printed "0 disagreement(s)" and exited 1. The report table had learned
        /// the distinction; the process contract had not, and a script does not read the table. That was
        /// the thirteenth instance of one defect and the first in a machine interface: everything before
        /// it mis-stated a claim to a human who could read the next line down. If a not-graded-without-a-
        /// disagreement producer ever returns, the third code returns with it - and this is its argument.
        /// </summary>
        public int ExitCode => Disagreements > 0 ? 1 : 0;

        public IReadOnlyList<string> DesktopNotGradedLines()
        {
            if (DesktopAgreed.NotGraded == 0) return Array.Empty<string>();

            var causes = new List<string>();
            // One cause left: [indeterminate] went with the kind that produced it (gap 5). So the plural
            // arithmetic below can no longer fire - one cause cannot be two.
            //
            // WHY THAT STAYS WHEN EXIT 3 WENT, because the asymmetry is deliberate and an inspector should
            // not have to guess at it. Exit 3 was a documented CONTRACT promising a value the tool can
            // never return - a false statement to a machine that cannot read the caveat, and a caller
            // branching on it writes dead code against our promise. This is a RENDERER that is general
            // enough to take a second cause: it claims nothing, it just does not need its generality
            // today. Unreachable-and-lying goes; unreachable-and-honest is a judgement, and the judgement
            // here is that "here are the reasons" is the right shape for this sentence - the twelfth
            // finding was this prose hard-coding "for two different reasons", and collapsing it back to
            // one hard-coded sentence is that defect's return path.
            if (Unstamped > 0)
                causes.Add($"{Unstamped} [unstamped] - no answer arrived, so NOTHING downstream could be checked on them.");

            // Derived, never assumed. One cause is one reason; only more than one earns the plural.
            var why = causes.Count > 1 ? $", for {causes.Count} different reasons" : "";
            var lines = new List<string>
            {
                $"DESKTOP COMPARISON NOT GRADED on {DesktopAgreed.NotGraded} of {LiveSessions} row(s){why} - see the rows above:",
            };
            lines.AddRange(causes.Select(c => "  " + c));
            return lines;
        }
    }

    /// <summary>
    /// THE ARITHMETIC OF THE HEADLINE NUMBER, in a function, because it is the sentence people quote and
    /// it had been wrong twice - both times found by a reader reasoning about console output, which is
    /// the least reliable way to find anything.
    ///
    /// This mission's own lesson, applied to itself for the third time: an unbindable seam is an
    /// untestable one. The counting lived inline in Program.Report, so no test could reach it, so it
    /// drifted out of step with Compare twice without a single test going red. Now it is bindable and
    /// AgreementSummaryTests pins it.
    ///
    /// THE RULE IT ENCODES: every live session IS checked. Only ONE of the five checks - the desktop
    /// comparison - can be defeated by an indeterminate row, and only on that row; the stamp, fold, LAW
    /// and palette checks run on everything and stand. So the denominator is every live session, and an
    /// unreadable row's CERTAIN findings are counted like anyone else's.
    ///
    /// The version before this subtracted indeterminate rows from the denominator, counted their certain
    /// findings in the numerator anyway, and then printed "the number above says nothing about them" -
    /// three statements that cannot all be true at once. It was introduced by the fix that let an
    /// unreadable row keep reporting its certain defects, which is the honest behaviour; the arithmetic
    /// simply did not follow it there. Found by inspection of pull request 1606.
    /// </summary>
    public static Summary Summarize(IReadOnlyList<SessionDto> roster, IReadOnlyList<Finding> findings)
    {
        int Count(string kind) => findings.Count(f => f.Kind == kind);
        // EVERY finding is a disagreement now (gap 5 deleted the indeterminate kind), so this is a plain
        // count rather than a classification. It used to ask each finding what it WAS, because the answer
        // could be "not graded" - and before that it was `findings.Count - indeterminate`, the summary
        // re-deriving the classification from the kind string, one of five consumers doing that separately,
        // which is what made them drift apart one pass at a time. If a second outcome ever returns, it
        // returns HERE and on Finding - not in five places again.
        return new Summary(
            Disagreements: findings.Count,
            LiveSessions: roster.Count,
            Unstamped: Count("unstamped"),
            StampNotFold: Count("stamp-not-fold"),
            LawBroken: Count("law-broken"),
            DesktopVsGateway: Count("desktop-vs-gateway"),
            TwoDifferentPixels: Count("two-different-pixels"),
            PaletteMissing: Count("palette-missing"));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Compare every surface's answer for every session in a roster. <paramref name="clientPalette"/> is
    /// the table the phone and the Cockpit actually ship, read by <see cref="ClientPalette.Read"/> -
    /// never a copy typed here.
    /// </summary>
    public static IEnumerable<Finding> Compare(
        IReadOnlyList<SessionDto> roster, IReadOnlyDictionary<string, string> clientPalette)
    {
        foreach (var row in roster)
        {
            var name = row.Name ?? row.SessionId ?? "(unnamed)";

            // ---- 1. THE STAMP IS PRESENT. Defect 15: GET /sessions/{sid} used to return these null while
            // the DTO documents them "Required on Gateway /sessions responses". A client that cannot get a
            // stamped answer fails loudly (magenta on the rail, a blank page in the Cockpit), so a null
            // stamp is a rendered defect, not a cosmetic one.
            if (string.IsNullOrEmpty(row.EffectiveColor) || string.IsNullOrEmpty(row.StateLabel)
                || string.IsNullOrEmpty(row.TriageBucket))
            {
                yield return new Finding(row.SessionId, name, "unstamped",
                    $"the Gateway returned effectiveColor='{row.EffectiveColor}', stateLabel='{row.StateLabel}', " +
                    $"triageBucket='{row.TriageBucket}' - a client cannot render an answer that is not there.");
                continue;
            }

            // ---- 2. THE STAMPED ANSWER IS THE FOLD'S ANSWER. Catches a row stamped by a different code
            // path than the shared fold - defect 6 (the Exes page folding without the fleet pass) - and the
            // shape of defect 7 (a dot and a label in one row from two authorities).
            var foldColor = SessionOrdering.EffectiveColor(row);
            var foldLabel = SessionOrdering.StateLabel(row);
            var foldBucket = SessionOrdering.Classify(row) switch
            {
                SessionOrdering.TriageBucket.NeedsYou => "needsYou",
                SessionOrdering.TriageBucket.OnHold => "onHold",
                _ => "active",
            };

            if (!string.Equals(foldColor, row.EffectiveColor, StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped colour '{row.EffectiveColor}' but the shared fold says '{foldColor}'.");
            if (!string.Equals(foldLabel, row.StateLabel, StringComparison.Ordinal))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped label '{row.StateLabel}' but the shared fold says '{foldLabel}'.");
            if (!string.Equals(foldBucket, row.TriageBucket, StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "stamp-not-fold",
                    $"the Gateway stamped bucket '{row.TriageBucket}' but the shared fold says '{foldBucket}'.");

            // ---- 3. THE LAW. If a session is working, it is BLUE. Always. Nothing outranks working.
            // Measured over the real fleet rather than over a constructed DTO.
            var working = string.Equals(row.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(row.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);
            if (working && !string.Equals(row.EffectiveColor, "blue", StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "law-broken",
                    $"activityState='{row.ActivityState}' but the fleet shows '{row.EffectiveColor}' " +
                    $"('{row.StateLabel}'). THE LAW: if a session is working it is BLUE, always.");

            // ---- 4. THE DESKTOP'S ANSWER. The real fold over the reconstructed desktop input, and the
            // ONLY check on this row that the destroyed BriefingState can defeat.
            //
            // THE REFUSAL LIVES HERE, NOT AT THE TOP OF THE LOOP. It used to sit above check 1 and
            // `continue` - so an unreadable row skipped the stamp check, the fold check, the LAW check and
            // the palette check as well, every one of which is Gateway-side and CERTAIN regardless of what
            // the Director's briefing label had been. An ambiguous row could therefore hide a definite
            // stamp-not-fold or a broken law behind "I cannot read this one". Refusing to answer the
            // question you cannot answer is honest; refusing to answer the four you can is just silence
            // with better manners. Found by inspection of pull request 1606.
            // THE REFUSAL THAT USED TO BE HERE IS DELETED, AND SO IS THE SHAPE THAT JUSTIFIED IT. Every
            // row is now gradeable, because SessionDto.BriefingState has exactly ONE writer again:
            // ControlEndpoints.Map, straight from the Director's own enum. The Gateway's overwrite - the
            // thing that destroyed the fact this comparison needs - is gone (gap 5), so "Briefing" on a
            // row means the Director said "Briefing", full stop, and the desktop reconstruction below is
            // the desktop's real row rather than one of two guesses.
            //
            // NOTE WHAT DID *NOT* HAPPEN: the SHAPE did not become unreachable. A row can still carry
            // VoiceGenerating=true alongside BriefingState="Briefing" - a Director genuinely briefing a
            // session whose voice the Gateway is generating is an ordinary thing. What died is the
            // AMBIGUITY, not the shape. That distinction is why this had to be deleted rather than left:
            // the predicate would have kept firing on those rows and refused to grade them, and the
            // desktop's answer there is now perfectly readable. Keeping it would not have been harmless
            // dead code, it would have been an instrument refusing rows it can read - the exact
            // crying-wolf failure its own comment warned about, arrived at from the other side.
            {
                var desktopInput = ToDesktopInput(row);
                var desktopColor = SessionOrdering.EffectiveColor(desktopInput);
                var desktopLabel = SessionOrdering.StateLabel(desktopInput);

                if (!string.Equals(desktopColor, row.EffectiveColor, StringComparison.OrdinalIgnoreCase))
                    yield return new Finding(row.SessionId, name, "desktop-vs-gateway",
                        $"the phone and the Cockpit show '{row.EffectiveColor}' ({row.StateLabel}); the desktop rail " +
                        $"folds '{desktopColor}' ({desktopLabel}) from the facts it has. {WhyDesktopDiffers(row)}");
                else if (!string.Equals(desktopLabel, row.StateLabel, StringComparison.Ordinal))
                    yield return new Finding(row.SessionId, name, "desktop-vs-gateway",
                        $"same colour, different words: the phone reads '{row.StateLabel}', the desktop reads " +
                        $"'{desktopLabel}'. {WhyDesktopDiffers(row)}");
            }

            // ---- 5. THE RENDERED PIXEL, MEASURED AGAINST THE ONE CANONICAL MAP. Comparing the fold's
            // ANSWER (the colour NAME) reports agreement while two screens paint different hexes - the exact
            // hole the Phase 4 Manager found (the rail's red #EF4444 against the client's #F14C4C, the rail's
            // yellow #EAB308 against #F59E0B). Law 7 is "every device shows the same thing, always", and the
            // thing the owner sees is a pixel, not a string.
            //
            // The "Dumb Clients" palette slice made ONE map the source: SessionColorPalette
            // (CcDirector.Gateway.Contracts). The Gateway stamps it onto SessionDto.EffectiveColorHex (what
            // the web session dot paints), and the desktop StatusPalette now REFERENCES it compile-time. So
            // this check resolves the name through canonical and asserts each client table matches it - the
            // guard that keeps the web's over-the-wire values from ever drifting from the canonical source.
            // A NAME NO PALETTE KNOWS IS NOT DRIFT, AND MUST NOT BE REPORTED AS DRIFT. Every comparison
            // below reads HexFor, and HexFor answers an unknown name with a SENTINEL rather than a colour -
            // and since the Session Cards mission the two sides deliberately answer with DIFFERENT
            // sentinels: this check's build paints the desktop's neutral, the canonical map and the
            // clients paint the magenta. Comparing those would report "the desktop palette has drifted
            // from SessionColorPalette", which is a lie about a build that is simply older than the
            // Gateway that emitted the name. So this case is answered once, here, and the drift
            // comparisons below never see it.
            if (!SessionColorPalette.Knows(row.EffectiveColor))
            {
                yield return new Finding(row.SessionId, name, "palette-missing",
                    $"the Gateway emitted colour '{row.EffectiveColor}' and no palette knows it - the desktop " +
                    $"rail paints its neutral {StatusPalette.Neutral} (honest: this build is older than that " +
                    $"Gateway), and the clients paint the magenta {SessionColorPalette.Broken}. Teach the " +
                    "palettes the name, or update the build.");
                continue;
            }

            var canonicalHex = SessionColorPalette.HexFor(row.EffectiveColor).ToUpperInvariant();
            var desktopHex = StatusPalette.HexFor(row.EffectiveColor).ToUpperInvariant();

            // The desktop table must equal canonical. It does so BY CONSTRUCTION today (StatusPalette's
            // constants reference SessionColorPalette), so this arm is the tripwire that fires the instant a
            // future edit hardcodes a desktop hex again - the very drift this mission exists to end.
            if (!string.Equals(desktopHex, canonicalHex, StringComparison.OrdinalIgnoreCase))
                yield return new Finding(row.SessionId, name, "two-different-pixels",
                    $"the desktop StatusPalette paints '{row.EffectiveColor}' {desktopHex}, the canonical map " +
                    $"{canonicalHex} - both fold the same name and paint different colours. The desktop palette " +
                    "has drifted from SessionColorPalette. Law 7 is 'every device shows the same thing, always'.");

            // The web client's COLORS table must equal canonical too - read from the file the phone and the
            // Cockpit actually ship, so a drift here is a real different pixel on those screens.
            if (!clientPalette.TryGetValue(row.EffectiveColor, out var clientHex))
            {
                yield return new Finding(row.SessionId, name, "palette-missing",
                    $"the Gateway emitted colour '{row.EffectiveColor}' and the client palette " +
                    $"({ClientPalette.RelativePath}) has no entry for it - the phone cannot paint it.");
            }
            else if (!string.Equals(clientHex.ToUpperInvariant(), canonicalHex, StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(row.SessionId, name, "two-different-pixels",
                    $"both surfaces fold to '{row.EffectiveColor}' and AGREE - and then paint different colours: " +
                    $"the canonical map {canonicalHex}, the web client table {clientHex.ToUpperInvariant()}. Law 7 is " +
                    "'every device shows the same thing, always'.");
            }
        }
    }

    /// <summary>
    /// Reconstruct the DESKTOP's fold input from a live Gateway row, by removing exactly the facts the
    /// Gateway adds and <c>ControlEndpoints.Map</c> does not carry.
    ///
    /// WHY A RECONSTRUCTION AND NOT A READ. The desktop never uses HTTP: SessionViewModel runs INSIDE the
    /// Director process and folds the in-process Session directly. The Director's own GET /fleet/sessions
    /// RELAYS the Gateway's already-stamped roster when a Gateway is attached (ControlEndpoints.cs:321-330
    /// -> GatewayClient.ListFleetSessionsAsync -> GET /sessions) - verified by probing a live Director, not
    /// by reading the code. So folding THAT and comparing it to the Gateway would be one function agreeing
    /// with itself: a guaranteed, meaningless ZERO. This is the check's one modelled step, so every removal
    /// below is cited and none is a guess.
    ///
    /// Verified by reading ControlEndpoints.Map (ControlEndpoints.cs:1188 onward): it carries ActivityState,
    /// Crashed, BriefingState (the DIRECTOR's own enum), VoiceMode, HoldState/OnHold, IsTranscribing,
    /// IsBrandNew, IsBackgroundRunning, IsAutoExplaining, WingmanEnabled, IsControlled, ControllerSessionId,
    /// StatusColor - and SessionRole, read from Session.GatewayResolvedRole, written ONLY by the Gateway's
    /// set-resolved-role verb (defect 5's fix).
    ///
    /// It does NOT carry these five, which the Gateway stamps in its roster loop (GatewayEndpoints.cs
    /// ~:770-790) and which NOTHING pushes down - the Gateway sends exactly two verbs to a Director,
    /// "launch" and "set-resolved-role". Each is a fold input, so each is a real divergence:
    ///   * DictationStatus - the durable phone-dictation record. Gateway: orange. Desktop: cannot see it.
    ///   * Transcribing - the Gateway's active-run mark. Gateway: orange. Desktop: cannot see it.
    ///   * VoiceGenerating - drives IsVoicePreparing (yellow). Gateway can see it; desktop cannot. Note that
    ///     since the 2026-07-19 widening of IsVoicePreparing to "yellow while !VoiceAudioReady", stripping
    ///     VoiceGenerating no longer flips the desktop fold on its own: with no audio either, the desktop
    ///     still folds yellow. VoiceAudioReady (below) is now the voice fact that actually diverges.
    ///   * VoiceAudioReady - the Gateway holds playable audio (VoiceGenerating false, !VoiceAudioReady false),
    ///     so it folds RED "needs you". The desktop cannot see the Gateway's audio, so it still folds YELLOW
    ///     "preparing voice". Gateway: red. Desktop: yellow. A real divergence, reported below.
    ///   * The OnHold expiry overlay - the Gateway owns the snooze clock and overlays OnHold=false BEFORE
    ///     the fold. The desktop reads the Director's raw hold. This one is TRANSIENT, not structural: the
    ///     reliable display-state channel reconciles the Director's raw hold at fold cadence (there is no
    ///     expiry sweep).
    ///
    /// KNOWN LIMIT, stated rather than papered over: the desktop's OWN SessionRole is not externally
    /// observable, because the Gateway's fleet pass overwrites the inbound role on every read
    /// (FleetRoleResolver.Stamp - every branch assigns). So a role that is STALE on a Director cannot be
    /// seen from here. That push is proved in-process, with nothing hand-set, by DesktopRoleStampWireProofTests.
    /// </summary>
    // IsIndeterminate WAS HERE, AND IS DELETED - DO NOT BRING IT BACK (gap 5).
    //
    // It answered "can this row be graded at all?", and it existed for exactly one shape: a row carrying
    // VoiceGenerating=true AND BriefingState="Briefing", back when the Gateway got its voice-mode yellow by
    // WRITING "Briefing" over a Director-owned field. That write destroyed the fact the desktop
    // reconstruction needs, so the row had two possible origins - the Director genuinely briefing (the
    // desktop folds yellow, and agrees) or the Gateway overwriting a "None" (the desktop folds red, and
    // disagrees) - with no way to tell them apart. Refusing to grade it was the honest answer to a question
    // the instrument genuinely could not answer.
    //
    // The Gateway no longer writes that field (gap 5: the overwrite is deleted and NOTHING replaced it,
    // because SessionOrdering.IsVoicePreparing already folds the Gateway's own VoiceGenerating fact). On
    // the fleet path SessionDto.BriefingState now has exactly ONE writer - ControlEndpoints.Map, straight
    // from the Director's own enum - so "Briefing" on a row means the Director said "Briefing". There is
    // nothing left to disambiguate, and the desktop reconstruction is the desktop's real row.
    //
    // READ THIS BEFORE RESTORING IT, BECAUSE THE OBVIOUS REASON IS THE WRONG ONE: the SHAPE it gated on is
    // still perfectly reachable. A Director genuinely briefing a session whose voice the Gateway is
    // generating carries both facts at once, and that is an ordinary row. Verified by probe against the
    // real Compare before deleting: such a row made this predicate return TRUE and emit an "indeterminate"
    // finding, while the desktop reconstruction AGREED with the Gateway - the instrument refusing a row it
    // could read perfectly. So this was deleted because it had become WRONG, not because it had become
    // unused. Its own comment said it best: an instrument that refuses to read rows it can read perfectly
    // well gets ignored just as fast as one that reads them wrong.
    //
    // If a Gateway-side write to a Director-owned field is ever reintroduced, the ambiguity returns and so
    // must a refusal. Do not reintroduce the write.


    public static SessionDto ToDesktopInput(SessionDto row)
    {
        var copy = JsonSerializer.Deserialize<SessionDto>(JsonSerializer.Serialize(row, Json), Json)!;

        copy.DictationStatus = null;
        copy.Transcribing = false;
        copy.VoiceGenerating = false;
        // The Gateway stamps VoiceAudioReady from its own voice store (GatewayEndpoints -> _voiceService
        // .HasVoice); the Director never sets it. Since IsVoicePreparing now reads !VoiceAudioReady, the
        // desktop's real value (false) must drive the reconstruction, not the Gateway's - otherwise a row
        // whose voice is READY would look identical on both screens when the desktop actually cannot see it.
        copy.VoiceAudioReady = false;

        // Un-apply the Gateway's expiry overlay: it set OnHold=false and flagged SnoozeExpired, so the
        // Director's own hold still reads Held until the sweep's nudge reaches it.
        if (row.SnoozeExpired)
        {
            copy.HoldState = HoldStates.Held;
            copy.SnoozeExpired = false;
        }

        // The desktop computes these itself - it must never read the Gateway's.
        copy.EffectiveColor = null;
        copy.StateLabel = null;
        copy.TriageBucket = null;

        return copy;
    }

    /// <summary>Name, in plain English, which Gateway-only fact produced a desktop/Gateway divergence.
    /// Stated from the row's own fields - never guessed. When no field explains it, say so rather than
    /// inventing a cause: a fabricated cause reads exactly like a finding and sends the next reader to fix
    /// a world that does not exist.</summary>
    public static string WhyDesktopDiffers(SessionDto row)
    {
        var reasons = new List<string>();
        if (row.DictationStatus is not null)
            reasons.Add($"the Gateway can see a phone dictation ('{row.DictationStatus}') and the desktop cannot");
        if (row.Transcribing)
            reasons.Add("the Gateway can see its own transcription mark and the desktop cannot");
        if (row.VoiceGenerating)
            reasons.Add("the Gateway can see voice being prepared and the desktop cannot");
        if (row.VoiceAudioReady)
            reasons.Add("the Gateway has this session's voice audio ready (so it reads red 'needs you') and the desktop cannot see it (so it still reads yellow 'preparing voice')");
        if (row.SnoozeExpired)
            reasons.Add("the Gateway's snooze clock has expired and the Director has not been nudged off hold yet");
        return reasons.Count == 0
            ? "Cause NOT identified from this row's fields - do not guess one; go and look."
            : "Because " + string.Join("; and ", reasons) + ".";
    }
}
