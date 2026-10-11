using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Architecture fitness function: production Director code must not perform provider transcription
/// directly. Audio-to-text belongs to the Gateway transcription owner.
/// </summary>
public sealed class GatewayOnlyTranscriptionGuardTests
{
    private static readonly string[] ForbiddenRuntimeUsages =
    {
        "new BatchTranscriptionPipeline",
        "new OpenAiTranscriptionProvider",
        "new OpenAiRealtimeProvider",
        "new LivePreviewTranscriber",
        "new OpenAiSttService",
        "new OpenAiRecordingTranscriber",
        "TranscriptionRoutingEndpoint",
        "/transcription/routing",
    };

    private static readonly string[] AllowedPrefixes =
    {
        "src/CcDirector.Gateway/Transcription/",
        "src/CcDirector.Gateway/Api/TranscriptionBatchEndpoint.cs",
    };

    [Fact]
    public void Production_director_code_does_not_instantiate_direct_transcription()
    {
        var root = RepositorySourceIndex.Root;
        var offenders = new List<string>();

        foreach (var file in RepositorySourceIndex.Under("src", ".cs"))
        {
            var rel = Relative(root, file);
            if (IsTestProject(rel)) continue;
            if (AllowedPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenRuntimeUsages)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    offenders.Add($"{rel}: {forbidden}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Production Director code must route audio-to-text through the Gateway transcription owner. "
            + "Do not instantiate direct transcription pipeline/provider classes outside Gateway-owned code:\n  "
            + string.Join("\n  ", offenders));
    }

    // One shared predicate (see TestProjectPath). This guard is one of the two that FIRED after the
    // suite split - two moved files carry the forbidden construction outside every allowed prefix, and
    // the old substring stopped excluding them. The twelve reported offenders are ten loopback plus
    // these two. The LATENT pair - green only because nothing moved happened to match them - is the
    // agent-plugin and storage-root guards.
    private static bool IsTestProject(string rel) => TestProjectPath.IsTestProject(rel);

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');
}
