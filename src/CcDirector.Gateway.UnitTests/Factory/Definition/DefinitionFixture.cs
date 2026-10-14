using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A whole, valid definition (a factory, a boss seat, two more seats, grants, two rule sets, a file, a check, and a
/// factory skill with a file beside it), and the helpers each test uses to break exactly one thing in it. Line
/// numbers in the tests count from the texts below, so each file's lines are numbered in a comment.
/// </summary>
internal static class DefinitionFixture
{
    public const string FactoryId = "devthrottle";

    public static readonly IReadOnlyCollection<string> FleetSkills = new[] { "browsers", "dev-throttle" };

    public static readonly IReadOnlyCollection<string> Machines = new[] { "SOREN_NORTH" };

    // 1  factory: devthrottle
    // 2  title: DevThrottle
    // 3  purpose: Builds DevThrottle
    // 4  goalText: |
    // 5    Ship the product.
    // 6  goalApprovedOn: 2026-10-10
    // 7  bossSeat: boss
    // 8  defaults:
    // 9    machine: SOREN_NORTH
    // 10   agent: ClaudeCode
    // 11   model: claude-opus-5-5
    // 12 repositories: [devthrottle]
    public const string Factory = """
        factory: devthrottle
        title: DevThrottle
        purpose: Builds DevThrottle
        goalText: |
          Ship the product.
        goalApprovedOn: 2026-10-10
        bossSeat: boss
        defaults:
          machine: SOREN_NORTH
          agent: ClaudeCode
          model: claude-opus-5-5
        repositories: [devthrottle]
        """;

    // 1  seat: boss
    // 2  name: Boss
    // 3  role: Boss
    // 4  brief: |
    // 5    Run the factory.
    // 6  starts:
    // 7    - cron: "0 8 * * 1-5"
    // 8      timeZone: Europe/Copenhagen
    // 9      prompt: Morning round.
    // 10 rules: [base]
    public const string Boss = """
        seat: boss
        name: Boss
        role: Boss
        brief: |
          Run the factory.
        starts:
          - cron: "0 8 * * 1-5"
            timeZone: Europe/Copenhagen
            prompt: Morning round.
        rules: [base]
        """;

    // 1  seat: outreach
    // 2  name: Outreach
    // 3  role: Sales
    // 4  line: Find and sell
    // 5  machine: SOREN_SOUTH
    // 6  agent: ClaudeCode
    // 7  model: gpt-6-luna
    // 8  repository: devthrottle
    // 9  brief: |
    // 10   Find prospects.
    // 11 starts:
    // 12   - every: 10m
    // 13     check: checks/new_mail.py
    // 14     secrets: [outreach-imap-password]
    // 15     prompt: "New mail: {count}. {context}"
    // 16 grants: [send-outreach-email]
    // 17 rules: [base, outreach]
    // 18 secrets: [outreach-smtp-password]
    // 19 skills:
    // 20   fleet: [browsers]
    // 21 files: [files/outreach_send.py]
    public const string Outreach = """
        seat: outreach
        name: Outreach
        role: Sales
        line: Find and sell
        machine: SOREN_SOUTH
        agent: ClaudeCode
        model: gpt-6-luna
        repository: devthrottle
        brief: |
          Find prospects.
        starts:
          - every: 10m
            check: checks/new_mail.py
            secrets: [outreach-imap-password]
            prompt: "New mail: {count}. {context}"
        grants: [send-outreach-email]
        rules: [base, outreach]
        secrets: [outreach-smtp-password]
        skills:
          fleet: [browsers]
        files: [files/outreach_send.py]
        """;

    // 1  seat: writer
    // 2  name: Writer
    // 3  role: Writer
    // 4  brief: Write the posts.
    public const string Writer = """
        seat: writer
        name: Writer
        role: Writer
        brief: Write the posts.
        """;

    // 1  grants:
    // 2    - id: send-outreach-email
    // 3      allows: Send email to prospects
    // 4      limits: At most 20 a day
    // 5      seats: [outreach]
    public const string Grants = """
        grants:
          - id: send-outreach-email
            allows: Send email to prospects
            limits: At most 20 a day
            seats: [outreach]
        """;

    // 1 {
    // 2   "permissions": {
    // 3     "allow": ["Bash(git status)"],
    // 4     "deny": ["Bash(git push:*)"],
    // 5     "ask": []
    // 6   }
    // 7 }
    public const string BaseRules = """
        {
          "permissions": {
            "allow": ["Bash(git status)"],
            "deny": ["Bash(git push:*)"],
            "ask": []
          }
        }
        """;

    public const string OutreachRules = """
        {
        	"permissions": {
        		"deny": ["Bash(rm:*)"]
        	}
        }
        """;

    // 1 ---
    // 2 name: mail-voice
    // 3 description: How the factory writes mail.
    // 4 ---
    // 5 Write plainly.
    public const string MailVoice = """
        ---
        name: mail-voice
        description: How the factory writes mail.
        ---
        Write plainly.
        """;

    /// <summary>
    /// The valid definition. Every text has its line endings made LF: a checkout that turns this file's line endings
    /// into CRLF would otherwise put carriage returns inside the raw strings, and the tests' edits are written with LF.
    /// </summary>
    public static Dictionary<string, string> Valid() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["factory.yaml"] = Factory,
        ["seats/boss.yaml"] = Boss,
        ["seats/outreach.yaml"] = Outreach,
        ["seats/writer.yaml"] = Writer,
        ["grants.yaml"] = Grants,
        ["rules/base.json"] = BaseRules,
        ["rules/outreach.json"] = OutreachRules,
        ["files/outreach_send.py"] = "print('sent')\n",
        ["checks/new_mail.py"] = "print('{\"count\": 0}')\n",
        ["skills/mail-voice/SKILL.md"] = MailVoice,
        ["skills/mail-voice/examples.md"] = "Dear Ada, thank you for writing.\n",
    }.ToDictionary(kv => kv.Key, kv => kv.Value.ReplaceLineEndings("\n"), StringComparer.Ordinal);

    /// <summary>The valid definition with one file's text replaced.</summary>
    public static Dictionary<string, string> With(string path, string text)
    {
        var content = Valid();
        content[path] = text.ReplaceLineEndings("\n");
        return content;
    }

    /// <summary>The valid definition with one exact piece of one file's text replaced (it must occur).</summary>
    public static Dictionary<string, string> Edit(string path, string oldText, string newText)
    {
        var content = Valid();
        Assert.Contains(oldText, content[path]);
        content[path] = content[path].Replace(oldText, newText, StringComparison.Ordinal);
        return content;
    }

    /// <summary>The valid definition without one file.</summary>
    public static Dictionary<string, string> Without(string path)
    {
        var content = Valid();
        Assert.True(content.Remove(path));
        return content;
    }

    public static FactoryDefinitionCheckResult Check(IReadOnlyDictionary<string, string> content)
        => FactoryDefinitionCheck.Check(content, FactoryId, FleetSkills, Machines);

    /// <summary>
    /// Assert the check found exactly one error, on <paramref name="file"/> at <paramref name="line"/>, whose message
    /// holds every one of <paramref name="words"/>. One error and only one: a change that broke one thing must not be
    /// reported as several.
    /// </summary>
    public static DefinitionFinding AssertOneError(FactoryDefinitionCheckResult result, string file, int line, params string[] words)
    {
        var errors = result.Findings.Where(f => f.IsError).ToList();
        Assert.True(errors.Count == 1,
            $"Expected one error, got {errors.Count}:\n" + string.Join("\n", errors.Select(e => $"  {e.File}:{e.Line} {e.Message}")));
        var error = errors[0];
        Assert.Equal(file, error.File);
        Assert.Equal(line, error.Line);
        foreach (var w in words) Assert.Contains(w, error.Message);
        Assert.Null(result.Definition);
        return error;
    }

    /// <summary>Assert there is a notice on <paramref name="file"/> at <paramref name="line"/> holding every word.</summary>
    public static void AssertNotice(FactoryDefinitionCheckResult result, string file, int line, params string[] words)
    {
        var match = result.Findings.Where(f => !f.IsError && f.File == file && f.Line == line)
            .FirstOrDefault(f => words.All(w => f.Message.Contains(w, StringComparison.Ordinal)));
        Assert.True(match is not null,
            $"No notice at {file}:{line} holding [{string.Join(", ", words)}]. Findings:\n" +
            string.Join("\n", result.Findings.Select(f => $"  {f.Severity} {f.File}:{f.Line} {f.Message}")));
    }
}
