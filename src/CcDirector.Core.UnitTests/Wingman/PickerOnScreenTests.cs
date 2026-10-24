using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// WHETHER A PICKER IS DRAWN, decided by code (<see cref="PickerOnScreen"/>). Every screen here is a real shape from
/// the corpus, reduced to the rows that decide it; the ids name the corpus stops they came from. Each collision the
/// held-out read found - a place where a drawn control and something that is not one share a word - has a test, so
/// a later edit to the sign list cannot quietly reopen one.
/// </summary>
public sealed class PickerOnScreenTests
{
    private static readonly string Marker = ((char)0x276F).ToString();
    private static readonly string CodexMarker = ((char)0x203A).ToString();
    private static readonly string NoBreakSpace = ((char)0x00A0).ToString();
    private const string ComposerFooter = "  bypass permissions on (shift+tab to cycle) - esc to interrupt - 1 agent";

    private static PickerReading Read(params string[] rows) => PickerOnScreen.Read(rows, "ClaudeCode");

    // ================================================================= real pickers

    [Fact]
    public void APermissionPrompt_IsDrawn()
    {
        var reading = Read(" Do you want to proceed?", " " + Marker + " 1. Yes", "   2. No", " Esc to cancel - Tab to amend");

        Assert.True(reading.Drawn);
        Assert.False(reading.ModelPickerDrawn);
        Assert.Contains("proceed-prompt", reading.Signs);
    }

    /// <summary>1e7d9846: a stale composer status line under a real prompt is not evidence of no picker.</summary>
    [Fact]
    public void APermissionPrompt_UnderAStaleComposerStatusLine_IsStillDrawn()
    {
        Assert.True(Read(" Do you want to proceed?", " " + Marker + " 1. Yes", "   2. No", " Esc to cancel - Tab to amend",
            ComposerFooter).Drawn);
    }

    [Fact]
    public void TheFolderTrustPrompt_WithUnnumberedOptions_IsDrawn()
    {
        Assert.True(Read(" " + Marker + " No, exit", "   Yes, I trust this folder", " Enter to confirm - Esc to cancel").Drawn);
    }

    /// <summary>c021e4de and 622c7f52: a repaint tear leaves only the selected, INDENTED option of the rate-limit menu
    /// and the /model selector. Indentation is what makes one row enough.</summary>
    [Fact]
    public void ATornPicker_WithOnlyItsIndentedSelectedRowLeft_IsDrawn()
    {
        var reading = Read(Marker + " /rate-limit-options", "", "  " + Marker + " 2. Wait here, then continue automatically", ComposerFooter);

        Assert.True(reading.Drawn);
        Assert.Contains("selected-option-row", reading.Signs);
    }

    /// <summary>532aaffb and 47b6750c: the multi-question form, whose footer is cut off at the screen edge.</summary>
    [Fact]
    public void TheMultiQuestionForm_WithItsFooterCutOff_IsDrawn()
    {
        Assert.True(Read("Ready to submit your answers?", Marker + " 1. Submit answers", "  2. Cancel", "En").Drawn);
    }

    [Fact]
    public void ACodexPicker_WithItsOwnMarkerAndFooter_IsDrawn()
    {
        var reading = PickerOnScreen.Read(new[] { CodexMarker + " 1. Update now", "  2. Skip", "  Press enter to continue" }, "Codex");

        Assert.True(reading.Drawn);
        Assert.True(reading.Calibrated);
    }

