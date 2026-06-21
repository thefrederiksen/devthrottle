using System.Reflection;
using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression guard for issue #592: the phone push-to-talk turn (the Director-side
/// <see cref="VoiceTurnEndpoint"/> reached via the Gateway's resumable-upload front door) must
/// transcribe through the ONE shared batch pipeline (issue #587) using the user-SELECTED method,
/// NOT the old hardcoded whisper-1 / api.openai.com path, and must apply the dictionary corrector
/// only - no free-text language-model cleanup.
///
/// The pipeline's own routing, byte-identical-without-a-dictionary-hit, and one-batch-call
/// behaviors are proven directly in <c>BatchTranscriptionPipelineTests</c>; the audio path's
/// no-method-resolvable behavior is proven in <c>VoiceTurnEndpointTests</c>. This guard locks in
/// the structural change so the hardcoded endpoint can never silently return: it asserts the
/// compiled <see cref="VoiceTurnEndpoint"/> type no longer carries the legacy whisper-1 constants
/// or its own bespoke transcription HTTP helper.
/// </summary>
public sealed class VoiceTurnSharedPipelineRegressionTests
{
    [Fact]
    public void VoiceTurnEndpoint_HasNoHardcodedWhisperConstants()
    {
        var type = typeof(VoiceTurnEndpoint);

        // The old path baked the URL + model in as private const string fields. After the
        // migration the type carries neither - the route, key, and model come from the resolved
        // method at request time.
        var stringConstants = type
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .ToList();

        Assert.DoesNotContain(stringConstants, v => v is not null && v.Contains("whisper-1", StringComparison.Ordinal));
        Assert.DoesNotContain(stringConstants, v => v is not null && v.Contains("api.openai.com", StringComparison.Ordinal));
    }

    [Fact]
    public void VoiceTurnEndpoint_HasNoBespokeTranscribeHelper()
    {
        var type = typeof(VoiceTurnEndpoint);

        // The bespoke whisper transcription helper (and its content-type guesser) were removed: the
        // shared pipeline owns the single transcription transport now. Their absence proves the
        // endpoint cannot transcribe except through the shared path.
        var methodNames = type
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("TranscribeAsync", methodNames);
        Assert.DoesNotContain("GuessAudioContentType", methodNames);
    }
}
