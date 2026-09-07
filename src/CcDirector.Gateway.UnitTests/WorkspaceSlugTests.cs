using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The workspace id rule (issue #2722), and the one property that matters about it: whatever the desktop
/// MINTS from a name, the Gateway must ACCEPT. Those are two pieces of code at two ends of an HTTP call,
/// and if they disagree the failure is a name the user can type and the server will not take - which is
/// exactly the kind of defect that only shows up on somebody else's machine, with their names.
/// </summary>
public sealed class WorkspaceSlugTests
{
    [Theory]
    [InlineData("Morning fleet", "morning-fleet")]
    [InlineData("  Morning   Fleet  ", "morning-fleet")]
    [InlineData("DevThrottle_1 restart", "devthrottle1-restart")]
    [InlineData("Linux Support - Architect", "linux-support-architect")]
    [InlineData("2026-09-06 restart", "2026-09-06-restart")]
    public void A_name_becomes_the_slug_a_person_would_expect(string name, string expected)
        => Assert.Equal(expected, WorkspaceSlug.From(name));

    [Theory]
    [InlineData("Morning fleet")]
    [InlineData("!!!")]
    [InlineData("a")]
    [InlineData("-")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ein sehr langer Name der weit uber vierundsechzig Zeichen hinausgeht und immer weiter geht")]
    // Genuinely non-ASCII, not a label claiming to be: every character here is stripped by the slug rule,
    // so the name has nothing usable in it and must still produce a valid id.
    [InlineData("日本語の名前")]
    [InlineData("éèê")]
    public void Every_minted_slug_is_one_the_Gateway_accepts(string? name)
    {
        // The property, not a sample: whatever comes out of From must pass the validator, including for
        // names with nothing usable in them.
        var slug = WorkspaceSlug.From(name);
        Assert.True(WorkspaceSlug.IsValid(slug), $"'{name}' minted '{slug}', which the id rule rejects");
        WorkspaceValidation.ValidateId(slug);   // throws if the store would refuse it
    }

    [Fact]
    public void An_id_ending_in_a_line_break_is_not_valid()
    {
        // .NET's $ also matches BEFORE a final newline, so a $-anchored pattern would accept this - and
        // the id would then reach a primary key and every log line that prints it.
        Assert.False(WorkspaceSlug.IsValid("morning-fleet\n"));
        Assert.False(WorkspaceSlug.IsValid("morning-fleet\r\n"));
        Assert.True(WorkspaceSlug.IsValid("morning-fleet"));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Morning Fleet")]
    [InlineData("morning_fleet")]
    [InlineData("-morning")]
    [InlineData("morning fleet")]
    [InlineData("")]
    [InlineData(null)]
    public void An_id_that_is_not_a_slug_is_not_valid(string? id)
        => Assert.False(WorkspaceSlug.IsValid(id));

    [Fact]
    public void A_long_name_is_cut_to_the_limit_and_never_ends_on_a_dash()
    {
        var slug = WorkspaceSlug.From(new string('a', 40) + " " + new string('b', 40));
        Assert.True(slug.Length <= WorkspaceSlug.MaxLength);
        Assert.False(slug.EndsWith('-'));
        Assert.True(WorkspaceSlug.IsValid(slug));
    }
}
