using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Build-time guard for the transcription-integrity rule (docs/CodingStyle.md section 16).
///
/// THE RULE: when a user dictates by voice, the speech-to-text result is the user's words and is
/// the source of truth. The ONLY permitted change to it is a validated dictionary find/replace,
/// applied by deterministic code (TranscriptEditEngine, driven by CleanupOrchestrator). A language
/// model may LOCATE misheard terms (propose JSON edits); it must NEVER receive the transcript and
/// return free text used as the user's words.
///
/// This rule has regressed before, so it is enforced here two ways:
///   1. ARCHITECTURE guards (source scans) that fail the build if a new transcript-rewrite path is
///      introduced, or if the removed free-text cleanup comes back.
///   2. BEHAVIORAL guards proving the shared pipeline ships the raw transcript byte-identical when a
///      model tries to rewrite it.
/// </summary>
public sealed class TranscriptionIntegrityTests
{
    // ===== Architecture guards =================================================================

    /// <summary>
    /// The directories that make up the transcription core. Inside these, the ONLY file allowed to
    /// talk to a text-generating model (chat/completions or responses) is CleanupOrchestrator, and it
    /// may only ask for find/replace edit proposals. Any other file here that reaches a chat model is
    /// a new transcript-rewrite path - exactly what the rule forbids.
    /// </summary>
    private static readonly string[] TranscriptionCoreDirs =
    [
        "src/CcDirector.Core/Dictation",
        "src/CcDirector.Core/Transcription",
        "src/CcDirector.Core/Recording",
    ];

    private const string AllowedChatModelFile = "CleanupOrchestrator.cs";

    [Fact]
    public void TranscriptionCore_OnlyCleanupOrchestrator_TalksToATextGeneratingModel()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var relDir in TranscriptionCoreDirs)
        {
            var dir = Path.Combine(root, relDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;

            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(path), AllowedChatModelFile, StringComparison.Ordinal))
                    continue;

                var text = File.ReadAllText(path);
                // A text-generating chat endpoint is the signature of a transcript-rewrite path.
                // Transcription itself uses /audio/transcriptions, which is fine and not matched here.
                if (text.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(text, @"/v1/responses\b"))
                {
                    offenders.Add(Path.GetRelativePath(root, path));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A transcription-core file other than CleanupOrchestrator reaches a text-generating model. "
            + "Transcripts may only be corrected by the validated dictionary find/replace in "
            + "TranscriptEditEngine - never rewritten by a model. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RemovedFreeTextVoiceCleanup_DoesNotReturn()
    {
        // The old free-text voice cleanup round-tripped the user's transcript through a chat model and
        // used the returned text as the user's words. It was removed; it must not come back. We match
        // an actual definition or call (the identifier immediately followed by "(") so prose that
        // explains the removal - like the comments in this file - does not trip the guard.
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (Regex.IsMatch(File.ReadAllText(path), @"CleanVoiceTranscriptAsync\("))
                offenders.Add(Path.GetRelativePath(root, path));
        }

        Assert.True(
            offenders.Count == 0,
            "The forbidden free-text voice cleanup CleanVoiceTranscriptAsync was defined or called again. "
            + "It rewrites the user's words through a model and must stay deleted. Offenders: "
            + string.Join(", ", offenders));
    }

    // ===== Behavioral guards ===================================================================

    [Fact]
    public async Task SharedPipeline_ModelTriesToRewriteWholeTranscript_ShipsRawByteIdentical()
    {
        const string raw = "yeah just push the cc-director change and let me know when it builds";
        // The cleanup model is asked only for an edit document, but here it MISBEHAVES and returns a
        // fully rewritten transcript as plain prose. ParseEdits rejects anything that is not a valid
        // {"edits":[...]} object, so the pipeline must ship the RAW transcript unchanged.
        const string modelRewrite = "Please push the change to cc-director and notify me once the build completes.";
        var handler = new StubHandler(transcript: raw, chatContent: modelRewrite);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "utterance.webm", Byo(), DictWith("cc-director"));

        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript); // ordinal: byte-identical
        Assert.False(result.DictionaryApplied);
        Assert.Empty(result.ChangedWords);
    }

    [Fact]
    public async Task SharedPipeline_ModelProposesNonDictionaryReplacement_RejectsItAndShipsRaw()
    {
        const string raw = "can you refactor the login flow for me";
        // A well-formed edit document, but the replacement is NOT a canonical dictionary term and the
        // find spans a whole clause. The validator must reject it (replace not canonical, not a
        // mishearing), leaving the transcript byte-identical.
        const string maliciousEdit =
            "{\"edits\":[{\"find\":\"refactor the login flow\",\"replace\":\"delete the production database\"}]}";
        var handler = new StubHandler(transcript: raw, chatContent: maliciousEdit);
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));

        var result = await pipeline.TranscribeAsync(
            new byte[] { 1, 2, 3 }, "utterance.webm", Byo(), DictWith("cc-director"));

        Assert.Equal(raw, result.RawTranscript);
        Assert.Equal(result.RawTranscript, result.CorrectedTranscript);
        Assert.False(result.DictionaryApplied);
        Assert.Empty(result.ChangedWords);
    }

    // ===== Helpers =============================================================================

    /// <summary>Answers /audio/transcriptions with a canned transcript and /chat/completions with a
    /// caller-supplied content string (which may be a valid edit document or deliberate garbage).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _transcript;
        private readonly string _chatContent;

        public StubHandler(string transcript, string chatContent)
        {
            _transcript = transcript;
            _chatContent = chatContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            string json;
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
                json = "{\"text\": " + System.Text.Json.JsonSerializer.Serialize(_transcript) + "}";
            else if (url.EndsWith("/chat/completions", StringComparison.Ordinal))
                json = "{\"choices\":[{\"message\":{\"content\": "
                    + System.Text.Json.JsonSerializer.Serialize(_chatContent) + "}}]}";
            else
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ResolvedTranscription Byo() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.OpenAiBaseUrl,
        ApiKey = "sk-byo-key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.OpenAiModel,
        Mode = TranscriptionMode.Byo,
    };

    private static DictationDictionary DictWith(params string[] vocab) => new(
        vocab,
        new Dictionary<string, IReadOnlyList<string>>(),
        new Dictionary<string, DictationProfile> { ["default"] = new("default", CleanupEnabled: true) });

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
