namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /healthz response.
/// </summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";
    public int Directors { get; set; }
    public int Sessions { get; set; }
    public string Version { get; set; } = "";
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>Director's GUID. Empty when returned by the Gateway aggregator.</summary>
    public string? DirectorId { get; set; }

    /// <summary>OS host name reporting the response.</summary>
    public string? MachineName { get; set; }
}
