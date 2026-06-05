using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /claude-transcripts?repo=... - the repo's transcript files (claude session ids +
/// last-write times, newest first), read directly from the claude projects directory.
///
/// Unlike /claude-sessions (built from claude.exe's lazily-written sessions-index.json),
/// this reflects the filesystem RIGHT NOW. External brain drivers depend on it to find
/// the transcript /clear just created and relink the session (issue #172).
/// </summary>
internal static class ClaudeTranscriptsEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/claude-transcripts", (string? repo) =>
        {
            FileLog.Write($"[ClaudeTranscriptsEndpoint] GET /claude-transcripts: repo={repo}");

            if (string.IsNullOrWhiteSpace(repo))
                return Results.BadRequest(new { error = "repo query parameter is required" });

            var files = ClaudeSessionReader.ListTranscripts(repo);
            return Results.Json(files.Select(f => new
            {
                claudeSessionId = f.ClaudeSessionId,
                lastWriteUtc = f.LastWriteUtc,
            }).ToList());
        });
    }
}
