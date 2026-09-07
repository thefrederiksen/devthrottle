using CcDirector.Gateway.Contracts;
using CcDirector.Launcher;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// What a launcher declares about itself when it joins the Gateway's command stream - issue #2720.
///
/// WHY A LAUNCHER IS ASKED INSTEAD OF INSPECTED. Until now the only way to know what a launcher could
/// be told was to compare its version against the release that added the feature. That question is
/// answerable from outside, which is its whole appeal, and it is the fragile form: it breaks on a fork,
/// on a development build, on a build made by hand, and on any build whose version does not track the
/// feature - silently, because a version string always parses into something.
///
/// THE PROPERTY THESE TESTS EXIST TO HOLD. Over-declaring is the dangerous direction: a verb a launcher
/// claims and cannot perform is a promise a caller acts on. Under-declaring is safe - the capability
/// simply goes unused. So the declared list is not a description sitting beside the dispatch, it is the
/// GATE the dispatch runs, and these tests hold that seam shut.
/// </summary>
public sealed class LauncherDeclaredCapabilitiesTests
{
    /// <summary>
    /// A verb outside the declared set is refused BEFORE dispatch. This is what makes the declaration
    /// load-bearing rather than descriptive: the launcher cannot execute something it did not promise.
    /// </summary>
    [Fact]
    public void A_verb_that_is_not_declared_is_not_honoured()
    {
        Assert.False(LauncherDeclaredCapabilities.Honours("director/self-destruct"));
        Assert.False(LauncherDeclaredCapabilities.Honours(""));
        Assert.False(LauncherDeclaredCapabilities.Honours(null));
        Assert.False(LauncherDeclaredCapabilities.Honours("   "));
    }

    /// <summary>Every declared verb is honoured. The reconciliation runs the OTHER way too, below.</summary>
    [Theory]
    [InlineData(LauncherCapabilities.DirectorStart)]
    [InlineData(LauncherCapabilities.DirectorStop)]
    [InlineData(LauncherCapabilities.DirectorRestart)]
    [InlineData(LauncherCapabilities.Launch)]
    [InlineData(LauncherCapabilities.Apps)]
    [InlineData(LauncherCapabilities.Files)]
    public void Every_verb_this_build_declares_is_honoured(string verb)
    {
        Assert.Contains(verb, LauncherDeclaredCapabilities.Verbs);
        Assert.True(LauncherDeclaredCapabilities.Honours(verb));
    }

    /// <summary>
    /// The reconciliation in the other direction. The list above is written out by hand here on purpose:
    /// asserting <c>Verbs</c> against itself would agree with any change, including one that quietly
    /// added a verb this build cannot perform - which is the over-declaration that matters. Adding a verb
    /// to production must therefore also be a decision made in a test.
    /// </summary>
    [Fact]
    public void The_declared_verb_set_is_exactly_this_list_and_nothing_has_been_added_unnoticed()
    {
        Assert.Equal(
            new[]
            {
                "director/start", "director/stop", "director/restart", "launch", "apps", "files",
            },
            LauncherDeclaredCapabilities.Verbs);
    }

    /// <summary>
    /// A CONDITION IS NOT A VERB, and mixing them would open the dispatch gate to a token that names no
    /// action. "director/restart:only-if-empty" is a promise about how a restart BEHAVES; sent as a verb
    /// it is a caller error and must be refused like any other unknown verb.
    /// </summary>
    [Fact]
    public void A_condition_token_is_declared_but_is_never_accepted_as_a_verb()
    {
        Assert.DoesNotContain(LauncherCapabilities.DirectorRestartOnlyIfEmpty, LauncherDeclaredCapabilities.Verbs);
        Assert.False(LauncherDeclaredCapabilities.Honours(LauncherCapabilities.DirectorRestartOnlyIfEmpty));
    }

    /// <summary>
    /// THIS BUILD DOES NOT PROMISE A GUARDED RESTART, AND THAT IS THE HONEST ANSWER RATHER THAN A GAP.
    /// The onlyIfEmpty condition belongs to Phase 2 (#2721) and this build's dispatch does not honour it,
    /// so declaring it would be exactly the over-declaration this file exists to prevent - a promise a
    /// drain would act on, sending a guarded restart to a launcher that cannot see the guard.
    ///
    /// THIS TEST IS EXPECTED TO BE CHANGED, on the same commit that makes the dispatch honour the
    /// condition, and not before. It is written as an assertion rather than a comment so that adding the
    /// token is a deliberate act with a test to update, instead of a line somebody slips into a list.
    /// </summary>
    [Fact]
    public void This_build_declares_no_conditions_because_its_dispatch_honours_none()
    {
        Assert.Empty(LauncherDeclaredCapabilities.Conditions);
    }

    /// <summary>Matching is case-insensitive: a token is an identifier one process writes and another
    /// reads, and a verb refused over letter case would read as a launcher that cannot do the thing.</summary>
    [Fact]
    public void Verbs_are_matched_regardless_of_case()
    {
        Assert.True(LauncherDeclaredCapabilities.Honours("Director/Restart"));
        Assert.True(LauncherDeclaredCapabilities.Honours("APPS"));
    }

    /// <summary>
    /// The declaration a Hello carries: every verb plus every condition, and the three local facts.
    ///
    /// The root facts are asserted for SHAPE, not value - what they are depends on where this test
    /// process resolved its storage root, and pinning a value would pin the test rig rather than the
    /// behaviour. That the root key is present and that the instance-home question was answered at all
    /// is the part a Gateway depends on.
    /// </summary>
    [Fact]
    public void The_declaration_carries_every_verb_every_condition_and_the_local_facts()
    {
        var declaration = LauncherDeclaredCapabilities.Describe();

        Assert.Equal(
            LauncherDeclaredCapabilities.Verbs.Concat(LauncherDeclaredCapabilities.Conditions),
            declaration.Commands);

        Assert.False(string.IsNullOrWhiteSpace(declaration.ServingRootKey));
        Assert.NotNull(declaration.ServingRootIsInstanceHome);
    }

    /// <summary>
    /// THE SIGNAL IS ASKED ABOUT, NEVER RAISED, and describing a launcher must therefore be free of
    /// consequences. Raising it would restart a Director as a side effect of the question.
    ///
    /// Called repeatedly here for exactly that reason: a describe that had a side effect would show up as
    /// something happening on the tenth call. This test process arms no launcher signal, so the honest
    /// answer is "nothing is listening" on Windows and "not observable" elsewhere - and both are recorded
    /// rather than flattened, because a null is a different fact from a false.
    /// </summary>
    [Fact]
    public void Describing_the_launcher_asks_the_signal_and_never_raises_it()
    {
        for (var i = 0; i < 10; i++)
        {
            var declaration = LauncherDeclaredCapabilities.Describe();
            if (OperatingSystem.IsWindows())
                Assert.False(declaration.RestartSignalArmed);   // asked, and nothing is listening here
            else
                Assert.Null(declaration.RestartSignalArmed);    // this platform cannot be asked at all
        }
    }
}
