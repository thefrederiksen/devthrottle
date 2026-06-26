using CcDirector.Core.Pi;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

public class PiPreambleWriterTests
{
    [Fact]
    public void WriteForSession_WritesPreambleFile_WithIdentityAndCommands()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-preamble-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sid = "abc12345-1111-2222-3333-444455556666";
            var path = PiPreambleWriter.WriteForSession(sid, "myrepo", "SOREN_NORTH", @"D:\repo\myrepo", dir);

            Assert.True(File.Exists(path));
            Assert.Equal(Path.Combine(dir, sid + ".txt"), path);

            var text = File.ReadAllText(path);
            Assert.Contains("abc12345", text);   // short id present
            Assert.Contains("myrepo", text);      // name present
            Assert.Contains("cc-sessions", text); // the fleet commands present
            Assert.Contains("cc-send", text);
            Assert.Contains("cc-ask", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WriteForSession_UnnamedSession_StillWritesUsableFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-preamble-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = PiPreambleWriter.WriteForSession("603b2066-aaaa-bbbb-cccc-ddddeeeeffff", null, "SOREN_NORTH", @"D:\repo", dir);
            var text = File.ReadAllText(path);
            Assert.Contains("(unnamed)", text);
            Assert.Contains("603b2066", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
