using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Tests.Drivers;

internal sealed class EmptyTranscriptReader : ITranscriptReader
{
    public List<TurnWidgetDto> ReadWidgets(string claudeSessionId, string repoPath) => new();

    public SessionUsageDto? ReadUsage(string claudeSessionId, string repoPath) => null;

    public List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath) => new();
}
