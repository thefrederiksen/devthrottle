using CcDirector.Core.Claude;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// ClaudeDriver's view of claude.exe's transcript store - an injection seam so the
/// driver's unit tests never touch the user profile. The real implementation reads
/// ~/.claude/projects from disk via the same Core readers the Director uses.
/// </summary>
public interface ITranscriptReader
{
    /// <summary>Parsed widgets of one transcript, chronological. Empty list when the
    /// transcript file does not exist yet (defined state right after spawn).</summary>
    List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath);

    /// <summary>Token usage of one transcript; null when the file does not exist yet.</summary>
    SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath);

    /// <summary>The repo's transcript files, newest first.</summary>
    List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath);
}

/// <summary>Disk-backed implementation over the same Core readers the Director uses.</summary>
public sealed class ClaudeTranscriptReader : ITranscriptReader
{
    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath)
    {
        var jsonl = ClaudeSessionReader.GetJsonlPath(claudeSessionId, repoPath);
        if (!File.Exists(jsonl))
            return new List<TurnWidgetDto>();
        var messages = StreamMessageParser.ParseFile(jsonl);
        return WidgetBuilder.BuildFromMessages(messages);
    }

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath)
    {
        var jsonl = ClaudeSessionReader.GetJsonlPath(claudeSessionId, repoPath);
        if (!File.Exists(jsonl))
            return null;
        return SessionTokenUsage.ComputeFromFile(jsonl, claudeSessionId);
    }

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath)
        => ClaudeSessionReader.ListTranscripts(repoPath);
}