    /// <summary>Claude Code 2.1.310, captured by the state matrix on 24 October 2026. The selected row is more than
    /// eight rows above the bottom and the changed footer says "set as default", so the earlier reader missed it.</summary>
    [Fact]
    public void Read_ClaudeCodeCurrentModelPicker_ReturnsDrawn()
    {
        var reading = Read(
            "     1.  Default (recommended)  Opus 5.5 · Best for everyday, complex tasks",
            "     2.  Opus 5.5               For complex work and everyday tasks",
            "     3.  Fable 5.1              For your toughest challenges",
            "     4.  Sonnet 5.5             Most efficient for simpler tasks",
            "     5.  Haiku 5.5              Fastest for quick answers",
            "   " + Marker + " 6.  Haiku 4.5 ✔            Fastest for quick answers",
            "     7.  Sonnet 5               Efficient for routine tasks",
            "     8.  Opus 5                 Best for everyday, complex tasks",
            "     9.  Fable 5                Most capable for your hardest and longest-running tasks",
            "   ↓ 10. Opus 4.8               Best for everyday, complex tasks",
            "      … +3 models",
            "",
            "   ○ Effort not supported for Haiku 4.5",
            "",
            "   Enter to set as default · s to use this session only · Esc to cancel");

        Assert.True(reading.Drawn);
        Assert.True(reading.ModelPickerDrawn);
        Assert.Contains("enter-to-set-default", reading.Signs);
    }

    /// <summary>Pi 1.2.0, captured by the state matrix on 24 October 2026.</summary>
    [Fact]
    public void Read_PiCurrentModelPicker_ReturnsDrawnAndCalibrated()
    {
        var reading = PickerOnScreen.Read(new[]
        {
            "Only showing models from configured providers. Use /login to add providers.",
            "",
            ">",
            "",
            "→ ✓ gpt-5.6-terra [azure-openai-responses]",
            "    gpt-5.6-sol [azure-openai-responses] · default",
            "    huihui-qwen3.5-4b-abliterated [lmstudio]",
            "",
            "  Model Name: Azure GPT-5.6 Terra (mindzie dev)",
            "",
            "  Model catalogs refreshed.",
            "",
            "  Enter to select · Ctrl+S to set as default · Escape/Ctrl+C to cancel",
            "────────────────────────────────────────────────────────────────────────────────",
            "~/ReposFred/_delivery-qa/repo-1 (master)",
            "0.0%/400k (auto)                 (azure-openai-responses) gpt-5.6-terra • thinking off"
        }, "Pi");

        Assert.True(reading.Drawn);
        Assert.True(reading.ModelPickerDrawn);
        Assert.True(reading.Calibrated);
        Assert.Contains("pi-model-select", reading.Signs);
    }

    // ================================================================= look-alikes that are not pickers

    /// <summary>The shape behind most invented menus: numbered yes/no questions in the reply, and the composer holding
    /// an answer. The composer's marker is followed by a NO-BREAK space, so even "2." there is not an option row.</summary>
    [Fact]
    public void NumberedQuestionsWithAnAnswerInTheComposer_IsNotDrawn()
    {
        Assert.False(Read("  1. Merge PR #38? (yes/no)", "  2. Rotate the key? (yes/no)",
            Marker + NoBreakSpace + "2. yes, merge 38", ComposerFooter).Drawn);
    }

    /// <summary>An earlier message echoed in the history sits at column 0 with the marker and an ORDINARY space. Alone,
    /// with no numbered neighbour, it is not a picker.</summary>
    [Fact]
    public void AnEchoedMessageStartingWithANumber_IsNotDrawn()
    {
        Assert.False(Read(Marker + " 3. A fix so this cannot happen again, plus a test.", "", "  Done - the fix is in.",
            Marker + NoBreakSpace, ComposerFooter).Drawn);
    }

    /// <summary>e43bd7b8: the usage-limit notice cancels a timer and offers no choice. Lower-case "esc to cancel".</summary>
    [Fact]
    public void TheUsageLimitNotice_IsNotDrawn()
    {
        Assert.False(Read("  You've hit your session limit - resets 5:40pm",
            "  Continuing automatically at 5:40pm - esc to cancel", ComposerFooter).Drawn);
    }

    /// <summary>6fe21ca1: the sign-in screen blocks ordinary messages but offers nothing to choose.</summary>
    [Fact]
    public void TheSignInScreen_IsNotDrawn()
    {
        Assert.False(Read("  Paste code here if prompted >", "  Esc to cancel").Drawn);
    }

