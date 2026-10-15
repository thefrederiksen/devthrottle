namespace CcDirector.ControlApi.Triggers;

/// <summary>What asking the Gateway for one version's checks came to: the files, or why there are none.</summary>
public sealed record FactoryCheckFetch(IReadOnlyDictionary<string, string>? Files, string? Refusal)
{
    public static FactoryCheckFetch Refused(string why) => new(null, why);
}

/// <summary>
/// Where a check from a definition comes from (Factory Definition, phase 5b): the Gateway's
/// <c>GET /directors/{directorId}/factory-checks/{factory}/{version}</c>, which answers only a PUBLISHED version, and
/// only its <c>checks/</c> and <c>files/</c>. Production is <see cref="GatewayClient"/>; tests pass a fake.
/// </summary>
public interface IFactoryCheckSource
{
    Task<FactoryCheckFetch> FetchFactoryChecksAsync(string directorId, string factory, int version, CancellationToken ct);
}
