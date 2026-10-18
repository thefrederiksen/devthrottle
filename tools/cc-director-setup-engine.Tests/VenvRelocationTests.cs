using System.Text;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The tools venv is built in a staging folder and renamed into place, so the absolute paths a venv writes into
/// itself have to be pointed at the final folder first. These tests hold what that rewrite must and must not do.
/// </summary>
public sealed class VenvRelocationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stagedVenv;
    private readonly string _finalVenv;
    private readonly string _stagedPython;
    private readonly string _finalPython;

    public VenvRelocationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-venvmove-" + Guid.NewGuid().ToString("N"));
        _stagedVenv = Path.Combine(_dir, "pyenv.staging-0123");
        _finalVenv = Path.Combine(_dir, "pyenv");
        _stagedPython = Path.Combine(_dir, "python.staging-0123");
        _finalPython = Path.Combine(_dir, "python");
        Directory.CreateDirectory(VenvRelocation.ScriptsDirOf(_stagedVenv));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string StagedScripts => VenvRelocation.ScriptsDirOf(_stagedVenv);
    private string StagedInterpreter => Path.Combine(StagedScripts, "python.exe");
    private string FinalInterpreter => Path.Combine(VenvRelocation.ScriptsDirOf(_finalVenv), "python.exe");

    /// <summary>The shape of a Windows console script: a launcher program, the interpreter line, then a zip archive.</summary>
    private static byte[] Launcher(string interpreter, byte[] stub, byte[] archive) =>
        stub.Concat(Encoding.UTF8.GetBytes($"#!{interpreter}\n")).Concat(archive).ToArray();

    private void WriteConfig() =>
        File.WriteAllText(Path.Combine(_stagedVenv, "pyvenv.cfg"),
            $"home = {_stagedPython}\r\nversion = 3.12.15\r\nexecutable = {Path.Combine(_stagedPython, "python.exe")}\r\n" +
            $"command = {Path.Combine(_stagedPython, "python.exe")} -m venv {_stagedVenv}\r\n");

    [Fact]
    public void Relocate_WindowsLauncher_RewritesOnlyTheInterpreterLineAndKeepsTheProgramAndArchive()
    {
        // The launcher reads the line just before the archive and finds the archive from its end, so the line may
        // change length - but the program in front of it and the archive behind it must come through byte for byte.
        var stub = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03 };
        var archive = Encoding.ASCII.GetBytes("PK\u0003\u0004 the zip archive with __main__.py");
        var script = Path.Combine(StagedScripts, "cc-devthrottle.exe");
        File.WriteAllBytes(script, Launcher(StagedInterpreter, stub, archive));
        WriteConfig();

        var ok = VenvRelocation.Relocate(_stagedVenv, _finalVenv, _stagedPython, _finalPython, new[] { script }, out var error);

        Assert.True(ok, error);
        Assert.Equal(Launcher(FinalInterpreter, stub, archive), File.ReadAllBytes(script));
    }

    [Fact]
    public void Relocate_Config_NamesTheFinalPythonAndVenvAndKeepsEverythingElse()
    {
        var script = Path.Combine(StagedScripts, "cc-pdf.exe");
        File.WriteAllBytes(script, Launcher(StagedInterpreter, new byte[] { 1 }, new byte[] { 2 }));
        WriteConfig();

        var ok = VenvRelocation.Relocate(_stagedVenv, _finalVenv, _stagedPython, _finalPython, new[] { script }, out var error);

        Assert.True(ok, error);
        var config = File.ReadAllText(Path.Combine(_stagedVenv, "pyvenv.cfg"));
        Assert.Equal(
            $"home = {_finalPython}\r\nversion = 3.12.15\r\nexecutable = {Path.Combine(_finalPython, "python.exe")}\r\n" +
            $"command = {Path.Combine(_finalPython, "python.exe")} -m venv {_finalVenv}\r\n",
            config);
    }

    [Fact]
    public void Relocate_AScriptThatDoesNotNameTheStagedVenv_FailsAndChangesNothing()
    {
        // A console script naming some other interpreter means the venv was not built the way this code expects.
        // Swapping it in would leave a tool that cannot start, so it is refused - before any file is written.
        var good = Path.Combine(StagedScripts, "cc-pdf.exe");
        var goodBytes = Launcher(StagedInterpreter, new byte[] { 1 }, new byte[] { 2 });
        File.WriteAllBytes(good, goodBytes);
        var odd = Path.Combine(StagedScripts, "cc-devthrottle.exe");
        File.WriteAllBytes(odd, Launcher(@"C:\elsewhere\python.exe", new byte[] { 1 }, new byte[] { 2 }));
        WriteConfig();

        var ok = VenvRelocation.Relocate(_stagedVenv, _finalVenv, _stagedPython, _finalPython, new[] { good, odd }, out var error);

        Assert.False(ok);
        Assert.Contains("cc-devthrottle.exe", error);
        Assert.Equal(goodBytes, File.ReadAllBytes(good));
        Assert.Contains(_stagedPython, File.ReadAllText(Path.Combine(_stagedVenv, "pyvenv.cfg")));
    }

    [Fact]
    public void Relocate_NoConfig_Fails()
    {
        var script = Path.Combine(StagedScripts, "cc-pdf.exe");
        File.WriteAllBytes(script, Launcher(StagedInterpreter, new byte[] { 1 }, new byte[] { 2 }));

        var ok = VenvRelocation.Relocate(_stagedVenv, _finalVenv, _stagedPython, _finalPython, new[] { script }, out var error);

        Assert.False(ok);
        Assert.Contains("pyvenv.cfg", error);
    }

    [Fact]
    public void Replace_NoOccurrence_ReturnsTheSameArraySoNothingIsRewritten()
    {
        var bytes = Encoding.ASCII.GetBytes("nothing to change");

        var result = VenvRelocation.Replace(bytes, Encoding.ASCII.GetBytes("absent"), Encoding.ASCII.GetBytes("x"));

        Assert.Same(bytes, result);
    }

    [Fact]
    public void Replace_SeveralOccurrences_ReplacesEach()
    {
        var result = VenvRelocation.Replace(Encoding.ASCII.GetBytes("a-OLD-b-OLD"), Encoding.ASCII.GetBytes("OLD"),
            Encoding.ASCII.GetBytes("NEWER"));

        Assert.Equal("a-NEWER-b-NEWER", Encoding.ASCII.GetString(result));
    }
}
