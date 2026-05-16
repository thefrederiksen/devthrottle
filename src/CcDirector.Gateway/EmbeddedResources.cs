using System.Reflection;

namespace CcDirector.Gateway;

internal static class EmbeddedResources
{
    private static readonly Assembly Asm = typeof(EmbeddedResources).Assembly;

    public static string Load(string fileName)
    {
        var resourceName = "CcDirector.Gateway.Web." + fileName;
        using var stream = Asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}. " +
                "Available: " + string.Join(", ", Asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
