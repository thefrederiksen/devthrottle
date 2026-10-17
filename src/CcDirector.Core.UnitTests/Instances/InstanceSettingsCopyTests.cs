using System.Text.Json.Nodes;
using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.UnitTests.Instances;

/// <summary>
/// What a cloned instance takes from its source (owner request, 2026-10-17, with issue
/// devthrottle_internal#2489): its settings, never its identity or its history. A clone that carried the
/// Director id across would be a second copy of the same Director, which is the defect the clone exists to avoid.
/// </summary>
public sealed class InstanceSettingsCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instance-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;

    public InstanceSettingsCopyTests()
    {
        _source = Path.Combine(_root, "instances", "default");
        _target = Path.Combine(_root, "instances", "test-copy");
        Write(_source, "config/config.json",
            """{"gateway":{"url":"https://gw.example","token":"device-key"},"agent":{"entries":[]},"session_rail_order":["s1"],"session_rail_expanded_crews":["c1"]}""");
        Write(_source, "config/director/gateway-token.txt", "device-key");
        Write(_source, "config/director/root-directories.json", """["/home/x/repos"]""");
        Write(_source, "config/director/repositories.json", """["/home/x/repos/a"]""");
        // Identity and history: none of these may reach the clone.
        Write(_source, "config/director/director-id-5b5945bcf19f4e85.txt", "director-id");
        Write(_source, "config/director/sessions.json", "[]");
        Write(_source, "config/director/crash-journal/x.json", "{}");
        Write(_source, "config/director/instances/x.json", "{}");
        Write(_source, "config/outlook/profiles.json", "{}");
        Write(_source, "logs/director/director.log", "log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void Write(string home, string relative, string text)
    {
        var path = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Target(string relative) => Path.Combine(_target, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Copy_CopiesEverySettingsFile()
    {
        var copied = InstanceSettingsCopy.Copy(_source, _target);

        Assert.Equal(InstanceSettingsCopy.CopiedFiles, copied);
        Assert.Equal("device-key", File.ReadAllText(Target("config/director/gateway-token.txt")));
        Assert.Equal("""["/home/x/repos/a"]""", File.ReadAllText(Target("config/director/repositories.json")));
    }

    [Fact]
    public void Copy_KeepsTheGatewayConnectionAndDropsTheSessionLayout()
    {
        InstanceSettingsCopy.Copy(_source, _target);

        var config = JsonNode.Parse(File.ReadAllText(Target("config/config.json")))!.AsObject();
        Assert.Equal("https://gw.example", (string?)config["gateway"]!["url"]);
        Assert.Equal("device-key", (string?)config["gateway"]!["token"]);
        Assert.NotNull(config["agent"]);
        Assert.False(config.ContainsKey("session_rail_order"));
        Assert.False(config.ContainsKey("session_rail_expanded_crews"));
    }

    [Theory]
    [InlineData("config/director/director-id-5b5945bcf19f4e85.txt")]
    [InlineData("config/director/sessions.json")]
    [InlineData("config/director/crash-journal/x.json")]
    [InlineData("config/director/instances/x.json")]
    [InlineData("config/outlook/profiles.json")]
    [InlineData("logs/director/director.log")]
    public void Copy_NeverCopiesIdentityHistoryOrSignIns(string relative)
    {
        InstanceSettingsCopy.Copy(_source, _target);

        Assert.False(File.Exists(Target(relative)));
    }

    [Fact]
    public void Copy_SourceWithoutAFile_SkipsIt()
    {
        File.Delete(Path.Combine(_source, "config", "director", "repositories.json"));

        var copied = InstanceSettingsCopy.Copy(_source, _target);

        Assert.DoesNotContain(Path.Combine("config", "director", "repositories.json"), copied);
        Assert.False(File.Exists(Target("config/director/repositories.json")));
    }

    [Fact]
    public void Copy_IntoItsOwnFolder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => InstanceSettingsCopy.Copy(_source, _source));
    }

    [Fact]
    public void ConfigWithoutSessionKeys_NotAJsonObject_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => InstanceSettingsCopy.ConfigWithoutSessionKeys("[1,2]"));
    }
}
