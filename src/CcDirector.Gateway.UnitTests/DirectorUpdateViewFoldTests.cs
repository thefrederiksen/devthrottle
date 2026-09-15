using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The update answer's wording (fleet maintenance, devthrottle_internal#2021 and #2022). A person pressed a button
/// on a machine they cannot see; the sentence is the only evidence they get of what happened there.
/// </summary>
public sealed class DirectorUpdateViewFoldTests
{
    [Fact]
    public void Sentence_EveryKnownDecision_HasItsOwnWords()
    {
        var sentences = LauncherDirectorUpdateReport.KnownDecisions
            .Select(d => DirectorUpdateViewFold.Sentence(d, "2.1.4", null).Text)
            .ToList();

        Assert.All(sentences, s => Assert.DoesNotContain("does not recognise", s));
        Assert.Equal(sentences.Count, sentences.Distinct().Count());
    }

    [Fact]
    public void Sentence_UnknownDecision_SaysItIsNotRecognised()
    {
        var (text, tone) = DirectorUpdateViewFold.Sentence("HeldBecauseTheMoonIsFull", "2.1.4", null);

        Assert.Contains("does not recognise", text);
        Assert.Contains("HeldBecauseTheMoonIsFull", text);
        Assert.Equal(FleetTone.Bad, tone);
    }

    [Fact]
    public void Fold_PassStillInstalling_IsInProgressAndNamesTheVersion()
    {
        var view = DirectorUpdateViewFold.Fold("LAPTOP", new LauncherDirectorUpdateReport
        {
            PassInProgress = true,
            InstallingVersion = "2.1.4",
        });

        Assert.True(view.InProgress);
        Assert.Contains("Installing 2.1.4", view.Headline);
    }

    [Fact]
    public void Fold_FinishedApplied_SaysUpdatedToTheRecordedVersion()
    {
        var at = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);
        var view = DirectorUpdateViewFold.Fold("LAPTOP", new LauncherDirectorUpdateReport
        {
            Finished = true,
            Decision = "Applied",
            LastDecision = "Applied",
            LastVersion = "2.1.4",
            LastDecisionAt = at,
        });

        Assert.False(view.InProgress);
        Assert.Equal("Updated to 2.1.4.", view.Headline);
        Assert.Equal(FleetTone.Ok, view.Tone);
        Assert.Equal(at, view.LastResultAt);
    }

    [Fact]
    public void Fold_FinishedNothingStaged_DoesNotBorrowAnOlderRecordsDetail()
    {
        var view = DirectorUpdateViewFold.Fold("LAPTOP", new LauncherDirectorUpdateReport
        {
            Finished = true,
            Decision = "NothingStaged",
            LastDecision = "RolledBack",
            LastVersion = "2.1.2",
            LastDetail = "2.1.2 never answered",
        });

        Assert.StartsWith("Nothing to install", view.Headline);
        Assert.DoesNotContain("2.1.2", view.Headline);
    }

    [Fact]
    public void Fold_StagedBuild_IsReportedAsWaiting()
    {
        var view = DirectorUpdateViewFold.Fold("LAPTOP", new LauncherDirectorUpdateReport { StagedVersion = "2.1.4" });

        Assert.Equal("2.1.4, waiting to install", view.Downloaded);
        Assert.Equal("No update has been installed by this launcher yet.", view.Headline);
    }

    [Fact]
    public void Fold_StatusWithAnEarlierRecord_PrefixesLastUpdate()
    {
        var view = DirectorUpdateViewFold.Fold("LAPTOP", new LauncherDirectorUpdateReport
        {
            LastDecision = "RolledBack",
            LastVersion = "2.1.4",
        });

        Assert.StartsWith("Last update: 2.1.4 did not start", view.Headline);
        Assert.Equal(FleetTone.Bad, view.Tone);
    }
}
