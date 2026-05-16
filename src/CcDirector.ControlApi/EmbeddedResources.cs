using System.Reflection;

namespace CcDirector.ControlApi;

internal static class EmbeddedResources
{
    private static readonly Assembly Asm = typeof(EmbeddedResources).Assembly;

    public static string Load(string fileName)
    {
        // Embedded resource names are of the form <RootNamespace>.<Path with dots>
        var resourceName = "CcDirector.ControlApi.Web." + fileName;
        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "Did you forget to add it to the csproj? Available: " +
                string.Join(", ", Asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
