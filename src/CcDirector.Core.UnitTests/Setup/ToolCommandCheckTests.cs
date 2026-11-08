using System.Collections.Concurrent;
using CcDirector.Core.Setup;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Setup;

/// <summary>
/// Issue #4744: a session whose instructions use a cc-devthrottle command the machine's tools lack is refused, naming the
/// command, and every way the question cannot be answered is a refusal too - never "probably fine". The tool is faked
/// here by its answers, exactly as the real command line gives them: exit 0 for a command it has, exit 2 with
/// "Error: No such command 'inbox'." for one it lacks.
/// </summary>
public sealed class ToolCommandCheckTests
{
    private const string Exe = @"C:\machine\pyenv\Scripts\cc-devthrottle.exe";

    private readonly ConcurrentBag<string> _asked = new();

    /// <summary>Tools that have every command except those under <paramref name="missingGroup"/>.</summary>
    private ToolCommandCheck ToolsWithout(string missingGroup, string missingWord) => new(() => Exe, (exe, args, _) =>
    {
        Assert.Equal(Exe, exe);
        Assert.Equal("--help", args[^1]);
        var command = string.Join(' ', args.Take(args.Count - 1));
        _asked.Add(command);
        return Task.FromResult(command.StartsWith(missingGroup + " " + missingWord, StringComparison.Ordinal)
            ? new ProcessRunner.Result(2, "", $"Error: No such command '{missingWord}'.\nUsage: cc-devthrottle {missingGroup} [OPTIONS] COMMAND [ARGS]...", true)
            : new ProcessRunner.Result(0, "Usage: ...", "", true));
    });

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ToolsLackTheFactoryInbox_RefusesNamingTheCommandAndTheToolsOwnWords()
    {
        // The 7 November 2026 machine: tools that have "factory" but no "factory inbox".
        var check = ToolsWithout("factory", "inbox");

        var refusal = await check.WhyTheToolsRefuseAsync(["session done", "factory inbox new", "factory memory set"]);

        Assert.NotNull(refusal);
        Assert.Contains("'cc-devthrottle factory inbox new'", refusal);
        Assert.Contains("Error: No such command 'inbox'.", refusal);
        Assert.Contains(Exe, refusal);
        Assert.Contains("no session was started", refusal);
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ARefusalArrives_IsAnsweredAtOnce_AndTheSlowerQuestionsAreWithdrawn()
    {
        // A refusal already known is never held back by another command that has not answered yet.
        var withdrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new ToolCommandCheck(() => Exe, async (_, args, ct) =>
        {
            if (args[0] == "factory")
                return new ProcessRunner.Result(2, "", "Error: No such command 'inbox'.", true);
            using var registration = ct.Register(() => withdrawn.TrySetResult());
            await withdrawn.Task;
            ct.ThrowIfCancellationRequested();
            return new ProcessRunner.Result(0, "", "", true);
        }, timeout: TimeSpan.FromHours(1));

        var refusal = await check.WhyTheToolsRefuseAsync(["session done", "factory inbox new"]);

        Assert.NotNull(refusal);
        Assert.Contains("'cc-devthrottle factory inbox new'", refusal);
        Assert.True(withdrawn.Task.IsCompleted);
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ToolsHaveEveryCommand_AnswersNull_AfterAskingForEachOne()
    {
        var check = ToolsWithout("nothing", "missing");

        var refusal = await check.WhyTheToolsRefuseAsync(["factory inbox new", "factory inbox bundle", "session done"]);

        Assert.Null(refusal);
        Assert.Equal(new[] { "factory inbox bundle", "factory inbox new", "session done" }, _asked.OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_NoCommands_AsksNothing()
    {
        var check = new ToolCommandCheck(() => throw new InvalidOperationException("must not be asked"),
            (_, _, _) => throw new InvalidOperationException("must not be run"));

        Assert.Null(await check.WhyTheToolsRefuseAsync([]));
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ToolNotInstalled_Refuses()
    {
        var check = new ToolCommandCheck(() => null, (_, _, _) => throw new InvalidOperationException("must not be run"));

        var refusal = await check.WhyTheToolsRefuseAsync(["factory inbox new"]);

        Assert.NotNull(refusal);
        Assert.Contains("not installed", refusal);
        Assert.Contains("'cc-devthrottle factory inbox new'", refusal);
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ToolDoesNotAnswerInTime_RefusesRatherThanAssumingItHasTheCommand()
    {
        var check = new ToolCommandCheck(() => Exe, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessRunner.Result(0, "", "", true);
        }, timeout: TimeSpan.FromMilliseconds(50));

        var refusal = await check.WhyTheToolsRefuseAsync(["factory inbox new"]);

        Assert.NotNull(refusal);
        Assert.Contains("did not answer within", refusal);
    }

    [Fact]
    public async Task WhyTheToolsRefuseAsync_ToolCannotStart_Refuses()
    {
        var check = new ToolCommandCheck(() => Exe, (_, _, _) => Task.FromResult(new ProcessRunner.Result(-1, "", "", false, 2)));

        var refusal = await check.WhyTheToolsRefuseAsync(["factory inbox new"]);

        Assert.NotNull(refusal);
        Assert.Contains("could not be started (error 2)", refusal);
    }

    [Theory]
    [InlineData("factory inbox; rm -rf")]
    [InlineData("--version")]
    [InlineData("  ")]
    public async Task WhyTheToolsRefuseAsync_NotACommandName_RefusesWithoutRunningAnything(string command)
    {
        var check = new ToolCommandCheck(() => Exe, (_, _, _) => throw new InvalidOperationException("must not be run"));

        var refusal = await check.WhyTheToolsRefuseAsync([command]);

        Assert.NotNull(refusal);
        Assert.Contains("not a command name", refusal);
    }
}
