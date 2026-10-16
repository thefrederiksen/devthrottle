using System.Text.Json;

namespace CcDirector.Gateway.Contracts.ApiMode;

/// <summary>
/// The one set of serializer options for session events, used by the Director that writes them and by the
/// Gateway that stores and reads them. Pass <see cref="Options"/> to every <c>JsonSerializer</c> call on a
/// <see cref="SessionEvent"/>.
///
/// Why it exists: System.Text.Json reads a polymorphic record only when its <c>type</c> discriminator is the
/// first key, unless told otherwise, and a store that does not keep key order (PostgreSQL <c>jsonb</c> puts
/// the shortest keys first, so <c>at</c> comes back ahead of <c>type</c>) hands back events that cannot be
/// read. These options read the discriminator wherever it is. Read-only, so no caller can change them for
/// everyone else.
/// </summary>
public static class SessionEventJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { AllowOutOfOrderMetadataProperties = true };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
