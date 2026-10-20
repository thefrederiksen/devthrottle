using CcDirector.Core.Boxes;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Boxes;

/// <summary>
/// The Director's hosted-desktop list (the Factory Boxes mission, phase 1): what the file may say, what happens when
/// it says something it may not, and the one lookup the desktop relay calls. Every test hands the registry the file's
/// text and a list to log into, so nothing touches the disk or the process log.
/// </summary>
public sealed class HostedDesktopRegistryTests
{
    private const string Requester = "stream 1234 for account t#abcd";

    private const string GoodFile = """
        {
          "desktops": [
            { "boxId": "demo-desktop-soren", "displayName": "Soren's demo desktop", "port": 3001 },
            { "boxId": "box-2", "displayName": "Second box", "port": 3002 }
          ]
        }
        """;

    private readonly List<string> _log = new();

    private HostedDesktopRegistry Loaded(string? fileText)
    {
        var registry = new HostedDesktopRegistry(() => fileText, _log.Add);
        registry.Reload();
        return registry;
    }

    private static string OneEntry(string entryJson) => $$"""{ "desktops": [ {{entryJson}} ] }""";

    // ---------- Loading the file ----------

    [Fact]
    public void Reload_GoodFile_ListsEveryDesktopByBoxIdWithItsName()
    {
        var registry = Loaded(GoodFile);

        Assert.Null(registry.LoadError);
        Assert.Collection(registry.Desktops,
            d => { Assert.Equal("box-2", d.BoxId); Assert.Equal("Second box", d.DisplayName); },
            d => { Assert.Equal("demo-desktop-soren", d.BoxId); Assert.Equal("Soren's demo desktop", d.DisplayName); });
    }

    [Fact]
    public void Reload_NoFile_HostsNothingAndIsNotAnError()
    {
        var registry = Loaded(null);

        Assert.Empty(registry.Desktops);
        Assert.Null(registry.LoadError);
    }

    [Theory]
    [InlineData("Demo-Desktop")]   // upper case
    [InlineData("demo_desktop")]   // underscore
    [InlineData("-demo")]          // leading hyphen
    [InlineData("demo-")]          // trailing hyphen
    [InlineData("")]               // empty
    [InlineData("127.0.0.1")]      // an address
    [InlineData("localhost:3001")] // a host and a port
    [InlineData("3001")]           // a bare port
    [InlineData("../etc")]         // a path
    public void Reload_BadBoxId_RefusesTheWholeFileAndLogsWhy(string boxId)
    {
        var file = $$"""
            { "desktops": [
              { "boxId": "good-box", "displayName": "Good", "port": 3001 },
              { "boxId": "{{boxId}}", "displayName": "Bad", "port": 3002 }
            ] }
            """;

        var registry = Loaded(file);

        // The good entry is NOT kept: an invalid entry refuses the file, it is never skipped.
        Assert.Empty(registry.Desktops);
        Assert.Contains("entry 2", registry.LoadError);
        Assert.Contains(_log, line => line.Contains("REFUSED") && line.Contains("entry 2"));
    }

