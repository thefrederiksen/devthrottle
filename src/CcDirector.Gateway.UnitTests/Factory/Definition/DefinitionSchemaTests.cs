using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// The definition's JSON Schemas are the contract phase 3 serves, so they must be valid schema documents, refuse
/// unknown keys on every object, agree with the code's own constants, and never say more than the validator enforces.
/// </summary>
public sealed class DefinitionSchemaTests
{
    public static TheoryData<DefinitionFileKind> Kinds()
    {
        var data = new TheoryData<DefinitionFileKind>();
        foreach (var k in Enum.GetValues<DefinitionFileKind>()) data.Add(k);
        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Schema_IsADraft202012Document_WithAnIdAndATitle(DefinitionFileKind kind)
    {
        var schema = DefinitionSchema.For(kind);
        using var doc = JsonDocument.Parse(schema.Text);
        var root = doc.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.EndsWith("/" + DefinitionSchema.ResourceFileName(kind), root.GetProperty("$id").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Schema_EveryObjectSaysAdditionalPropertiesFalse(DefinitionFileKind kind)
    {
        var objects = 0;
        Walk(DefinitionSchema.For(kind).Root, s =>
        {
            if (s.GetProperty("type").GetString() != "object") return;
            objects++;
            Assert.Equal(JsonValueKind.False, s.GetProperty("additionalProperties").ValueKind);
        });
        Assert.True(objects >= 1);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Schema_UsesOnlyKeywordsTheValidatorImplements(DefinitionFileKind kind)
    {
        Walk(DefinitionSchema.For(kind).Root, s =>
        {
            foreach (var p in s.EnumerateObject())
                Assert.Contains(p.Name, DefinitionSchema.ImplementedKeywords);
        });
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Schema_EveryRequiredKeyIsAProperty(DefinitionFileKind kind)
    {
        Walk(DefinitionSchema.For(kind).Root, s =>
        {
            if (!s.TryGetProperty("required", out var required)) return;
            var props = s.GetProperty("properties");
            foreach (var r in required.EnumerateArray()) Assert.True(props.TryGetProperty(r.GetString()!, out _), r.GetString());
        });
    }

    [Fact]
    public void Load_UnimplementedKeyword_IsRefused()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "x", "type": "object", "additionalProperties": false,
              "properties": { "a": { "type": "string", "oneOf": [ { "type": "string" } ] } }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
        Assert.Contains("the keyword 'oneOf' is not implemented", ex.Message);
        Assert.Contains("#/properties/a", ex.Message);
    }

    [Theory]
    [InlineData("""{ "type": "array", "items": { "type": "string" }, "maxLength": 1 }""", "'maxLength' does nothing on type 'array'")]
    [InlineData("""{ "type": "array", "items": { "type": "string" }, "pattern": "x", "description": "d" }""", "'pattern' does nothing on type 'array'")]
    [InlineData("""{ "type": "string", "minItems": 5 }""", "'minItems' does nothing on type 'string'")]
    [InlineData("""{ "type": "string", "items": { "type": "string", "oneOf": [] } }""", "'items' does nothing on type 'string'")]
    [InlineData("""{ "type": "boolean", "enum": [true] }""", "'enum' does nothing on type 'boolean'")]
    public void Load_KeywordOnATypeThatDoesNotEnforceIt_IsRefused(string property, string expected)
    {
        // Review finding 3: an implemented keyword on the wrong type was accepted and did nothing.
        var schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "x", "type": "object", "additionalProperties": false,
              "properties": { "a": PROPERTY }
            }
            """.Replace("PROPERTY", property, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("#/properties/a", ex.Message);
    }

    [Fact]
    public void Load_UnimplementedType_IsRefused()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "x", "type": "object", "additionalProperties": false,
              "properties": { "n": { "type": "integer" } }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
        Assert.Contains("the type 'integer' is not implemented", ex.Message);
    }

    [Fact]
    public void Load_ObjectThatAllowsUnknownKeys_IsRefused()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "x", "type": "object",
              "properties": { "a": { "type": "string" } }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
        Assert.Contains("\"additionalProperties\": false", ex.Message);
    }

    [Fact]
    public void Load_PatternWithNoDescription_IsRefused()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "title": "x", "type": "object", "additionalProperties": false,
              "properties": { "a": { "type": "string", "pattern": "^a$" } }
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
        Assert.Contains("a pattern needs a description", ex.Message);
    }

    [Fact]
    public void Load_WrongDraft_IsRefused()
    {
        const string schema = """
            { "$schema": "http://json-schema.org/draft-07/schema#", "title": "x", "type": "object", "additionalProperties": false, "properties": {} }
            """;

        Assert.Throws<InvalidOperationException>(() => DefinitionSchema.Load("x.schema.json", schema));
    }

    [Fact]
    public void Schemas_AgentEnums_AreTheSupportedAgents()
    {
        var expected = Enum.GetValues<AgentKind>().Where(k => k != AgentKind.RawCli).Select(k => k.ToString()).ToList();
        Assert.Equal(expected, FactorySkillSupport.SupportedAgents);

        var factoryAgents = Prop(DefinitionFileKind.Factory, "defaults", "agent").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        var seatAgents = Prop(DefinitionFileKind.Seat, "agent").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(expected, factoryAgents);
        Assert.Equal(expected, seatAgents);
    }

    [Fact]
    public void Schemas_PurposeLimit_IsTheRegistrysOwn()
    {
        Assert.Equal(FactoryRegistryStore.MaxPurposeChars, Prop(DefinitionFileKind.Factory, "purpose").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void Schemas_IdLimits_AreFactoryNamesOwn()
    {
        Assert.Equal(FactoryNames.MaxFactoryLength, Prop(DefinitionFileKind.Factory, "factory").GetProperty("maxLength").GetInt32());
        Assert.Equal(FactoryNames.MaxFactoryLength, Prop(DefinitionFileKind.Factory, "bossSeat").GetProperty("maxLength").GetInt32());
        Assert.Equal(FactoryNames.MaxFactoryLength, Prop(DefinitionFileKind.Seat, "seat").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void Validate_ReportsEachProblemOnItsKeysLine()
    {
        var (root, error) = DefinitionDocument.ParseYaml("seats/x.yaml", "seat: x\nname: 5\nrole: R\nbrief: B\nextra: 1\n");
        Assert.Null(error);

        var problems = DefinitionSchema.For(DefinitionFileKind.Seat).Validate(root!);

        Assert.Equal(new[] { 2, 5 }, problems.Select(p => p.Line));
    }

    private static JsonElement Prop(DefinitionFileKind kind, params string[] path)
    {
        var s = DefinitionSchema.For(kind).Root;
        foreach (var p in path) s = s.GetProperty("properties").GetProperty(p);
        return s;
    }

    private static void Walk(JsonElement schema, Action<JsonElement> visit)
    {
        visit(schema);
        if (schema.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject()) Walk(p.Value, visit);
        if (schema.TryGetProperty("items", out var items)) Walk(items, visit);
    }
}

/// <summary>The factory-skill table: one row per agent a definition may name, each "not shown yet" until phase 0(c).</summary>
public sealed class FactorySkillSupportTests
{
    [Fact]
    public void Table_HasARowForEverySupportedAgent_EachNotShownYet()
    {
        foreach (var agent in Enum.GetValues<AgentKind>().Where(k => k != AgentKind.RawCli))
        {
            var answer = FactorySkillSupport.For(agent);
            Assert.False(answer.CanLoad);
            Assert.Equal($"Phase 0 has not shown that {agent} can load a factory skill, so a seat on {agent} cannot name one yet.", answer.Reason);
        }
    }

    [Fact]
    public void Table_RawCli_HasNoRow()
    {
        Assert.Throws<InvalidOperationException>(() => FactorySkillSupport.For(AgentKind.RawCli));
    }
}
