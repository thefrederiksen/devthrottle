using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

// =====================================================================================
// FileLog's hosted seams (issue #2203). A hosted deploy runs TWO Gateway containers at
// once against one storage mount, and the startup record of the container that fails is
// exactly what we need. Two guarantees are pinned here:
//   1. MirrorToConsole puts every line on standard output as well - the sink the
//      container platform captures per container, and the one with no queue in front of
//      it, so it survives the file-share stall that erased three startup records.
//   2. UseUniqueInstanceId installs the caller-selected directory before Start(), and
//      REFUSES to run after Start() instead of splitting one process record across files.
// =====================================================================================
[Collection("ProcessWideState")]
public sealed class FileLogConsoleMirrorTests
{
    [Fact]
    public void Write_MirrorToConsoleOn_AlsoWritesTheLineToStandardOutput()
    {
        var originalOut = Console.Out;
        var captured = new StringWriter();
        var wasMirroring = FileLog.MirrorToConsole;

        using var scope = FileLog.RedirectForTests();
        try
        {
            Console.SetOut(captured);
            FileLog.MirrorToConsole = true;

            FileLog.Write("[Program] CC Director Gateway starting");

            Assert.Contains("[Program] CC Director Gateway starting", captured.ToString());
        }
        finally
        {
            FileLog.MirrorToConsole = wasMirroring;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Write_MirrorToConsoleOff_KeepsStandardOutputClean()
    {
        // The mirror is switched off once the Gateway binds, because copying the running log into the
        // platform's log mount is a different outage. Off must mean silent.
        var originalOut = Console.Out;
        var captured = new StringWriter();
        var wasMirroring = FileLog.MirrorToConsole;

        using var scope = FileLog.RedirectForTests();
        try
        {
            Console.SetOut(captured);
            FileLog.MirrorToConsole = false;

            FileLog.Write("[GatewayHost] a routine line long after startup");

            Assert.DoesNotContain("a routine line long after startup", captured.ToString());
        }
        finally
        {
            FileLog.MirrorToConsole = wasMirroring;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void UseUniqueInstanceId_AfterStart_ThrowsRatherThanSplittingTheRecord()
    {
        // Inside the scope the writer is running. Moving the file now would split this process's record
        // across two files, so this must fail loudly rather than half-apply.
        using var scope = FileLog.RedirectForTests();

        var ex = Assert.Throws<InvalidOperationException>(FileLog.UseUniqueInstanceId);
        Assert.Contains("before FileLog.Start()", ex.Message);
    }

    [Fact]
    public void UseUniqueInstanceId_WithDirectory_WritesToTheSelectedDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-filelog-selected-" + Guid.NewGuid().ToString("N"));
        try
        {
            FileLog.UseUniqueInstanceId(dir);
            FileLog.Start();
            FileLog.Write("[FileLog] selected directory proof");
            FileLog.Stop();

            var file = Assert.Single(Directory.GetFiles(dir, "director-*.log"));
            Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(Path.GetFullPath(file)));
            Assert.Contains("selected directory proof", File.ReadAllText(file));
        }
        finally
        {
            FileLog.Stop();
            FileLog.UseUniqueInstanceId();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
