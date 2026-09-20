using System.Diagnostics;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Unit tests for the five verified primitives (owner ruling 15, Architect ruling A3). These are the only
/// checks a rule may run, so they get the tests they deserve ONCE, here, rather than each generated
/// variant going unreviewed - which is the whole argument for shipping primitives instead of code.
///
/// <c>is_path_inside</c> carries the three cases the acceptance names: a <c>..</c> that walks out, a LINK
/// that points out, and a PREFIX COLLISION (<c>repo-other</c> is not inside <c>repo</c>).
/// </summary>
public sealed class RulePrimitivesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-rule-primitives-" + Guid.NewGuid().ToString("N"));

    public RulePrimitivesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort - the OS may hold a reparse point briefly */ }
    }

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    // ---- is_path_inside -------------------------------------------------------------------------

    [Fact]
    public void IsPathInside_answers_true_for_a_path_under_the_root()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Assert.True(RulePrimitives.IsPathInside(Path.Combine(root, "src", "file.cs"), root));
    }

    [Fact]
    public void IsPathInside_answers_true_for_the_root_itself()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(root);
        Assert.True(RulePrimitives.IsPathInside(root, root));
    }

    [Fact]
    public void IsPathInside_resolves_dot_dot_and_refuses_a_path_that_walks_out()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "secrets"));

        var walksOut = Path.Combine(root, "src", "..", "..", "secrets", "key.txt");
        Assert.False(RulePrimitives.IsPathInside(walksOut, root));
    }

    [Fact]
    public void IsPathInside_keeps_a_dot_dot_that_stays_inside()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var staysIn = Path.Combine(root, "src", "..", "README.md");
        Assert.True(RulePrimitives.IsPathInside(staysIn, root));
    }

    [Fact]
    public void IsPathInside_refuses_a_prefix_collision()
    {
        var root = Path.Combine(_dir, "repo");
        var neighbour = Path.Combine(_dir, "repo-other");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(neighbour);

        // "repo-other" starts with "repo". A string prefix test would call this inside; it is not.
        Assert.False(RulePrimitives.IsPathInside(Path.Combine(neighbour, "file.cs"), root));
        Assert.False(RulePrimitives.IsPathInside(neighbour, root));
    }

    [Fact]
    public void IsPathInside_follows_a_link_that_points_outside_the_root()
    {
        var root = Path.Combine(_dir, "repo");
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "key.txt"), "secret");

        var link = Path.Combine(root, "escape");
        CreateDirectoryLink(link, outside);

        // The instrument first: the link must EXIST and RESOLVE, or the assertion below proves nothing.
        Assert.True(Directory.Exists(link), "link was not created at " + link);
        var resolved = Directory.ResolveLinkTarget(link, returnFinalTarget: true);
        Assert.True(resolved is not null, link + " was created but is not a link");

        Assert.False(RulePrimitives.IsPathInside(Path.Combine(link, "key.txt"), root));
    }

    [Fact]
    public void IsPathInside_follows_a_link_that_stays_inside_the_root()
    {
        var root = Path.Combine(_dir, "repo");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);

        var link = Path.Combine(root, "alias");
        CreateDirectoryLink(link, real);
        Assert.True(Directory.ResolveLinkTarget(link, returnFinalTarget: true) is not null,
            link + " was created but is not a link");

        Assert.True(RulePrimitives.IsPathInside(Path.Combine(link, "file.cs"), root));
    }

    /// <summary>
    /// THE MACOS AND LINUX DEFECT, BUILT BY HAND SO IT DOES NOT DEPEND ON WHERE THE TEMPORARY DIRECTORY IS.
    ///
    /// The test above found this by accident, because on macOS the temporary directory sits under
    /// <c>/var</c>, which is a link to <c>/private/var</c>. This one builds the same shape deliberately: a
    /// link whose TARGET is written through a linked ancestor. The operating system call behind
    /// <c>ResolveLinkTarget</c> on macOS and Linux follows the link chain but leaves the target's own
    /// directories as written, so the containment check used to compare one fully-resolved path against one
    /// half-resolved one and report a file plainly inside the repository as outside it.
    ///
    /// It is stated here as its own case because the accident is not a specification. Windows resolves the
    /// whole path inside its own call and never showed this, and the hosted Gateway - where rules actually
    /// run - is Linux.
    /// </summary>
    [Fact]
    public void IsPathInside_follows_a_link_whose_target_is_written_through_a_linked_ancestor()
    {
        // <dir>/actual is the real place; <dir>/gateway is a link to it.
        var actual = Path.Combine(_dir, "actual");
        var root = Path.Combine(actual, "repo");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);

        var ancestorLink = Path.Combine(_dir, "gateway");
        CreateDirectoryLink(ancestorLink, actual);

        // <dir>/actual/repo/alias points at the same "real" directory, but NAMED THROUGH the link.
        var alias = Path.Combine(root, "alias");
        CreateDirectoryLink(alias, Path.Combine(ancestorLink, "repo", "real"));

        // The instrument first: both links must exist and resolve, or the assertion below proves nothing.
        Assert.True(Directory.ResolveLinkTarget(ancestorLink, returnFinalTarget: true) is not null,
            ancestorLink + " was created but is not a link");
        Assert.True(Directory.ResolveLinkTarget(alias, returnFinalTarget: true) is not null,
            alias + " was created but is not a link");

        Assert.True(RulePrimitives.IsPathInside(Path.Combine(alias, "file.cs"), root));
    }

    [Fact]
    public void IsPathInside_answers_false_for_missing_arguments()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(root);
        Assert.False(RulePrimitives.IsPathInside("", root));
        Assert.False(RulePrimitives.IsPathInside(Path.Combine(root, "a.txt"), "   "));
    }

    /// <summary>Create a directory link, failing LOUDLY if the machine will not make one. A skipped link
    /// case would leave the acceptance row passing over a case that never ran.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A Windows SYMBOLIC link needs administrator rights; a JUNCTION is the same reparse point for
        // this purpose and needs none, and Directory.ResolveLinkTarget resolves both.
        var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "could not create a junction at " + link + " -> " + target +
                ": exit " + process.ExitCode + " " + stdout + " " + stderr);
    }

    // ---- matches_any ----------------------------------------------------------------------------

    [Fact]
    public void MatchesAny_finds_a_term_anywhere_in_the_text_ignoring_case()
    {
        var terms = new[] { "usage limit", "out of credits" };
        Assert.True(RulePrimitives.MatchesAny("Claude Usage Limit reached. Try again later.", terms));
        Assert.True(RulePrimitives.MatchesAny("you are OUT OF CREDITS", terms));
    }

    [Fact]
    public void MatchesAny_answers_false_when_no_term_appears()
    {
        Assert.False(RulePrimitives.MatchesAny("all good here", new[] { "usage limit" }));
    }

    [Fact]
    public void MatchesAny_answers_false_for_an_empty_term_list_or_empty_text()
    {
        Assert.False(RulePrimitives.MatchesAny("usage limit", Array.Empty<string>()));
        Assert.False(RulePrimitives.MatchesAny("", new[] { "usage limit" }));
    }

    [Fact]
    public void MatchesAny_treats_terms_as_literal_text_never_as_a_pattern()
    {
        // ".*" is two characters, not "anything". A primitive that took patterns would be an interpreter.
        Assert.False(RulePrimitives.MatchesAny("anything at all", new[] { ".*" }));
        Assert.True(RulePrimitives.MatchesAny("exit code .* here", new[] { ".*" }));
    }

    // ---- elapsed_since --------------------------------------------------------------------------

    [Fact]
    public void ElapsedSince_measures_seconds_between_the_two_moments()
    {
        Assert.Equal(3600, RulePrimitives.ElapsedSince(Now.AddHours(-1), Now));
        Assert.Equal(0, RulePrimitives.ElapsedSince(Now, Now));
    }

    [Fact]
    public void ElapsedSince_reports_a_negative_span_rather_than_hiding_a_future_moment()
    {
        Assert.Equal(-60, RulePrimitives.ElapsedSince(Now.AddMinutes(1), Now));
    }

    [Fact]
    public void ElapsedSince_compares_in_utc_whatever_kind_it_is_handed()
    {
        var localFirst = Now.AddHours(-2).ToLocalTime();
        Assert.Equal(7200, RulePrimitives.ElapsedSince(localFirst, Now), 3);
    }

    // ---- retry_delay_from -----------------------------------------------------------------------

    [Theory]
    [InlineData("Rate limited. Try again in 30 seconds.", 30)]
    [InlineData("overloaded_error - retry after 5 minutes", 300)]
    [InlineData("Service unavailable, try again in 2 hours", 7200)]
    [InlineData("try again in 1 minute", 60)]
    public void RetryDelayFrom_reads_a_relative_wait_off_the_screen(string screen, double expected)
    {
        Assert.Equal(expected, RulePrimitives.RetryDelayFrom(screen, Now));
    }

    [Fact]
    public void RetryDelayFrom_reads_a_clock_time_later_today_against_now()
    {
        // now is 12:00 UTC; 14:30 is two and a half hours away.
        Assert.Equal(9000, RulePrimitives.RetryDelayFrom("Your limit will reset at 14:30", Now));
    }

    [Fact]
    public void RetryDelayFrom_rolls_a_clock_time_already_past_to_tomorrow()
    {
        // now is 12:00 UTC; 09:00 has gone, so the next 09:00 is 21 hours away.
        Assert.Equal(75600, RulePrimitives.RetryDelayFrom("resets at 09:00", Now));
    }

    [Fact]
    public void RetryDelayFrom_answers_nothing_when_the_screen_says_nothing_about_waiting()
    {
        Assert.Null(RulePrimitives.RetryDelayFrom("Everything is fine.", Now));
        Assert.Null(RulePrimitives.RetryDelayFrom("", Now));
    }

    // ---- extract_first --------------------------------------------------------------------------

    [Fact]
    public void ExtractFirst_pulls_the_first_path_out_of_the_screen()
    {
        var screen = "Do you want to edit D:\\ReposFred\\devthrottle\\src\\file.cs ? (y/n)";
        Assert.Equal("D:\\ReposFred\\devthrottle\\src\\file.cs",
            RulePrimitives.ExtractFirst(screen, RuleExtractKind.Path));
    }

    [Fact]
    public void ExtractFirst_pulls_a_posix_path_too()
    {
        Assert.Equal("/home/soren/repo/file.cs",
            RulePrimitives.ExtractFirst("writing to /home/soren/repo/file.cs now", RuleExtractKind.Path));
    }

    [Fact]
    public void ExtractFirst_pulls_the_first_duration_out_of_the_screen()
    {
        Assert.Equal("5 minutes",
            RulePrimitives.ExtractFirst("try again in 5 minutes, or 2 hours", RuleExtractKind.Duration));
    }

    [Fact]
    public void ExtractFirst_pulls_the_first_clock_time_out_of_the_screen()
    {
        Assert.Equal("09:44", RulePrimitives.ExtractFirst("last acted at 09:44 today", RuleExtractKind.Timestamp));
    }

    [Fact]
    public void ExtractFirst_answers_an_empty_string_when_there_is_nothing_of_that_kind()
    {
        Assert.Equal("", RulePrimitives.ExtractFirst("nothing here at all", RuleExtractKind.Path));
        Assert.Equal("", RulePrimitives.ExtractFirst("", RuleExtractKind.Duration));
    }
}
