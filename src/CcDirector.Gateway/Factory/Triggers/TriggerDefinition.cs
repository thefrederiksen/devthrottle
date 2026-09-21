using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>
/// The rules a trigger definition must keep (the Website Business Factory mission, product track). Pure, so it
/// is tested directly and the create and update routes cannot disagree.
/// </summary>
public static partial class TriggerDefinition
{
    /// <summary>The shortest interval a trigger may have. A check is cheap, but not free.</summary>
    public const int MinimumIntervalSeconds = 60;

    /// <summary>The longest interval: a day. Anything rarer is a schedule, not a trigger.</summary>
    public const int MaximumIntervalSeconds = 86_400;

    public const int MaxNameLength = 128;
    public const int MaxLabelLength = 128;
    public const int MaxMachineLength = 256;
    public const int MaxRepoPathLength = 1024;
    public const int MaxCheckCommandLength = 2048;
    public const int MaxPromptLength = 8192;

    /// <summary>The placeholder in a prompt that is replaced with the check's count.</summary>
    public const string CountPlaceholder = "{count}";

    // Letters, digits, space, dot, dash and underscore: a name travels in a URL path and on a command line.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]*$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Why this request cannot become (or change) a trigger, or null when it can. On a create every field is
    /// required; on an update a null field keeps its value, but a field that IS given must still be valid.
    /// </summary>
    public static string? Validate(TriggerDefinitionRequest req, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (Check(req.Name, "name", MaxNameLength, isCreate) is { } nameError) return nameError;
        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (!NamePattern().IsMatch(name))
                return "name may hold only letters, digits, spaces, dots, dashes and underscores, and must start with a letter or digit";
            if (Guid.TryParse(name, out _))
                return "name must not be a trigger id; a trigger is looked up by either";
        }

        if (Check(req.Factory, "factory", MaxLabelLength, isCreate) is { } factoryError) return factoryError;
        if (Check(req.FactoryAgent, "factoryAgent", MaxLabelLength, isCreate) is { } agentError) return agentError;
        if (Check(req.Machine, "machine", MaxMachineLength, isCreate) is { } machineError) return machineError;
        if (Check(req.RepoPath, "repoPath", MaxRepoPathLength, isCreate) is { } repoError) return repoError;
        if (Check(req.CheckCommand, "checkCommand", MaxCheckCommandLength, isCreate) is { } checkError) return checkError;
        if (Check(req.Prompt, "prompt", MaxPromptLength, isCreate) is { } promptError) return promptError;

        if (req.IntervalSeconds is null)
        {
            if (isCreate) return "intervalSeconds is required";
        }
        else if (req.IntervalSeconds < MinimumIntervalSeconds)
        {
            return $"intervalSeconds must be at least {MinimumIntervalSeconds} (one minute)";
        }
        else if (req.IntervalSeconds > MaximumIntervalSeconds)
        {
            return $"intervalSeconds must be at most {MaximumIntervalSeconds} (one day); use a schedule for anything rarer";
        }

        return null;
    }

    /// <summary>The prompt a started session gets: the definition's prompt with every <c>{count}</c> replaced.</summary>
    public static string PromptFor(string prompt, int count)
        => (prompt ?? "").Replace(CountPlaceholder, count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    /// <summary>How long a check may run before the Director kills it: half the interval, at most five minutes,
    /// so a hung check is reported long before the next one is due.</summary>
    public static int TimeoutSecondsFor(int intervalSeconds)
        => Math.Min(300, Math.Max(30, intervalSeconds / 2));

    private static string? Check(string? value, string field, int maxLength, bool required)
    {
        if (value is null)
            return required ? $"{field} is required" : null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return $"{field} must not be blank";
        if (trimmed.Length > maxLength)
            return $"{field} is longer than {maxLength} characters";
        return null;
    }
}
