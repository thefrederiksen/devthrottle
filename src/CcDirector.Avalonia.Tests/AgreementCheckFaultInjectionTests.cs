using CcDirector.Gateway.Contracts;
using CcDirector.StateAgreementCheck;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE CHECK THAT CHECKS THE CHECK. Every fault the cross-surface agreement check claims to catch is
/// injected here, and the check is watched REPORTING IT - because a green check nobody has ever seen fail
/// is exactly what this mission exists to end.
///
/// WHY THIS FILE IS THE POINT. The live check currently reports ZERO over the real fleet. That number is
/// worth nothing on its own: this repository has shipped a suite that was green for FOURTEEN MONTHS over a
/// state production never emitted, and a specification that read like a finding while being invented. The
/// mission's own law is that a test is not proof until you have watched it go red with the reported
/// symptom. The same standard has to apply to the instrument. So each test below breaks one thing, asserts
/// the check NAMES it, and <see cref="ACleanRoster_ReportsNothing"/> is the control that stops the whole
/// file from passing by simply being red about everything.
///
/// AND THE CASE THE LIVE RUN CANNOT REACH. The live fleet had ZERO sessions carrying a Gateway-only fold
/// input at the moment it was measured - no phone dictation, no transcription, no voice being prepared, no
/// expired snooze. So the live zero DID NOT EXERCISE those arms at all. They are exercised here, on purpose,
/// which is the difference between a proof and a green light.
///
/// Design: docs/new_architecture/sessions.html, section 6.
/// </summary>
public sealed class AgreementCheckFaultInjectionTests
{
    /// <summary>The canonical palette, as the client ships it. Spelled out literally rather than read from
    /// the constants - a test that reads the value it is checking proves nothing.</summary>
    private static Dictionary<string, string> CanonicalClientPalette() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#EF4444",
        ["blue"] = "#3B82F6",
        ["green"] = "#22C55E",
        ["cyan"] = "#06B6D4",
        ["yellow"] = "#EAB308",
        ["orange"] = "#F97316",
        ["purple"] = "#A855F7",
        ["supporting"] = "#64748B",
        ["error"] = "#B91C1C",
        ["grey"] = "#6B7280",
    };

    /// <summary>A session exactly as the Gateway serves it: folded by the REAL fold and stamped from that
    /// fold's own answers, which is what StampFleetRolesAndFold does. Nothing here is hand-stamped, so a
    /// row built by this helper is correct BY CONSTRUCTION and any finding against it is the check's fault.</summary>
    private static SessionDto AsGatewayServesIt(SessionDto s)
    {
        s.EffectiveColor = SessionOrdering.EffectiveColor(s);
        // The Gateway stamps the hex beside the name in the same fold (StampFleetRolesAndFold), so a row
        // built here carries it too - the web session dot paints this verbatim.
        s.EffectiveColorHex = SessionColorPalette.HexFor(s.EffectiveColor);
        s.StateLabel = SessionOrdering.StateLabel(s);
        s.TriageBucket = SessionOrdering.Classify(s) switch
        {
            SessionOrdering.TriageBucket.NeedsYou => "needsYou",
            SessionOrdering.TriageBucket.OnHold => "onHold",
            _ => "active",
        };
        return s;
    }

    private static SessionDto Waiting(string id = "s") =>
        AsGatewayServesIt(new SessionDto { SessionId = id, Name = id, ActivityState = "WaitingForInput", StatusColor = "red" });

    private static SessionDto Working(string id = "s") =>
        AsGatewayServesIt(new SessionDto { SessionId = id, Name = id, ActivityState = "Working", StatusColor = "blue" });

    private static List<AgreementCheck.Finding> Run(params SessionDto[] roster) =>
        AgreementCheck.Compare(roster, CanonicalClientPalette()).ToList();

    // ===================== THE CONTROL =====================

    /// <summary>
    /// THE CONTROL, and the most important test in the file. Every other test here breaks something and
    /// asserts a finding; without this one they would all still pass if the check simply reported
    /// everything, always. A check that cannot be quiet cannot be trusted when it is.
    /// </summary>
    [Fact]
    public void ACleanRoster_ReportsNothing()
    {
        var findings = Run(Working("working"), Waiting("waiting"),
            AsGatewayServesIt(new SessionDto { SessionId = "snoozed", Name = "snoozed", ActivityState = "WaitingForInput", HoldState = HoldStates.Held }),
            AsGatewayServesIt(new SessionDto { SessionId = "crashed", Name = "crashed", ActivityState = "Exited", Crashed = true }),
            AsGatewayServesIt(new SessionDto { SessionId = "exited", Name = "exited", ActivityState = "Exited" }));

        Assert.Empty(findings);
    }

    // ===================== THE LAW =====================

    /// <summary>
    /// THE LAW, broken on purpose: a session that is WORKING, stamped anything but blue. This is defect 1 -
    /// the controlled session that read grey "Sub-agent" while 23 minutes and 56 thousand tokens into real
    /// work - and it is the single thing the check exists to make impossible to ship again.
    /// </summary>
    [Fact]
    public void AWorkingSessionThatIsNotBlue_IsReportedAsTheLawBroken()
    {
        var row = Working("busy-worker");
        row.EffectiveColor = "supporting";   // the old ladder's answer: ownership erasing activity
        row.StateLabel = "Sub-agent";

        var findings = Run(row);

        Assert.Contains(findings, f => f.Kind == "law-broken");
        Assert.Contains(findings, f => f.Detail.Contains("if a session is working it is BLUE", StringComparison.Ordinal));
    }

    // ===================== THE STAMP =====================

    /// <summary>Defect 15: the DTO documents these "Required on Gateway /sessions responses" and the by-id
    /// route returned them null. A client that cannot get a stamped answer fails loudly - magenta on the
    /// rail, a blank page in the Cockpit - so an absent stamp is a rendered defect, not a cosmetic one.</summary>
    [Fact]
    public void AnUnstampedRow_IsReported_BecauseNoClientCanRenderAnAnswerThatIsNotThere()
    {
        var row = Waiting("unstamped");
        row.EffectiveColor = null;

        Assert.Contains(Run(row), f => f.Kind == "unstamped");
    }

    /// <summary>
    /// Defect 6: the Exes page called the fold but skipped the fleet pass, so it produced a different answer
    /// for the same session than every other screen. The shape is a row whose stamp is not what the shared
    /// fold says - whoever stamped it, it was not this fold over these inputs.
    /// </summary>
    [Fact]
    public void ARowStampedBySomethingOtherThanTheSharedFold_IsReported()
    {
        var row = Waiting("stamped-elsewhere");
        row.EffectiveColor = "grey";   // some other authority's answer for a session the fold calls red

        var findings = Run(row);

        Assert.Contains(findings, f => f.Kind == "stamp-not-fold");
    }

    /// <summary>
    /// Defect 7: the dot and the State cell beside it disagreeing IN THE SAME ROW - one read the Gateway's
    /// answer, the other re-derived its own. This is why the check compares the label and the bucket too,
    /// and not merely the colour.
    /// </summary>
    [Fact]
    public void ARowWhoseLabelContradictsItsDot_IsReported_NotJustTheColour()
    {
        var row = Waiting("dot-vs-words");
        row.StateLabel = "Snoozed";   // the dot says red "Needs you"; the words say parked

        var findings = Run(row);

        Assert.Contains(findings, f => f.Kind == "stamp-not-fold" && f.Detail.Contains("label", StringComparison.Ordinal));
    }

    // ===================== THE RENDERED PIXEL =====================

    /// <summary>
    /// THE HOLE THE PHASE 4 MANAGER FOUND, and it would have made the whole measurement worthless.
    ///
    /// Both surfaces fold to the string "red". They AGREE. A check that compares the fold's ANSWER reports
    /// ZERO - and the two screens paint visibly different reds. This is the real, shipped pre-Phase-4 state:
    /// the rail's #EF4444 against the client's #F14C4C (VS Code red), and the rail's yellow #EAB308 against
    /// #F59E0B (amber-500 - a real Tailwind colour, wrong ramp member for the NAME). Law 7 is "every device
    /// shows the same thing, always", and the thing the owner sees is a pixel, not a string.
    ///
    /// This was also confirmed against the LIVE fleet: reintroducing #F14C4C into the shipping client table
    /// made the live check report eleven of these and then go back to zero when it was reverted.
    /// </summary>
    [Theory]
    [InlineData("red", "#F14C4C")]    // the VS Code red the client used to ship
    [InlineData("yellow", "#F59E0B")] // amber-500 under the name "yellow"
    public void TwoSurfacesThatAgreeOnTheNameAndPaintDifferentPixels_AreReported(string name, string strayHex)
    {
        var row = name == "red" ? Waiting("two-reds") : YellowRow();
        Assert.Equal(name, row.EffectiveColor); // the surfaces AGREE on the name - that is the whole trap

        var palette = CanonicalClientPalette();
        palette[name] = strayHex;   // the client drifts; the fold's answer does not change at all

        var findings = AgreementCheck.Compare(new[] { row }, palette).ToList();

        Assert.Contains(findings, f => f.Kind == "two-different-pixels");
        Assert.Contains(findings, f => f.Detail.Contains("paint different colours", StringComparison.Ordinal));

        // ...and the proof that the OLD check was blind to it: the fold's answer is identical on both sides.
        Assert.Equal(row.EffectiveColor, SessionOrdering.EffectiveColor(row));
    }

    /// <summary>
    /// A NEWER GATEWAY, AN OLDER EVERYTHING ELSE - and the check must call it what it is.
    ///
    /// The Session Cards mission made the desktop rail answer an unrecognised colour NAME with a neutral
    /// while the canonical map and the clients still answer it with the magenta sentinel. Those are two
    /// different pixels for the same name, on purpose, and a naive pixel comparison reads that as the
    /// desktop palette having DRIFTED from the canonical map - which would send somebody hunting a
    /// hand-rolled hex that does not exist, when the real answer is that this build predates the colour.
    ///
    /// So: exactly ONE finding, and it is the palette-missing one. No two-different-pixels.
    /// </summary>
    [Fact]
    public void AColourNoBuildKnows_IsReportedAsAMissingPalette_NotAsDriftBetweenTwoPixels()
    {
        var row = Waiting("newer-gateway");
        row.EffectiveColor = "chartreuse";   // a name a later fold learned and this build never did

        var findings = Run(row);

        var missing = Assert.Single(findings, f => f.Kind == "palette-missing");
        Assert.Contains("chartreuse", missing.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => f.Kind == "two-different-pixels");
    }

    private static SessionDto YellowRow() =>
        AsGatewayServesIt(new SessionDto
        {
            SessionId = "briefing", Name = "briefing", ActivityState = "WaitingForInput", BriefingState = "Briefing",
        });

    // ===================== THE DESKTOP THE LIVE FLEET COULD NOT SHOW US =====================

    /// <summary>
    /// THE FOUR INPUTS THE DESKTOP CANNOT SEE - and the arms the live run did not exercise, because at the
    /// moment it was measured ZERO of the thirteen live sessions carried any of them.
    ///
    /// Defect 5's fix pushed SessionRole down to the desktop. It was ONE of FIVE Gateway-only fold inputs.
    /// ControlEndpoints.Map still does not carry DictationStatus, Transcribing or VoiceGenerating, and the
    /// Gateway sends a Director exactly two verbs ("launch", "set-resolved-role"), so nothing pushes them
    /// down. The fold reads all three. Each one is therefore a real, live disagreement between the rail and
    /// the phone - for a session that is NOT working, since blue outranks every one of them.
    ///
    /// These tests assert the GAP, not a fix. Closing it is a scope decision for the owner - the
    /// specification's own section 3 says the desktop "cannot compute its own colour. It must ask."
    /// </summary>
    [Fact]
    public void APhoneDictation_IsOrangeOnTheGatewayAndRedOnTheDesktop_AndIsReported()
    {
        var row = Waiting("dictating");
        row.DictationStatus = "Uploading from phone";
        AsGatewayServesIt(row);
        Assert.Equal("orange", row.EffectiveColor); // what the phone and the Cockpit show

        var findings = Run(row);

        Assert.Contains(findings, f => f.Kind == "desktop-vs-gateway");
        Assert.Contains(findings, f => f.Detail.Contains("phone dictation", StringComparison.Ordinal));
        // The desktop's own answer, from the facts it actually has:
        Assert.Equal("red", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    [Fact]
    public void AGatewayTranscriptionMark_IsOrangeOnTheGatewayAndRedOnTheDesktop_AndIsReported()
    {
        var row = Waiting("transcribing");
        row.Transcribing = true;
        AsGatewayServesIt(row);
        Assert.Equal("orange", row.EffectiveColor);

        Assert.Contains(Run(row), f => f.Kind == "desktop-vs-gateway");
        Assert.Equal("red", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    [Fact]
    public void VoiceAudioReady_IsRedOnTheGatewayAndYellowOnTheDesktop_AndIsReported()
    {
        // THE VOICE DIVERGENCE, as it stands since the 2026-07-19 widening of IsVoicePreparing (yellow while
        // !VoiceAudioReady). A voice-mode session whose audio the Gateway HAS ready folds red "needs you" on
        // the Gateway - VoiceGenerating false, VoiceAudioReady true, so it is no longer preparing. The desktop
        // cannot see the Gateway's audio store, so it still folds yellow "preparing voice". Gateway red,
        // desktop yellow - a real divergence, and VoiceAudioReady is the Gateway-only fact behind it.
        var row = Waiting("voice-ready");
        row.VoiceMode = true;
        row.VoiceAudioReady = true;   // the GATEWAY holds playable audio; the desktop cannot see it
        AsGatewayServesIt(row);
        Assert.Equal("red", row.EffectiveColor);

        var findings = Run(row);
        Assert.Contains(findings, f => f.Kind == "desktop-vs-gateway");
        Assert.Contains(findings, f => f.Detail.Contains("voice audio ready", StringComparison.Ordinal));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    /// <summary>
    /// The behaviour change of 2026-07-19 made visible: voice BEING PREPARED (generating, or simply no audio
    /// yet) now AGREES across the two surfaces. Because IsVoicePreparing holds yellow while !VoiceAudioReady,
    /// and the desktop reconstruction also has no audio, BOTH fold yellow. This row used to be the divergence
    /// (its yellow came only from the Gateway-only VoiceGenerating); it is now the agreement, because the
    /// yellow no longer depends on a fact only the Gateway can see. VoiceAudioReady (the test above) is now
    /// the fact that actually diverges.
    /// </summary>
    [Fact]
    public void VoiceBeingPrepared_NowAgrees_BecauseNoAudioFoldsYellowOnBothSurfaces()
    {
        var row = Waiting("voice-preparing");
        row.VoiceMode = true;
        row.VoiceGenerating = true;   // Gateway-only, but stripping it still leaves !VoiceAudioReady -> yellow
        AsGatewayServesIt(row);
        Assert.Equal("yellow", row.EffectiveColor);

        Assert.Empty(Run(row));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }
    /// <summary>
    /// The control for the one above, and the reason it is not just "report everything with a briefing
    /// label". A Director that is briefing with NO voice generation is perfectly readable: the Gateway
    /// cannot have overwritten anything - it does not write that field at all any more (gap 5). Both
    /// surfaces have the same label, both fold yellow, and the check must stay quiet.
    /// </summary>
    [Fact]
    public void ADirectorBriefingWithoutVoiceGeneration_IsReadable_AndAgrees()
    {
        var row = Waiting("briefing-only");
        row.BriefingState = "Briefing";
        AsGatewayServesIt(row);
        Assert.Equal("yellow", row.EffectiveColor);

        Assert.Empty(Run(row));
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    /// <summary>
    /// THE ROW THE CHECK USED TO REFUSE, AND NOW GRADES. This is the test that earns the deletion of
    /// IsIndeterminate, so read it before restoring that predicate.
    ///
    /// A Director genuinely briefing a session whose voice the Gateway is generating carries BOTH facts at
    /// once - VoiceGenerating=true AND BriefingState="Briefing" - and that is an ordinary row, not a
    /// contrived one. It is exactly the shape IsIndeterminate gated on, and THE SHAPE IS STILL REACHABLE:
    /// deleting the Gateway's overwrite killed the AMBIGUITY, not the shape. That distinction is the whole
    /// reason the predicate had to go rather than be left as harmless dead code - it would have kept firing
    /// here, on a row whose desktop answer is now perfectly readable, and refused to grade it.
    ///
    /// Readable because BriefingState has exactly one writer again (ControlEndpoints.Map, from the
    /// Director's own enum), so "Briefing" means the Director said "Briefing" - there is no second origin
    /// to weigh. The desktop has that label too and folds yellow; the Gateway folds yellow; they agree; the
    /// check says nothing. Under the old code this row produced an "indeterminate" finding - verified by
    /// probe against the real Compare before the deletion, not assumed.
    /// </summary>
    [Fact]
    public void ADirectorBriefingWhileTheGatewayPreparesVoice_IsGraded_AndAgrees()
    {
        var row = Waiting("briefing-and-voice");
        row.VoiceMode = true;
        row.VoiceGenerating = true;   // the GATEWAY's own fact, added
        row.BriefingState = "Briefing";  // the DIRECTOR's own fact, carried - not a hijack
        AsGatewayServesIt(row);

        // The Director's briefing wins the fold's first arm, so both surfaces read "Wingman reading".
        Assert.Equal("yellow", row.EffectiveColor);
        Assert.Equal("Wingman reading", row.StateLabel);

        // The desktop cannot see VoiceGenerating - and here it does not need to: it has the Director's own
        // label, which is the same fact the Gateway folded. So they agree, and this is GRADED, not refused.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
        Assert.Empty(Run(row));
    }

    /// <summary>
    /// The expired snooze. TRANSIENT rather than structural, and the difference matters: the Gateway owns
    /// the clock and overlays OnHold=false before the fold, so it says red "Needs you" while the Director
    /// still reads Held and the rail says grey "Snoozed" - but the reliable display-state channel reconciles
    /// the Director's raw hold at fold cadence (there is no expiry sweep), so the window is bounded. It is
    /// reported all the same: a bounded disagreement is still a disagreement, and the bound is a claim about
    /// timing that this check does not measure.
    /// </summary>
    [Fact]
    public void AnExpiredSnooze_IsRedOnTheGatewayAndStillSnoozedOnTheDesktop_AndIsReported()
    {
        // The Gateway's overlay, exactly as StampFleetRolesAndFold applies it: OnHold off, SnoozeExpired on.
        var row = new SessionDto
        {
            SessionId = "expired", Name = "expired", ActivityState = "WaitingForInput",
            HoldState = HoldStates.None, SnoozeExpired = true,
        };
        AsGatewayServesIt(row);
        Assert.Equal("red", row.EffectiveColor);

        var findings = Run(row);

        Assert.Contains(findings, f => f.Kind == "desktop-vs-gateway");
        Assert.Contains(findings, f => f.Detail.Contains("snooze clock has expired", StringComparison.Ordinal));
        Assert.Equal("grey", SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    /// <summary>
    /// THE COMMON CASE AGREES - which is precisely why nobody ever saw the gap above. A session that is
    /// working, or simply waiting, carries none of the four, so the rail and the phone fold identical
    /// inputs and reach identical answers. Every lie this mission has found has this shape: correct in the
    /// ordinary case, wrong in the one that was never looked at.
    /// </summary>
    [Theory]
    [InlineData("Working")]
    [InlineData("WaitingForInput")]
    [InlineData("Exited")]
    public void WithNoGatewayOnlyInput_TheDesktopAndTheGatewayAgree(string activityState)
    {
        var row = AsGatewayServesIt(new SessionDto { SessionId = "ordinary", Name = "ordinary", ActivityState = activityState });

        Assert.Empty(Run(row));
        Assert.Equal(row.EffectiveColor, SessionOrdering.EffectiveColor(AgreementCheck.ToDesktopInput(row)));
    }

    /// <summary>
    /// AND THE ONE THE LAW ALREADY PROTECTS: a WORKING session with all four Gateway-only inputs at once
    /// still agrees, because blue outranks every one of them. This is the law paying for itself - the gap
    /// above is real, and it can only ever bite a session that has stopped.
    /// </summary>
    [Fact]
    public void AWorkingSessionWithEveryGatewayOnlyInputAtOnce_StillAgrees_BecauseNothingOutranksWorking()
    {
        var row = Working("busy");
        row.DictationStatus = "Uploading from phone";
        row.Transcribing = true;
        row.IsTranscribing = true;
        row.VoiceMode = true;
        row.VoiceGenerating = true;
        row.VoiceAudioReady = true;
        row.BriefingState = "Briefing";
        AsGatewayServesIt(row);

        Assert.Equal("blue", row.EffectiveColor);
        Assert.Empty(Run(row));
    }
}
