using System.Reflection;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for the append-only factory activity record (Website Business Factory, product track). The
/// claims that matter: the store can append and read and do nothing else; a correction is a new row and the
/// row it corrects is untouched; a refused row writes nothing; filters, order and paging work; and one
/// tenant never sees another's rows.
/// </summary>
public sealed class FactoryActivityRecordTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private FactoryActivityRecord NewRecord() => new(_h.Open());

    private static AppendFactoryActivityRequest Row(
        string outcome = FactoryActivityOutcome.Done, string what = "Added an address to the remove-me list.",
        string factory = "website-business", string agent = "front-desk", Guid? corrects = null,
        DateTime? occurred = null) => new()
    {
        Factory = factory,
        FactoryAgent = agent,
        Outcome = outcome,
        What = what,
        Subject = "Pine Valley Plumbing",
        Link = "https://example.test/mail/123",
        CorrectsId = corrects,
        OccurredUtc = occurred,
    };

    private static int CountAll(FactoryActivityRecord record)
        => record.Query(limit: FactoryActivityRecord.MaxPageSize).Rows.Count;

    // ---------------------------------------------------------------------------------------------------
    // Append-only, by the type's own public surface.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Store_exposes_no_mutating_method_besides_Append()
    {
        var type = typeof(FactoryActivityRecord);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        // Append is the one write; Query is the read. Anything else appearing here - Update, Delete, Purge,
        // Correct, a retention sweep - breaks the record's one promise and must be argued for, not slipped in.
        Assert.Equal(new[] { "Append", "Query" }, methods);

        var settable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToArray();
        Assert.Empty(settable);
    }

    // ---------------------------------------------------------------------------------------------------
    // Append then query, and corrections.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Append_then_Query_returns_the_row_with_server_stamps()
    {
        var record = NewRecord();
        var before = DateTime.UtcNow;

        var written = record.Append(Row(), callingActor: "session:abc");

        Assert.NotEqual(Guid.Empty, written.Id);
        Assert.Equal("session:abc", written.Actor);
        Assert.True(written.RecordedUtc >= before);
        Assert.True(written.OccurredUtc >= before);

        var read = Assert.Single(record.Query().Rows);
        Assert.Equal(written.Id, read.Id);
        Assert.Equal("website-business", read.Factory);
        Assert.Equal("front-desk", read.FactoryAgent);
        Assert.Equal(FactoryActivityOutcome.Done, read.Outcome);
        Assert.Equal("Added an address to the remove-me list.", read.What);
        Assert.Equal("Pine Valley Plumbing", read.Subject);
        Assert.Equal("https://example.test/mail/123", read.Link);
        Assert.Null(read.CorrectsId);
    }

    [Fact]
    public void A_named_actor_wins_over_the_calling_session()
    {
        var record = NewRecord();
        var req = Row();
        req.Actor = "cc-website-factory";

        Assert.Equal("cc-website-factory", record.Append(req, callingActor: "session:abc").Actor);
    }

    [Fact]
    public void A_row_with_no_actor_and_no_caller_is_refused()
    {
        var record = NewRecord();
        var ex = Assert.Throws<FactoryActivityValidationException>(() => record.Append(Row(), callingActor: null));
        Assert.Contains("Who acted", ex.Message);
        Assert.Equal(0, CountAll(record));
    }

    [Fact]
    public void A_correction_is_a_new_row_pointing_at_the_old_one_and_the_old_one_is_unchanged()
    {
        var record = NewRecord();
        var original = record.Append(Row(what: "Sent the welcome email."), "session:abc");
        var originalBefore = record.Query().Rows.Single(r => r.Id == original.Id);

        var correction = record.Append(
            Row(outcome: FactoryActivityOutcome.Failed, what: "Correction: the welcome email bounced.", corrects: original.Id),
            "session:def");

        Assert.NotEqual(original.Id, correction.Id);
        Assert.Equal(original.Id, correction.CorrectsId);

        var rows = record.Query().Rows;
        Assert.Equal(2, rows.Count);
        var originalAfter = rows.Single(r => r.Id == original.Id);
        // Every field of the corrected row is exactly as it was written.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(originalBefore),
                     System.Text.Json.JsonSerializer.Serialize(originalAfter));
    }

    [Fact]
    public void A_correction_of_a_row_that_does_not_exist_is_refused_and_writes_nothing()
    {
        var record = NewRecord();
        var ex = Assert.Throws<FactoryActivityValidationException>(
            () => record.Append(Row(corrects: Guid.NewGuid()), "session:abc"));
        Assert.Contains("no factory activity row", ex.Message);
        Assert.Equal(0, CountAll(record));
    }

    // ---------------------------------------------------------------------------------------------------
    // Refusals write nothing.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_refused_outcome_lists_the_allowed_words_and_writes_nothing()
    {
        var record = NewRecord();
        record.Append(Row(), "session:abc");
        var before = CountAll(record);

        var ex = Assert.Throws<FactoryActivityValidationException>(() => record.Append(Row(outcome: "succeeded"), "session:abc"));

        Assert.Contains("'succeeded' is not a factory activity outcome", ex.Message);
        foreach (var word in FactoryActivityOutcome.All)
            Assert.Contains(word, ex.Message);
        Assert.Equal(before, CountAll(record));
    }

    [Fact]
    public void Every_listed_outcome_is_accepted()
    {
        var record = NewRecord();
        foreach (var word in FactoryActivityOutcome.All)
            record.Append(Row(outcome: word), "session:abc");
        Assert.Equal(FactoryActivityOutcome.All.Length, CountAll(record));
        Assert.Equal(10, FactoryActivityOutcome.All.Length);
    }

    [Fact]
    public void A_sentence_over_500_characters_is_refused_and_one_of_exactly_500_is_kept()
    {
        var record = NewRecord();
        Assert.Throws<FactoryActivityValidationException>(() => record.Append(Row(what: new string('a', 501)), "session:abc"));
        Assert.Equal(0, CountAll(record));

        record.Append(Row(what: new string('a', 500)), "session:abc");
        Assert.Equal(500, record.Query().Rows.Single().What.Length);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("agent")]
    [InlineData("what")]
    public void A_missing_required_field_is_refused(string missing)
    {
        var record = NewRecord();
        var req = Row();
        if (missing == "factory") req.Factory = " ";
        if (missing == "agent") req.FactoryAgent = null;
        if (missing == "what") req.What = "";
        Assert.Throws<FactoryActivityValidationException>(() => record.Append(req, "session:abc"));
        Assert.Equal(0, CountAll(record));
    }

    // ---------------------------------------------------------------------------------------------------
    // Filters, order, paging.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Query_filters_by_factory_agent_outcome_and_time_window()
    {
        var record = NewRecord();
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        record.Append(Row(agent: "front-desk", outcome: FactoryActivityOutcome.Done, occurred: t0), "s");
        record.Append(Row(agent: "front-desk", outcome: FactoryActivityOutcome.Blocked, occurred: t0.AddHours(1)), "s");
        record.Append(Row(agent: "builder", outcome: FactoryActivityOutcome.Done, occurred: t0.AddHours(2)), "s");
        record.Append(Row(factory: "other-factory", agent: "front-desk", occurred: t0.AddHours(3)), "s");

        Assert.Equal(3, record.Query(factory: "website-business").Rows.Count);
        Assert.Equal(3, record.Query(factoryAgent: "front-desk").Rows.Count);
        Assert.Single(record.Query(outcome: FactoryActivityOutcome.Blocked).Rows);
        // from inclusive, to exclusive.
        var window = record.Query(fromUtc: t0.AddHours(1), toUtc: t0.AddHours(3)).Rows;
        Assert.Equal(2, window.Count);
        Assert.All(window, r => Assert.InRange(r.OccurredUtc, t0.AddHours(1), t0.AddHours(2)));
    }

    [Fact]
    public void Query_orders_newest_first_by_default_and_oldest_first_on_request_and_pages()
    {
        var record = NewRecord();
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
            record.Append(Row(what: $"row {i}", occurred: t0.AddMinutes(i)), "s");

        Assert.Equal(new[] { "row 4", "row 3", "row 2", "row 1", "row 0" }, record.Query().Rows.Select(r => r.What));
        Assert.Equal(new[] { "row 0", "row 1", "row 2", "row 3", "row 4" }, record.Query(oldestFirst: true).Rows.Select(r => r.What));

        var first = record.Query(offset: 0, limit: 2);
        Assert.Equal(new[] { "row 4", "row 3" }, first.Rows.Select(r => r.What));
        Assert.True(first.HasMore);
        var last = record.Query(offset: 4, limit: 2);
        Assert.Equal(new[] { "row 0" }, last.Rows.Select(r => r.What));
        Assert.False(last.HasMore);
    }

    [Fact]
    public void Query_refuses_an_unknown_outcome_filter_and_a_bad_page()
    {
        var record = NewRecord();
        Assert.Throws<FactoryActivityValidationException>(() => record.Query(outcome: "succeeded"));
        Assert.Throws<FactoryActivityValidationException>(() => record.Query(offset: -1));
        Assert.Throws<FactoryActivityValidationException>(() => record.Query(limit: 0));
        Assert.Throws<FactoryActivityValidationException>(() => record.Query(limit: FactoryActivityRecord.MaxPageSize + 1));
    }

    // ---------------------------------------------------------------------------------------------------
    // Tenant isolation.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Another_tenants_query_does_not_see_the_row_and_cannot_correct_it()
    {
        var alpha = new FactoryActivityRecord(_h.Open(new FixedTenantContext(new TenantId("alpha"))));
        var beta = new FactoryActivityRecord(_h.Open(new FixedTenantContext(new TenantId("beta"))));

        var alphaRow = alpha.Append(Row(), "session:alpha");

        Assert.Single(alpha.Query().Rows);
        Assert.Empty(beta.Query().Rows);
        // A correction names a row by id; another tenant's id is "no such row", never a way in.
        Assert.Throws<FactoryActivityValidationException>(() => beta.Append(Row(corrects: alphaRow.Id), "session:beta"));
        Assert.Empty(beta.Query().Rows);
    }

    // ---------------------------------------------------------------------------------------------------
    // The switch as written in config.json.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"factoryAgents\":{}}", false)]
    [InlineData("{\"factoryAgents\":{\"enabled\":false}}", false)]
    [InlineData("{\"factoryAgents\":{\"enabled\":\"true\"}}", false)]
    [InlineData("{\"factoryAgents\":true}", false)]
    [InlineData("{\"factoryAgents\":{\"enabled\":true}}", true)]
    public void The_switch_is_on_only_for_a_boolean_true(string json, bool expected)
    {
        Assert.Equal(expected, FactoryAgentsConfig.Parse(JsonNode.Parse(json) as JsonObject));
    }
}
