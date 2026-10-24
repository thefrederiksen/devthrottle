using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Tests for the first-run wizard state machine (issue #2101): gating on the completion marker
/// (including the migration guarantee that a machine with the OLD onboarding marker never sees the
/// new wizard), the marker write, the canonical step order and mandatory bookends, back/forward
/// navigation, the progress-dot states, and the skip rules - in particular that the Agents step is
/// unskippable when zero agents are found. Config-touching tests redirect CcStorage to a temp root
/// via CC_DIRECTOR_ROOT and are serialized with the other config-env tests.
/// </summary>
[Collection("ConfigEnvSerial")]
public class FirstRunWizardModelTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-firstrun-tests", Guid.NewGuid().ToString("N"));

    private static void WithRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<WizardStep> ShellSteps() => new[]
    {
        WizardStep.Welcome, WizardStep.Agents, WizardStep.Gateway, WizardStep.Done,
    };

    // --- Gating + marker (AC1, AC2) --------------------------------------------------------------

    [Fact]
    public void ShouldShow_FreshInstall_NoMarker_ReturnsTrue()
    {
        WithRoot(() => Assert.True(FirstRunWizardModel.ShouldShow()));
    }

    [Fact]
    public void ShouldShow_AfterMarkComplete_ReturnsFalse_AndMarkerIsSet()
    {
        WithRoot(() =>
        {
            Assert.True(FirstRunWizardModel.ShouldShow());

            FirstRunWizardModel.MarkComplete();

            Assert.False(FirstRunWizardModel.ShouldShow());
            Assert.True(OnboardingModel.IsOnboardingComplete());
        });
    }

    [Fact]
    public void ShouldShow_WhenOldOnboardingMarkerPresent_ReturnsFalse_Migration()
    {
        WithRoot(() =>
        {
            // A machine that finished the OLD onboarding carries onboarding.completed. The new wizard
            // shares that marker, so such a machine must never see the new wizard.
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["onboarding"] = new JsonObject { ["completed"] = true },
            });

            Assert.False(FirstRunWizardModel.ShouldShow());
        });
    }

    [Fact]
    public void MarkComplete_DoesNotClobberOtherConfigSections()
    {
        WithRoot(() =>
        {
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["gateway"] = new JsonObject { ["url"] = "http://gateway-host:7878" },
            });

            FirstRunWizardModel.MarkComplete();

            Assert.True(OnboardingModel.IsOnboardingComplete());
            Assert.Equal("http://gateway-host:7878", GatewayConfig.Load().Url);
        });
    }

    // --- Step list / order / bookends (AC3, AC6) -------------------------------------------------

    [Fact]
    public void Constructor_NormalisesToCanonicalOrder_RegardlessOfInputOrder()
    {
        // Even handed the steps out of order, the model presents them in the one canonical order.
        var model = new FirstRunWizardModel(new[]
        {
            WizardStep.Done, WizardStep.Gateway, WizardStep.Welcome, WizardStep.Agents,
        });

        Assert.Equal(
            new[] { WizardStep.Welcome, WizardStep.Gateway, WizardStep.Agents, WizardStep.Done },
            model.Steps);
    }

    [Fact]
    public void Constructor_DeduplicatesRepeatedSteps()
    {
        var model = new FirstRunWizardModel(new[]
        {
            WizardStep.Welcome, WizardStep.Welcome, WizardStep.Done, WizardStep.Done,
        });

        Assert.Equal(new[] { WizardStep.Welcome, WizardStep.Done }, model.Steps);
    }

    [Fact]
    public void CreateFull_HasAllNineStepsInCanonicalOrder()
    {
        var model = FirstRunWizardModel.CreateFull();
        Assert.Equal(9, model.Count);

        // The WHOLE sequence, written out, not compared against the production list. Comparing
        // model.Steps to CanonicalOrder only proves the model did not reorder its own input - it
        // passes for any order production happens to declare, so it cannot catch an ordering change.
        // Spot checks on three indexes were not enough either: swapping Agents and Code kept every one
        // of them true. This is the assertion that fails when the journey changes.
        Assert.Equal(
            new[]
            {
                WizardStep.Welcome,     // the invitation
                WizardStep.Gateway,     // who you are, before anything about this machine
                WizardStep.Agents,      // your workforce
                WizardStep.Tools,       // their toolbelt
                WizardStep.Code,        // what we watch over
                WizardStep.GitHub,      // where that code is pushed (issue #4426)
                WizardStep.Screenshots, // show, don't type
                WizardStep.Browsers,    // give an agent a signed-in browser
                WizardStep.Done,        // the receipt
            },
            model.Steps);
        Assert.Equal(FirstRunWizardModel.CanonicalOrder, model.Steps);

        // The gateway is the FIRST thing asked after the welcome: connecting is who you are, and every
        // step after it configures one particular machine. Identity leads, machine questions follow.
        Assert.Equal(WizardStep.Gateway, model.Steps[1]);

        // Tools sits right after Agents: workforce first, then their toolbelt.
        Assert.Equal(WizardStep.Tools, model.Steps[3]);

        // Browsers is the last step before Done, and it sits immediately after Screenshots (issue
        // #1012): both are about how an agent gets context from you, and Browsers is the one step that
        // can ask for an interactive sign-in, so it goes after everything else has been delivered.
        // Done must never be preceded by a daily-report frequency question again - that is an ACCOUNT
        // setting, so it was asked once per machine with no way to reconcile the answers (issue #996).
        // The count above is also the number of progress dots, so a step creeping back in is a failing
        // assertion rather than a silently longer wizard.
        Assert.Equal(WizardStep.Browsers, model.Steps[^2]);
        Assert.Equal(WizardStep.Screenshots, model.Steps[^3]);
    }

    [Fact]
    public void CanonicalOrder_HasNoStepAskingForAnAccountLevelSetting()
    {
        // The guard for issue #996, stated as the rule rather than as one banned name: the wizard
        // runs per director per machine, so every step it presents must be answerable about THIS
        // machine. Enum.GetValues is the source, so adding a step to WizardStep without adding it
        // to this list fails here and forces the question to be asked deliberately.
        var machineScoped = new[]
        {
            WizardStep.Welcome, WizardStep.Agents, WizardStep.Tools, WizardStep.Code,
            // gh signs in per machine: the account it signs in to is the person's, but the sign-in is this machine's.
            WizardStep.GitHub,
            WizardStep.Screenshots, WizardStep.Browsers, WizardStep.Gateway, WizardStep.Done,
        };

        // The SET is what this rule is about - every declared step must be one a machine can answer.
        // Presentation order is a separate decision (the gateway now leads), so the two are compared
        // as sets here rather than as sequences; the exact order is asserted in the test above.
        Assert.Equal(machineScoped.OrderBy(s => s), Enum.GetValues<WizardStep>().OrderBy(s => s));
        Assert.Equal(machineScoped.OrderBy(s => s), FirstRunWizardModel.CanonicalOrder.OrderBy(s => s));
    }

    [Fact]
    public void Constructor_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FirstRunWizardModel(Array.Empty<WizardStep>()));
    }

    [Fact]
    public void Constructor_MissingWelcomeBookend_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FirstRunWizardModel(new[] { WizardStep.Agents, WizardStep.Done }));
    }

    [Fact]
    public void Constructor_MissingDoneBookend_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new FirstRunWizardModel(new[] { WizardStep.Welcome, WizardStep.Agents }));
    }

    // --- Navigation (AC3) ------------------------------------------------------------------------

    [Fact]
    public void Navigation_StartsOnWelcome_MovesForwardAndBackAcrossPresentSteps()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        Assert.Equal(WizardStep.Welcome, model.Current);
        Assert.True(model.IsFirst);
        Assert.False(model.IsLast);

        // Canonical order puts the gateway before the agents, whatever order the present-step list
        // was given in.
        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Gateway, model.Current);
        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Agents, model.Current);
        Assert.True(model.MoveNext());
        Assert.Equal(WizardStep.Done, model.Current);
        Assert.True(model.IsLast);

        // Cannot move past Done.
        Assert.False(model.MoveNext());
        Assert.Equal(WizardStep.Done, model.Current);

        // Back walks the same present steps in reverse.
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Agents, model.Current);
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Gateway, model.Current);
        Assert.True(model.MoveBack());
        Assert.Equal(WizardStep.Welcome, model.Current);

        // Cannot move back before Welcome.
        Assert.False(model.MoveBack());
        Assert.Equal(WizardStep.Welcome, model.Current);
    }

    [Fact]
    public void GoTo_PresentStep_Moves_AbsentStep_Throws()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.GoTo(WizardStep.Gateway);
        Assert.Equal(WizardStep.Gateway, model.Current);

        // Code is not part of this present-step list.
        Assert.Throws<ArgumentException>(() => model.GoTo(WizardStep.Code));
    }

    // --- Progress dots (AC3) ---------------------------------------------------------------------

    [Fact]
    public void DotState_ReflectsPosition_PastCurrentUpcoming()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.GoTo(WizardStep.Agents); // index 2 of 4 (Welcome, Gateway, Agents, Done)

        Assert.Equal(WizardDotState.Past, model.DotStateAt(0));
        Assert.Equal(WizardDotState.Past, model.DotStateAt(1));
        Assert.Equal(WizardDotState.Current, model.DotStateAt(2));
        Assert.Equal(WizardDotState.Upcoming, model.DotStateAt(3));
    }

    [Fact]
    public void DotState_PositionOutOfRange_Throws()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        Assert.Throws<ArgumentOutOfRangeException>(() => model.DotStateAt(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.DotStateAt(-1));
    }

    // --- Skip rules (AC4, AC6) -------------------------------------------------------------------

    [Fact]
    public void Welcome_CarriesTheWholeWizardSkip()
    {
        Assert.True(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Welcome));
        Assert.False(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Agents));
        Assert.False(FirstRunWizardModel.IsWholeWizardSkip(WizardStep.Done));
    }

    [Fact]
    public void EveryStep_SkippableByDefault_BeforeAnAgentScan()
    {
        var model = new FirstRunWizardModel(FirstRunWizardModel.CanonicalOrder);
        foreach (var step in model.Steps)
            Assert.True(model.CanSkipStep(step));
    }

    [Fact]
    public void AgentsStep_IsUnskippable_WhenZeroAgentsFound()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.SetAgentsFound(false);
        Assert.False(model.AgentsFound);
        Assert.False(model.CanSkipStep(WizardStep.Agents));

        // Only the Agents step is blocked; every other step stays skippable.
        Assert.True(model.CanSkipStep(WizardStep.Gateway));
        Assert.True(model.CanSkipStep(WizardStep.Welcome));
    }

    [Fact]
    public void AgentsStep_BecomesSkippable_OnceAnAgentIsFound()
    {
        var model = new FirstRunWizardModel(ShellSteps());

        model.SetAgentsFound(false);
        Assert.False(model.CanSkipStep(WizardStep.Agents));

        model.SetAgentsFound(true);
        Assert.True(model.CanSkipStep(WizardStep.Agents));
    }

    [Fact]
    public void CanSkipCurrent_TracksTheCurrentStepAndTheAgentsFlag()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);

        model.GoTo(WizardStep.Welcome);
        Assert.True(model.CanSkipCurrent);

        model.GoTo(WizardStep.Agents);
        Assert.False(model.CanSkipCurrent);
    }

    [Fact]
    public void DeferAgents_RecordsTheCarriedToDo_WithoutMakingTheStepSkippable()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);

        Assert.False(model.AgentsDeferred);
        model.DeferAgents();

        // The deferral is a carried to-do, not a skip: the step's skip rule is unchanged.
        Assert.True(model.AgentsDeferred);
        Assert.False(model.CanSkipStep(WizardStep.Agents));
    }

    [Fact]
    public void DeferAgents_IsCleared_WhenAnAgentIsFoundLater()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(false);
        model.DeferAgents();
        Assert.True(model.AgentsDeferred);

        // A later successful scan (e.g. after an install) resolves the to-do.
        model.SetAgentsFound(true);
        Assert.False(model.AgentsDeferred);
    }

    [Fact]
    public void DeferAgents_WithAgentsAlreadyFound_ReportsNoPendingToDo()
    {
        var model = new FirstRunWizardModel(ShellSteps());
        model.SetAgentsFound(true);

        model.DeferAgents();
        Assert.False(model.AgentsDeferred);
    }
}
