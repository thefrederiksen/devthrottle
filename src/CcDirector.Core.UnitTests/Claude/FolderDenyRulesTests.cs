using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// DENY RULES IN THE FOLDER A SESSION STARTS IN (the owner's ruling of 24 October 2026). On that day a factory's own
/// Architect had written a deny list into the folder's .claude/settings.json, and the owner could not approve removing
/// a rule from that very file. The Director reads both settings files at launch and reports what it finds; it never
/// changes them.
/// </summary>
public sealed class FolderDenyRulesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dt-folder-deny-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(_folder, ".claude"));
        var path = Path.Combine(_folder, ".claude", name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Read_BothSettingsFilesWithDenyRules_ReportsEachFileWithItsRulesInFileOrder()
    {
        var settings = Write("settings.json",
            "{\"permissions\": {\"allow\": [\"Bash(ls:*)\"], \"deny\": [\"Bash(cc-gmail:*)\", \"Edit(.claude/settings.json)\"]}}");
        var local = Write("settings.local.json", "{\"permissions\": {\"deny\": [\"Write(.claude/settings.json)\"]}}");

        var found = FolderDenyRules.Read(_folder);

        Assert.Equal(new[] { settings, local }, found.Select(f => f.File));
        Assert.Equal(new[] { "Bash(cc-gmail:*)", "Edit(.claude/settings.json)" }, found[0].Rules);
        Assert.Equal(new[] { "Write(.claude/settings.json)" }, found[1].Rules);
        Assert.All(found, f => Assert.Null(f.Problem));
    }

    [Fact]
    public void Read_NoSettingsFiles_OrOnlyAskAndAllowRules_OrAnEmptyDenyList_FindsNothing()
    {
        Assert.Empty(FolderDenyRules.Read(_folder));

        Write("settings.json", "{\"permissions\": {\"ask\": [\"Bash(cc-gmail:*)\"], \"allow\": [\"Bash(ls:*)\"]}}");
        Write("settings.local.json", "{\"permissions\": {\"deny\": []}}");

        Assert.Empty(FolderDenyRules.Read(_folder));
    }

    [Fact]
    public void Read_ASettingsFileThatIsNotJson_IsReportedWithItsProblem_NeverPassedOverAsClean()
    {
        var path = Write("settings.json", "{\"permissions\": {\"deny\": [");

        var found = Assert.Single(FolderDenyRules.Read(_folder));

        Assert.Equal(path, found.File);
        Assert.Empty(found.Rules);
        Assert.StartsWith("it is not JSON, so whether it holds a deny rule could not be read", found.Problem);
    }

    [Fact]
    public void Read_ACommentAndATrailingComma_AreReadAsClaudeCodeReadsThem()
    {
        Write("settings.json", "{\n  // written by the Architect\n  \"permissions\": {\"deny\": [\"Bash(rm:*)\",]},\n}\n");

        Assert.Equal(new[] { "Bash(rm:*)" }, Assert.Single(FolderDenyRules.Read(_folder)).Rules);
    }

    [Fact]
    public void Read_NeverChangesTheFiles()
    {
        var text = "{\"permissions\": {\"deny\": [\"Bash(cc-gmail:*)\"]}}";
        var path = Write("settings.json", text);

        FolderDenyRules.Read(_folder);

        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void LogLine_SaysTheFileTheRulesAndThatTheyWereLeftAsTheyAre_AndNothingForAFolderWithNone()
    {
        var path = Write("settings.json", "{\"permissions\": {\"deny\": [\"Bash(cc-gmail:*)\"]}}");

        Assert.Equal(
            $"DENY RULES IN THE FOLDER: {path} holds deny rules: Bash(cc-gmail:*). A deny rule refuses with no prompt, so the " +
            "owner cannot approve what it blocks; they were left as they are.",
            FolderDenyRules.LogLine(FolderDenyRules.Read(_folder)));
        Assert.Null(FolderDenyRules.LogLine(Array.Empty<CcDirector.Gateway.Contracts.FolderDenyRulesDto>()));
    }
}
