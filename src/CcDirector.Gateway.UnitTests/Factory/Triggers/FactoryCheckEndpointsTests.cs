using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// WHERE A DIRECTOR GETS A CHECK FROM (Factory Definition, phase 5b, design 2.2): the download route answers only a
/// PUBLISHED version, only its <c>checks/</c> and <c>files/</c>, only for a Director whose machine has a seat of that
/// factory - each refusal with its sentence. And the report route reads a report carrying 16 KB of each output and
/// 64 KB of state. Through the real Map functions, in memory (no port), on the real definition store over SQLite.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryCheckEndpointsTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private const string Factory = DefinitionFixture.FactoryId;

    // The fixture's seats: boss and writer on the default machine SOREN_NORTH, outreach on SOREN_SOUTH.
    private static readonly Dictionary<string, string> Directors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dir-north"] = "SOREN_NORTH", ["dir-south"] = "soren_south", ["dir-elsewhere"] = "LAPTOP_2",
    };

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly FactoryDefinitionStore _definitions;
    private readonly TriggerStore _triggers;

    public FactoryCheckEndpointsTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        _definitions = new FactoryDefinitionStore(_db);
        _triggers = new TriggerStore(_db);

        FactoryCheckEndpoints.Map(_routes, _ => T, directorMachine: (_, id) => Directors.GetValueOrDefault(id), _definitions);

        var service = new TriggerService(_triggers, new FactoryActivityRecord(_db),
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no machine in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => Now, startLifetime: CancellationToken.None);
        TriggerEndpoints.Map(_routes, _ => T, service, directorMachine: (_, id) => Directors.GetValueOrDefault(id),
            nowUtc: () => Now, sessionFactoryOf: _ => SessionFactoryLookup.NotKnown, isDefined: _definitions.IsDefined);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    /// <summary>Version 1 saved as a draft and, unless told otherwise, published; version 2 a draft above it.</summary>
    private void Seed(bool publishVersion1 = true, bool draftVersion2 = false)
    {
        Assert.True(_definitions.SaveDraft(T, Factory, DefinitionFixture.Valid(), 0, FactoryMemoryAuthorKinds.Person,
            "owner", "first", Now).Ok);
        if (publishVersion1)
            Assert.True(_definitions.Publish(T, Factory, 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
        if (draftVersion2)
            Assert.True(_definitions.SaveDraft(T, Factory, DefinitionFixture.With("checks/new_mail.py", "print('v2')\n"), 1,
                FactoryMemoryAuthorKinds.Person, "owner", "second", Now).Ok);
    }

    // ---------- the download ----------

    [Theory]
    [InlineData("dir-north")]
    [InlineData("dir-south")]
    public async Task Get_APublishedVersion_ADirectorWithASeatOnItsMachine_AnswersOnlyChecksAndFiles(string director)
    {
        Seed();

        var (status, body) = await Send("GET", $"/directors/{director}/factory-checks/{Factory}/1");

        Assert.Equal(200, status);
        var answer = body.Deserialize<FactoryCheckFilesResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Factory, answer.Factory);
        Assert.Equal(1, answer.Version);
        Assert.Equal(new[] { "checks/new_mail.py", "files/outreach_send.py" }, answer.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("print('{\"count\": 0}')\n", answer.Files["checks/new_mail.py"]);
    }

    [Fact]
    public async Task Get_ADraftVersion_IsRefusedWithTheSentence()
    {
        Seed(draftVersion2: true);

        var (status, body) = await Send("GET", $"/directors/dir-north/factory-checks/{Factory}/2");

        Assert.Equal(404, status);
        Assert.Equal("version_not_published", body.GetProperty("code").GetString());
        Assert.Equal("version 2 of the definition of devthrottle is a draft, not published; a check is only ever " +
                     "downloaded from a published version", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_AFactoryWithOnlyADraft_IsRefused()
    {
        Seed(publishVersion1: false);

        var (status, body) = await Send("GET", $"/directors/dir-north/factory-checks/{Factory}/1");

        Assert.Equal(404, status);
        Assert.Equal("version_not_published", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_AVersionThatDoesNotExist_IsRefused()
    {
        Seed();

        var (status, body) = await Send("GET", $"/directors/dir-north/factory-checks/{Factory}/9");

        Assert.Equal(404, status);
        Assert.Equal("version 9 of the definition of devthrottle does not exist, so it has no checks to download",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_ADirectorWhoseMachineHasNoSeatOfTheFactory_IsRefused()
    {
        Seed();

        var (status, body) = await Send("GET", $"/directors/dir-elsewhere/factory-checks/{Factory}/1");

        Assert.Equal(404, status);
        Assert.Equal("no_seat_on_machine", body.GetProperty("code").GetString());
        Assert.Equal("version 1 of the definition of devthrottle has no seat on LAPTOP_2, so Director 'dir-elsewhere' " +
                     "is not given its checks", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_ADirectorThisAccountDoesNotHave_IsRefused()
    {
        Seed();

        var (status, body) = await Send("GET", $"/directors/dir-stranger/factory-checks/{Factory}/1");

        Assert.Equal(404, status);
        Assert.Equal("director_not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_AFactoryNameThatIsNotOne_IsRefused()
    {
        var (status, body) = await Send("GET", "/directors/dir-north/factory-checks/not a factory!/1");

        Assert.Equal(404, status);
        Assert.Equal("factory_not_found", body.GetProperty("code").GetString());
    }

    // ---------- the report body ----------

    [Fact]
    public void ReportBodyLimit_FitsTwoFullOutputsAndAFullStateAtTheWorstEscaping()
    {
        // A control character is escaped as six bytes; that is the most any one character or byte can take.
        var report = new TriggerCheckReport
        {
            CheckedAtUtc = Now, ExitCode = 0, StartError = null,
            Output = new string('\u0001', TriggerCheckLimits.OutputCap),
            ErrorOutput = new string('\u0001', TriggerCheckLimits.OutputCap),
            State = new string('\u0001', TriggerCheckLimits.StateCapBytes),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(report).Length;

        Assert.True(bytes <= TriggerCheckLimits.ReportBodyLimitBytes, $"{bytes} bytes is over the {TriggerCheckLimits.ReportBodyLimitBytes} limit");
    }

    [Fact]
    public async Task Report_AFullSizeReport_IsReadAndRecorded()
    {
        var (created, error) = _triggers.Create(T, new TriggerDefinitionRequest
        {
            Name = "inbox", Factory = "warmforward", FactoryAgent = "outreach", Machine = "SOREN_NORTH",
            RepoPath = @"D:\work", CheckCommand = "python check.py",
            IntervalSeconds = 300, Prompt = "{count}",
        }, "the owner (test)", Now.AddDays(-1));
        Assert.True(created is not null, error);
        var report = JsonSerializer.Serialize(new TriggerCheckReport
        {
            CheckedAtUtc = Now, ExitCode = 0,
            Output = "{\"count\": 0}" + new string(' ', TriggerCheckLimits.OutputCap - 12),
            ErrorOutput = new string('e', TriggerCheckLimits.OutputCap),
            State = new string('s', TriggerCheckLimits.StateCapBytes),
        });

        var (status, body) = await Send("POST", $"/directors/dir-north/triggers/{created!.Id:D}/checks", report);

        Assert.True(status == 200, $"expected 200, got {status}: {body}");
        Assert.Equal("nothing-to-do", body.GetProperty("outcome").GetString());
    }

    // ---------- the in-memory pipeline ----------

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                     .OrderBy(e => e.RoutePattern.Parameters.Count))
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (matcher.TryMatch(path, values)) return (endpoint, values);
        }
        return null;
    }

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, string? json = null)
    {
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.RouteValues = matched!.Value.Values;
        var bytes = Encoding.UTF8.GetBytes(json ?? "");
        ctx.Request.Body = new MemoryStream(bytes);
        if (json is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }
}
