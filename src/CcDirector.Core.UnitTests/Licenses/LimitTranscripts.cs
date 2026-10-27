using System.Text.Json;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// Claude Code transcript lines shaped like the real records of 26 Oct 2026 (sessions 102 and 115 on one machine,
/// stopped on a weekly limit), with every identifier and address replaced. Only the fields the recogniser reads, and
/// the ones beside them that a real record carries, are kept.
/// </summary>
internal static class LimitTranscripts
{
    public const string LimitRecordUuid = "4176a42c-e276-4856-8453-a333697fc968";
    public static readonly DateTime LimitAtUtc = new(2026, 10, 26, 14, 48, 40, 710, DateTimeKind.Utc);

    private static string Line(object record) => JsonSerializer.Serialize(record);

    public static string UserPrompt(string text, DateTime at, string? uuid = null) => Line(new
    {
        type = "user",
        isSidechain = false,
        uuid = uuid ?? Guid.NewGuid().ToString(),
        timestamp = at.ToString("O"),
        message = new { role = "user", content = text },
    });

    public static string AssistantText(string text, DateTime at) => Line(new
    {
        type = "assistant",
        isSidechain = false,
        uuid = Guid.NewGuid().ToString(),
        timestamp = at.ToString("O"),
        message = new { role = "assistant", model = "claude-opus-5-5", content = new[] { new { type = "text", text } } },
    });

    /// <summary>The limit reply as Claude Code wrote it: a synthetic assistant record marked as an API error.</summary>
    public static string LimitReply(DateTime? at = null, string? uuid = null, string apiError = "usage_limit_reached") => Line(new
    {
        type = "assistant",
        isSidechain = false,
        uuid = uuid ?? LimitRecordUuid,
        timestamp = (at ?? LimitAtUtc).ToString("O"),
        message = new
        {
            role = "assistant",
            model = "<synthetic>",
            content = new[] { new { type = "text", text = "You've hit your weekly limit \u00b7 resets Oct 29, 5pm (America/Toronto)" } },
        },
        apiError,
        apiErrorParams = new { rate_limit_info = new { status = "rejected", rateLimitType = "seven_day", overageStatus = "rejected" } },
        error = "rate_limit",
        isApiErrorMessage = true,
        apiErrorStatus = 429,
    });

    public static string TurnDuration(DateTime at) => Line(new
    {
        type = "system", subtype = "turn_duration", durationMs = 1280, timestamp = at.ToString("O"), uuid = Guid.NewGuid().ToString(),
    });

    public static string Attachment(DateTime at) => Line(new { type = "attachment", timestamp = at.ToString("O") });

    /// <summary>Session 102's stop: an owner prompt, the limit reply, the turn's duration.</summary>
    public static string[] StoppedOnTheLimit() =>
    [
        AssistantText("Yes, both are moving, and I don't need anything from you right now.", LimitAtUtc.AddMinutes(-16)),
        TurnDuration(LimitAtUtc.AddMinutes(-16)),
        UserPrompt("Can you please create a clear dev report?", LimitAtUtc.AddSeconds(-1)),
        Attachment(LimitAtUtc.AddSeconds(-1)),
        LimitReply(),
        TurnDuration(LimitAtUtc.AddMilliseconds(22)),
    ];

    /// <summary>A turn that ended with an ordinary reply.</summary>
    public static string[] EndedNormally() =>
    [
        UserPrompt("Is this moving forward?", LimitAtUtc.AddMinutes(-2)),
        AssistantText("Yes, both are moving.", LimitAtUtc.AddMinutes(-1)),
        TurnDuration(LimitAtUtc.AddMinutes(-1)),
    ];

    public static string Write(string folder, string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(folder, name + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }
}
