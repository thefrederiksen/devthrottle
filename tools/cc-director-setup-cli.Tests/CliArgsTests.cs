using CcDirector.Setup.Cli;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

public class CliArgsTests
{
    [Fact]
    public void Parse_CommandOptionsAndFlags()
    {
        var args = CliArgs.Parse(["update", "--role", "gateway", "--component", "cc-pdf", "--dry-run", "--json"]);

        Assert.Equal("update", args.Command);
        Assert.Equal("gateway", args.Option("role"));
        Assert.Equal("cc-pdf", args.Option("component"));
        Assert.True(args.HasFlag("dry-run"));
        Assert.True(args.HasFlag("json"));
    }

    [Fact]
    public void Parse_Positionals()
    {
        var args = CliArgs.Parse(["rollback", "director", "--json"]);
        Assert.Equal("rollback", args.Command);
        Assert.Single(args.Positionals);
        Assert.Equal("director", args.Positionals[0]);
        Assert.True(args.HasFlag("json"));
    }

    [Fact]
    public void Parse_DefaultsToHelp()
    {
        Assert.Equal("help", CliArgs.Parse([]).Command);
        Assert.Equal("help", CliArgs.Parse(["--json"]).Command); // leading option, no command
    }

    [Fact]
    public void Option_FallbackUsedWhenAbsent()
    {
        var args = CliArgs.Parse(["plan"]);
        Assert.Equal("latest", args.Option("manifest", "latest"));
        Assert.Equal("workstation", args.Option("role", "workstation"));
    }

    [Fact]
    public void Parse_FlagBeforeOptionDoesNotSwallowNext()
    {
        // --dry-run is a known flag; --root must still be parsed as an option after it.
        var args = CliArgs.Parse(["update", "--dry-run", "--root", "C:\\tmp"]);
        Assert.True(args.HasFlag("dry-run"));
        Assert.Equal("C:\\tmp", args.Option("root"));
    }
}
