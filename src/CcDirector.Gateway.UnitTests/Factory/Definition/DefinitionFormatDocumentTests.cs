using System.Text.Json;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// The people's page about the format, <c>docs/factories/definition-format.md</c>, is written by hand from the design.
/// This keeps it with the schemas: every key a schema names must appear on the page, written as code, so a key added
/// to a schema without a word for people fails here.
/// </summary>
public sealed class DefinitionFormatDocumentTests
{
    [Fact]
    public void Document_NamesEveryKeyTheSchemasTake()
    {
        var path = Path.Combine(RepositorySourceIndex.Root, "docs", "factories", "definition-format.md");
        Assert.True(File.Exists(path), $"{path} is missing.");
        var text = File.ReadAllText(path);

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<DefinitionFileKind>())
            Collect(DefinitionSchema.For(kind).Root, keys);

        Assert.True(keys.Count > 30, $"Only {keys.Count} keys were read from the schemas; the walk is broken.");
        var missing = keys.Where(k => !text.Contains($"`{k}`", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "docs/factories/definition-format.md does not name: " + string.Join(", ", missing));
    }

    private static void Collect(JsonElement schema, ISet<string> keys)
    {
        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
            {
                keys.Add(p.Name);
                Collect(p.Value, keys);
            }
        }
        if (schema.TryGetProperty("items", out var items)) Collect(items, keys);
    }
}