    [Fact]
    public void Reload_BoxIdLongerThanADnsLabel_IsRefused()
    {
        var registry = Loaded(OneEntry($$"""{ "boxId": "{{new string('a', 64)}}", "displayName": "Long", "port": 3001 }"""));

        Assert.Empty(registry.Desktops);
        Assert.Contains("longer than 63", registry.LoadError);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("100000")]
    public void Reload_PortOutOfRange_IsRefused(string port)
    {
        var registry = Loaded(OneEntry($$"""{ "boxId": "demo", "displayName": "Demo", "port": {{port}} }"""));

        Assert.Empty(registry.Desktops);
        Assert.Contains("1 to 65535", registry.LoadError);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public void Reload_PortAtTheEdgesOfTheRange_IsAccepted(string port)
    {
        var registry = Loaded(OneEntry($$"""{ "boxId": "demo", "displayName": "Demo", "port": {{port}} }"""));

        Assert.Null(registry.LoadError);
        Assert.Single(registry.Desktops);
    }

    [Fact]
    public void Reload_PortWrittenAsText_IsRefused()
    {
        var registry = Loaded(OneEntry("""{ "boxId": "demo", "displayName": "Demo", "port": "127.0.0.1:3001" }"""));

        Assert.Empty(registry.Desktops);
        Assert.NotNull(registry.LoadError);
    }

    [Fact]
    public void Reload_MissingPort_IsRefused()
    {
        var registry = Loaded(OneEntry("""{ "boxId": "demo", "displayName": "Demo" }"""));

        Assert.Empty(registry.Desktops);
        Assert.Contains("has no port", registry.LoadError);
    }

    [Theory]
    [InlineData("host", "\"10.0.0.5\"")]
    [InlineData("address", "\"192.168.1.1\"")]
    [InlineData("url", "\"http://example.com\"")]
    public void Reload_AHostInTheFile_IsRefusedNotIgnored(string field, string value)
    {
        var registry = Loaded(OneEntry($$"""{ "boxId": "demo", "displayName": "Demo", "port": 3001, "{{field}}": {{value}} }"""));

        Assert.Empty(registry.Desktops);
        Assert.Contains("a host can never be given", registry.LoadError);
    }

    [Fact]
    public void Reload_SameBoxIdTwice_IsRefused()
    {
        var registry = Loaded("""
            { "desktops": [
              { "boxId": "demo", "displayName": "One", "port": 3001 },
              { "boxId": "demo", "displayName": "Two", "port": 3002 }
            ] }
            """);

        Assert.Empty(registry.Desktops);
        Assert.Contains("listed twice", registry.LoadError);
    }

    [Fact]
    public void Reload_SamePortTwice_IsRefused()
    {
        var registry = Loaded("""
            { "desktops": [
              { "boxId": "one", "displayName": "One", "port": 3001 },
              { "boxId": "two", "displayName": "Two", "port": 3001 }
            ] }
            """);

        Assert.Empty(registry.Desktops);
        Assert.Contains("already listed for another box", registry.LoadError);
    }

    [Theory]
    [InlineData("""{ "boxId": "demo", "port": 3001 }""")]
    [InlineData("""{ "boxId": "demo", "displayName": "   ", "port": 3001 }""")]
    public void Reload_NoDisplayName_IsRefused(string entry)
    {
        var registry = Loaded(OneEntry(entry));

        Assert.Empty(registry.Desktops);
        Assert.Contains("no display name", registry.LoadError);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ }")]
    [InlineData("[]")]
    public void Reload_NotTheFormat_IsRefused(string text)
    {
        var registry = Loaded(text);

        Assert.Empty(registry.Desktops);
        Assert.NotNull(registry.LoadError);
    }

    [Fact]
    public void Reload_FileFixedAfterARefusal_ListsItsDesktopsAgain()
    {
        var text = OneEntry("""{ "boxId": "BAD", "displayName": "Demo", "port": 3001 }""");
        var registry = new HostedDesktopRegistry(() => text, _log.Add);
        registry.Reload();
        Assert.Empty(registry.Desktops);

        text = OneEntry("""{ "boxId": "demo", "displayName": "Demo", "port": 3001 }""");
        var changed = registry.Reload();

        Assert.True(changed);
        Assert.Null(registry.LoadError);
        Assert.Equal("demo", Assert.Single(registry.Desktops).BoxId);
    }

    [Fact]
    public void Reload_SameFileAgain_ReportsNoChange()
    {
        var registry = Loaded(GoodFile);

        Assert.False(registry.Reload());
    }

    [Fact]
    public void Reload_DesktopRemoved_ReportsAChange()
    {
        var text = GoodFile;
        var registry = new HostedDesktopRegistry(() => text, _log.Add);
        registry.Reload();

        text = OneEntry("""{ "boxId": "box-2", "displayName": "Second box", "port": 3002 }""");

        Assert.True(registry.Reload());
        Assert.Equal("box-2", Assert.Single(registry.Desktops).BoxId);
    }

    [Fact]
    public void Reload_FileCannotBeRead_HostsNothingAndSaysWhy()
    {
        var registry = new HostedDesktopRegistry(() => throw new IOException("disk gone"), _log.Add);

        registry.Reload();

        Assert.Empty(registry.Desktops);
        Assert.Contains("disk gone", registry.LoadError);
    }

    // ---------- The lookup the relay calls ----------

    [Fact]
    public void Resolve_HostedBoxId_GivesItsLocalPort()
    {
        var registry = Loaded(GoodFile);

        var answer = registry.Resolve("demo-desktop-soren", Requester);

        Assert.True(answer.Allowed);
        Assert.Equal(3001, answer.Port);
        Assert.Equal("demo-desktop-soren", answer.BoxId);
        Assert.Null(answer.Refusal);
    }

    [Fact]
    public void Resolve_UnknownBoxId_IsRefusedAndTheRefusalIsLoggedWithTheBoxIdAndTheRequester()
    {
        var registry = Loaded(GoodFile);
        _log.Clear();

        var answer = registry.Resolve("someone-elses-box", Requester);

        Assert.False(answer.Allowed);
        Assert.Equal(0, answer.Port);
        Assert.Equal("this Director hosts no desktop with that box id", answer.Refusal);
        var line = Assert.Single(_log);
        Assert.Equal("[HostedDesktopRegistry] Resolve REFUSED: box=someone-elses-box, requester=stream 1234 for account t#abcd: "
                     + "this Director hosts no desktop with that box id", line);
    }

    [Theory]
    [InlineData("3001")]
    [InlineData("127.0.0.1:3001")]
    [InlineData("localhost")]
    [InlineData("192.168.1.1")]
    [InlineData("demo-desktop-soren:3001")]
    [InlineData("demo-desktop-soren/../other")]
    public void Resolve_APortOrAHostInsteadOfABoxId_IsRefusedAndLogged(string asked)
    {
        var registry = Loaded(GoodFile);
        _log.Clear();

        var answer = registry.Resolve(asked, Requester);

        Assert.False(answer.Allowed);
        Assert.Equal(0, answer.Port);
        var line = Assert.Single(_log);
        Assert.StartsWith("[HostedDesktopRegistry] Resolve REFUSED: box=", line);
        Assert.Contains($"requester={Requester}", line);
    }

    [Fact]
    public void Resolve_PortNumberOfAHostedDesktop_IsStillRefused()
    {
        // Knowing the port of a hosted desktop must not open it: only its box id does.
        var registry = Loaded(GoodFile);

        Assert.False(registry.Resolve("3001", Requester).Allowed);
    }

    [Fact]
    public void Resolve_BoxIdWithDifferentCase_IsRefused()
    {
        var registry = Loaded(GoodFile);

        Assert.False(registry.Resolve("Demo-Desktop-Soren", Requester).Allowed);
    }

    [Fact]
    public void Resolve_AfterTheFileWasRefused_RefusesWithTheFilesReason()
    {
        var registry = Loaded(OneEntry("""{ "boxId": "demo", "displayName": "Demo", "port": 0 }"""));

        var answer = registry.Resolve("demo", Requester);

        Assert.False(answer.Allowed);
        Assert.Contains("hosted-desktop list was refused", answer.Refusal);
    }

    [Fact]
    public void Resolve_NullOrBlankBoxId_IsRefused()
    {
        var registry = Loaded(GoodFile);

        Assert.False(registry.Resolve(null, Requester).Allowed);
        Assert.False(registry.Resolve("", Requester).Allowed);
    }

    [Fact]
    public void Resolve_ControlCharactersInTheAskedId_DoNotReachTheLogLine()
    {
        var registry = Loaded(GoodFile);
        _log.Clear();

        registry.Resolve("evil\n[HostedDesktopRegistry] Resolve: box=demo", Requester);

        Assert.DoesNotContain('\n', Assert.Single(_log));
    }

    [Fact]
    public void Desktops_NeverCarryAPort()
    {
        // The pushed shape has exactly two fields; a port can only come from Resolve.
        Assert.Equal(new[] { nameof(HostedDesktopDto.BoxId), nameof(HostedDesktopDto.DisplayName) },
            typeof(HostedDesktopDto).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }
}
