using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Every unobserved task exception reaches the error store ONCE (issue #3749). Program and App both subscribed to
/// <see cref="TaskScheduler.UnobservedTaskException"/>, each logged the exception, and every event arrived as two
/// reports - "[Program] UNOBSERVED TASK" and "[App] UNOBSERVED TASK EXCEPTION" - doubling the count the owner reads.
/// The subscriptions run inside Program.Main and App start-up, which a test cannot call, so this reads the
/// Director's own source: exactly one subscription, and it is Program's, made before Avalonia starts.
/// </summary>
public sealed class UnobservedTaskReportedOnceTests
{
    [Fact]
    public void TheDirectorSubscribesToUnobservedTaskExceptionsOnce_InProgram()
    {
        var project = Path.Combine(TestRepoRoot.Path, "src", "CcDirector.Avalonia");
        var subscription = new Regex(@"UnobservedTaskException\s*\+=");
        var separator = Path.DirectorySeparatorChar;

        var subscribers = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}") && !f.Contains($"{separator}obj{separator}"))
            .SelectMany(f => subscription.Matches(File.ReadAllText(f)).Select(_ => Path.GetRelativePath(project, f)))
            .ToList();

        Assert.Equal(new[] { "Program.cs" }, subscribers);
    }
}