    /// <summary>da2279da: "How do you want to proceed?" in the agent's prose contains the permission prompt's words.</summary>
    [Fact]
    public void TheWordsDoYouWantToProceed_InsideProse_AreNotDrawn()
    {
        Assert.False(Read("  How do you want to proceed? I recommend getting spawn working first.",
            Marker + NoBreakSpace + "try the spawn again", ComposerFooter).Drawn);
    }

    /// <summary>The survey is optional; a person types an ordinary message beneath it.</summary>
    [Fact]
    public void TheFeedbackSurvey_IsNotDrawn()
    {
        Assert.False(Read("  How is Claude doing this session? (optional)", "  1: Bad    2: Fine   3: Good   0: Dismiss",
            ComposerFooter).Drawn);
    }

    [Fact]
    public void Read_NormalComposer_ReturnsNotDrawn()
    {
        Assert.False(Read("────────────────────────────────", Marker + NoBreakSpace,
            "────────────────────────────────", ComposerFooter).Drawn);
    }

    [Fact]
    public void Read_HalfDrawnModelPickerWithoutAControlSign_ReturnsNotDrawn()
    {
        Assert.False(Read("   Select model",
            "   Switch between models. Your pick becomes the default for new sessions.",
            "", "     1.  Default (recommended)").Drawn);
    }

    /// <summary>A footer far above the bottom of the screen belongs to a picker that has since gone.</summary>
    [Fact]
    public void AFooterScrolledFarAboveTheBottom_IsNotDrawn()
    {
        var rows = new List<string> { " Enter to confirm - Esc to cancel" };
        for (var i = 0; i < PickerOnScreen.BottomRows + 2; i++) rows.Add("  the agent kept working");
        rows.Add(ComposerFooter);

        Assert.False(Read(rows.ToArray()).Drawn);
    }

    // ================================================================= unread, and unchecked agents

    [Fact]
    public void AnUnreadScreen_IsNotDrawn_AndSaysItWasNotRead()
    {
        foreach (var rows in new[] { null, Array.Empty<string>(), new[] { "", "   " } })
        {
            var reading = PickerOnScreen.Read(rows, "ClaudeCode");
            Assert.False(reading.Drawn);
            Assert.False(reading.ScreenRead);
        }
    }

    [Fact]
    public void AnAgentTheListWasNeverCheckedAgainst_IsJudgedByTheSameList_AndMarkedUncalibrated()
    {
        var reading = PickerOnScreen.Read(new[] { " Do you want to proceed?", " Esc to cancel - Tab to amend" }, "Gemini");

        Assert.True(reading.Drawn);
        Assert.False(reading.Calibrated);
        Assert.Contains("UNCALIBRATED", reading.Describe());
    }

    // ================================================================= what the judge is still asked
    //
    // THE PROMPT IS UNCHANGED BY THIS WORK, AND STILL CARRIES A FALSE LINE (owner ruling, 2026-09-20). It labels the
    // terminal's alternate screen - a display mode - "Full-screen picker showing". Measured on the corpus, that label
    // said yes on 29 of the 32 screens where the judge invents a menu and on 1 of the 28 real pickers, and when it said
    // yes on a screen with no picker the judge invented one 17 times in 30, against 2 in 27 when it said no. About a
    // third of all stops run in that mode, so it is the largest single cause of the invented menus.
    //
    // IT WAS LEFT IN DELIBERATELY. Removing it, and telling the judge this class's answer instead, was built and
    // measured: menus went to zero in the model's own output and agreement with the labelled corpus rose from 68.9% to
    // 71.1% - but stops that NEEDED THE OWNER and were called calm went from 5 to 9 in 60. The false line was
    // accidentally useful: wrong about pickers, right that somebody was waiting. Losing a stop the owner had to answer
    // is the failure this whole feature exists to avoid, so the prompt keeps its line and this class binds the record
    // instead, where it costs nothing.
    //
    // The change is kept whole, with its measurements, at docs/missions/wingman-picker-flag-2026-09-19/ in the private
    // repository (HELD-prompt-change.patch). Reviving it needs the 5-against-9 settled over repeated runs first.
}
