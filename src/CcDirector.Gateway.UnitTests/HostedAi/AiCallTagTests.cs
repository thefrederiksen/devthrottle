using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.HostedAi;

/// <summary>The tests below set the process-wide service-token variable, so they never run beside each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiCallTagEnvCollection
{
    public const string Name = "AI call tag service token";
}

/// <summary>
/// Every hosted AI call says what it is FOR and, on the hosted Gateway, which account it is for
/// (devthrottle_internal #2213). The API records both on the call's usage row; these prove the Gateway
/// side: the headers that go on the wire, the one rule deciding the account, and that no production call
/// site builds a client without a tag.
/// </summary>
[Collection(AiCallTagEnvCollection.Name)]
public sealed class AiCallTagTests : IDisposable
{
    private const string Token = "gateway-service-token-for-the-tests-0123456789";
    private static readonly TenantId Account = new("6f1d2c3b-0000-4000-8000-00000000abcd");
    private readonly string? _priorToken = Environment.GetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, _priorToken);

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "ok" } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string? Header(HttpRequestMessage req, string name)
        => req.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

    [Fact]
    public async Task AHostedTenantsCall_CarriesTheFeature_TheAccount_AndTheServiceCredential()
    {
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, Token);
        var stub = new StubHandler();
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_gateway", IncludedModelId.WingmanFast,
            http, _ => { }, tag: GatewayAiCallTags.For(Account, AiFeature.TurnVerdict));

        await brain.AskAsync("judge this turn");

        var req = stub.LastRequest!;
        Assert.Equal("turn-verdict", Header(req, AiCallTag.FeatureHeader));
        Assert.Equal(Account.Value, Header(req, AiCallTag.AccountHeader));
        Assert.Equal(Token, Header(req, AiCallTag.ServiceTokenHeader));
        Assert.Equal("dt_live_gateway", req.Headers.Authorization!.Parameter);
        Assert.DoesNotContain("turn-verdict", stub.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSelfHostTenant_NamesTheFeature_AndNoAccount()
    {
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, Token);
        var stub = new StubHandler();
        using var http = new HttpClient(stub);
        var brain = new HostedInferenceBrain("https://devthrottle.com/api/v1", "dt_live_owner", IncludedModelId.Wingman,
            http, _ => { }, tag: GatewayAiCallTags.For(TenantId.Local, AiFeature.Rules));

        await brain.AskAsync("read this rule");

        Assert.Equal("rules", Header(stub.LastRequest!, AiCallTag.FeatureHeader));
        Assert.Null(Header(stub.LastRequest!, AiCallTag.AccountHeader));
        Assert.Null(Header(stub.LastRequest!, AiCallTag.ServiceTokenHeader));
    }

    [Fact]
    public void TheSystemTenant_AndANullTenant_NameNoAccount()
    {
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, Token);
        Assert.Null(GatewayAiCallTags.For(TenantId.System, AiFeature.Supervision).AccountTenantId);
        Assert.Null(GatewayAiCallTags.For((TenantId?)null, AiFeature.Supervision).AccountTenantId);
    }

    /// <summary>A hosted Gateway without the credential must not quietly record every account's usage under the
    /// key's owner - that silent misattribution is the defect this tag exists to end.</summary>
    [Fact]
    public void AnAccountTenant_WithoutTheServiceCredential_FailsLoudly_NamingTheSetting()
    {
        Environment.SetEnvironmentVariable(AccountNotifyByTenantClient.ServiceTokenEnvVar, null);
        var ex = Assert.Throws<InvalidOperationException>(() => GatewayAiCallTags.For(Account, AiFeature.TurnVerdict));
        Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAccount_CannotBeNamed_WithoutTheCredential_AndABadFeatureNameIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new AiCallTag(AiFeature.TurnVerdict, "some-tenant", null));
        Assert.Throws<ArgumentException>(() => new AiCallTag("Turn Verdict"));
        Assert.Throws<ArgumentException>(() => new AiCallTag(""));
    }

    [Fact]
    public void TheTag_NeverPrintsTheCredential_OrTheTenant()
    {
        var tag = new AiCallTag(AiFeature.TurnVerdict, Account.Value, Token);
        var text = tag.ToString();
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Account.Value, text, StringComparison.Ordinal);
        Assert.Contains("turn-verdict", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithFeature_KeepsTheAccount()
    {
        var judge = new AiCallTag(AiFeature.Transcription("dictation"), Account.Value, Token).WithFeature(AiFeature.DictationJudge);
        Assert.Equal("dictation.judge", judge.Feature);
        Assert.Equal(Account.Value, judge.AccountTenantId);
    }

    [Fact]
    public void TranscriptionFeatures_AreNamedBySurface()
    {
        Assert.Equal("transcription.dictation", AiFeature.Transcription("dictation"));
        Assert.Equal("transcription.notes", AiFeature.Transcription("notes"));
        Assert.Equal("transcription.gateway", AiFeature.Transcription(null));
        // Every surface the Gateway stamps today makes a valid name.
        foreach (var s in new[] { "dictation", "voice", "batch", "voice-test", "notes" })
            _ = new AiCallTag(AiFeature.Transcription(s));
    }

    [Fact]
    public async Task SpeechCarriesItsTag_OnEveryAttempt()
    {
        var stub = new StubHandler();
        using var http = new HttpClient(stub);
        using var resp = await TtsSynthesis.PostAsync(http, "https://devthrottle.com/api/v1/audio/speech", "dt_live_owner",
            new { model = "m", voice = "v", input = "hello", response_format = "mp3" }, 5, preferBackup: false,
            CancellationToken.None, new AiCallTag(AiFeature.VoiceSpeech));
        Assert.Equal("voice.speech", Header(stub.LastRequest!, AiCallTag.FeatureHeader));
    }

    /// <summary>
    /// EVERY PRODUCTION AI CLIENT IS BUILT WITH A TAG. The tag is an optional argument on the clients so the tests
    /// that build them bare keep compiling, which means the compiler cannot prove a new call site remembered it.
    /// This does: it reads the production source for every construction of the three clients and fails on one
    /// that passes no <c>tag:</c>. A call site added without a feature would otherwise be reported as "untagged"
    /// forever and nobody would know which feature it was.
    /// </summary>
    [Fact]
    public void EveryProductionConstruction_OfAnAiClient_PassesATag()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar).Any(d => d.Contains("Tests", StringComparison.Ordinal)))
            .ToList();
        var constructions = new Regex(@"new\s+(?:[\w.]+\.)?(HostedInferenceBrain|HostedCandidateJudge|BatchTranscriptionPipeline)\s*\(");
        var found = 0;
        var missing = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in constructions.Matches(text))
            {
                found++;
                var args = ArgumentsFrom(text, m.Index + m.Length - 1);
                if (!Regex.IsMatch(args, @"\btag\s*:"))
                    missing.Add($"{Path.GetRelativePath(src, file)}: {m.Value.Trim()}");
            }
        }

        // The scan must have found the clients it guards, or an empty pass would prove nothing.
        Assert.True(found >= 7, $"expected at least 7 production constructions of the AI clients, found {found}");
        Assert.True(missing.Count == 0, "AI clients built without a tag:\n" + string.Join("\n", missing));
    }

    // The text between the opening parenthesis at openIndex and its matching close.
    private static string ArgumentsFrom(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text.Substring(openIndex, i - openIndex + 1);
        }
        return text[openIndex..];
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "CcDirector.Core", "HostedAi", "AiCallTag.cs")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
