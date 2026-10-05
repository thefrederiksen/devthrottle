using System.Collections.Generic;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /healthz response.
/// </summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";

    /// <summary>
    /// Fleet counts, or NULL when the responder has no honest number to give - in which case they are
    /// OMITTED from the JSON entirely rather than serialized as 0.
    ///
    /// Hosted Multi-Tenancy: /healthz is the PUBLIC unauthenticated liveness probe, so on the hosted
    /// Gateway it carries no credential and therefore has no tenant - and a fleet-GLOBAL count across every
    /// account is a cross-tenant disclosure. The aggregate is not computed there at all. These were plain
    /// <c>int</c> before, so "not computed" serialized as <c>0</c>: not a leak (0 is not the real count),
    /// but a FALSE statement rather than an absent one. /healthz is what every Director and endpoint
    /// selector dials, so a permanent 0 reads as a dead fleet. Absent is honest; zero is misleading.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Directors { get; set; }

    /// <inheritdoc cref="Directors"/>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Sessions { get; set; }
    public string Version { get; set; } = "";

    /// <summary>
    /// The exact source commit this running image was built from (the short SHA stamped into the container
    /// as the COCKPIT_COMMIT build argument), or NULL when the responder has no such stamp - in which case
    /// it is OMITTED from the JSON. Unlike <see cref="Version"/>, which is a hand-bumped product version that
    /// does NOT change per commit, this identifies the precise deployed build. The deploy pipeline reads it
    /// to tell the OLD container apart from the NEW one during a redeploy: it polls /healthz until this
    /// reports the commit it just shipped, which is the only honest signal that the new image is actually
    /// serving traffic (the old container answers 200 right up until it is recycled). Fleet-global build
    /// identity, identical for every tenant, so it carries no per-tenant fact and is safe on hosted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; set; }

    /// <summary>
    /// Per-SUBSYSTEM readiness: one entry per part of the Gateway that can be down on its own while the
    /// process serves, each either "available" or "unavailable". NULL when the responder computes none, in
    /// which case it is omitted rather than serialized as an empty object claiming everything is fine.
    ///
    /// WHY A LIVENESS PROBE CARRIES THIS. "The Gateway is up" and "the Gateway's pages work" are different
    /// statements, and on 2 September 2026 the difference cost hours: the deploy's own health poll saw a
    /// sustained 200 carrying the new commit and called the release good, while Your Throttle answered 503
    /// to every request and the owner's turns went unrecorded. Nothing in the pipeline was WRONG - it
    /// checked what it checked. It simply had no way to ask whether the parts behind the pages had come up,
    /// because the process reported one status for the whole of itself.
    ///
    /// So a subsystem that fails without taking the process down has to say so where the deploy can read
    /// it. The deploy asserts every entry here is "available" after the swap; see the "Prove every
    /// subsystem came up" step in deploy-hosted-gateway.yml.
    ///
    /// STATUS WORDS ONLY, AND DELIBERATELY NOTHING MORE. This endpoint is PUBLIC and unauthenticated. The
    /// reason a subsystem is down is a full operator sentence that names the database host, so it belongs
    /// on the authenticated feed and in the Gateway log - never here. A caller who can read this learns
    /// that statistics are down, which is a fact about our service, and nothing about our infrastructure.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Subsystems { get; set; }

    /// <summary>
    /// Whether this Gateway offers Directors set up for a team (devthrottle_internal#2311): true only on the hosted
    /// Gateway with Teams released (<c>CC_GATEWAY_TEAMS=1</c>), where <c>GET /devices/enroll-hosted/teams</c> and
    /// <c>POST /devices/enroll-hosted/move</c> are mapped. THE ONE SIGNAL a Director reads before it asks for teams:
    /// false, or absent (a Gateway from before teams, which never sends it, and a Director's own /healthz), means
    /// set up for the person's own account exactly as before. A process fact, the same for every tenant, so it is
    /// safe on this public endpoint. Not a subsystem: the deploy asserts every subsystem is "available".
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Teams { get; set; }

    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    /// <summary>Director's GUID. Empty when returned by the Gateway aggregator.</summary>
    public string? DirectorId { get; set; }

    /// <summary>OS host name reporting the response.</summary>
    public string? MachineName { get; set; }
}
