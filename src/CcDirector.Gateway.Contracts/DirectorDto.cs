namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Describes a running CC Director instance discovered by the Gateway.
/// Serialized to and from the instances/{guid}.json registration files
/// and to JSON for GET /directors.
/// </summary>
public sealed class DirectorDto
{
    /// <summary>Stable per-process GUID assigned by the Director on startup.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>OS process id of the Director.</summary>
    public int Pid { get; set; }

    /// <summary>UTC timestamp when the Director started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>HTTP base URL for the Director's internal control endpoint, e.g. http://127.0.0.1:55321 .</summary>
    public string ControlEndpoint { get; set; } = "";

    /// <summary>Hostname of the machine the Director is running on.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>OS user name the Director is running as.</summary>
    public string User { get; set; } = "";

    /// <summary>
    /// devthrottle_internal#1176: the instance's user-editable display name (e.g. "SOREN_NORTH_SLOT_2"),
    /// reported by the Director from its named-instance registry. Empty when the instance has no name
    /// (older builds, unnamed default) - clients fall back to <see cref="MachineName"/>. Cosmetic only.
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Director version string (informational).</summary>
    public string Version { get; set; } = "";

    /// <summary>The operating system the Director runs on (<see cref="DirectorOs"/>), from its Hello. Empty when
    /// the Director's build is too old to report it.</summary>
    public string Os { get; set; } = "";

    /// <summary>Schema version for forward-compat. Always 1 in v1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>UTC timestamp the Gateway last successfully reached this Director. Server-side only.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Cross-machine endpoint that browser users should be deeplinked to. Set by HTTP
    /// registration (the Director knows its own routable URL). Null for FSW-discovered
    /// Directors (use <see cref="ControlEndpoint"/> in that case).
    /// </summary>
    public string? TailnetEndpoint { get; set; }

    /// <summary>
    /// Issue #324: the Director's own declaration that it has NO reachable advertised
    /// endpoint (no tailnet identity resolved), with the reason naming the fix. Null when
    /// the endpoint is claimed reachable. While set, the Gateway must not probe the
    /// (empty) endpoint - the Director told us why it cannot answer.
    /// </summary>
    public string? EndpointUnreachableReason { get; set; }

    /// <summary>
    /// How this Director got into the registry. "file" = FSW-discovered same-machine.
    /// "http" = registered via POST /directors/register from anywhere on the network.
    /// Defaults to "file" for backward compat with existing JSON registration files.
    /// </summary>
    public string Source { get; set; } = "file";

    /// <summary>
    /// UTC timestamp of the last PASSING stream-leg verification.
    ///
    /// NOTHING WRITES THIS. It is always null on the wire. Its writer was the two-way verify handshake,
    /// which the tunnel-only cut deleted along with the Gateway's dial-back and the /verify-ws callback -
    /// liveness is now the tunnel connection itself. The Cockpit still READS it (DirectorDetailView), so
    /// it is kept rather than deleted: removing it would break a live client, and whether to restore the
    /// signal or drop the row is a product decision. See issue #1570.
    /// </summary>
    public DateTime? StreamVerifiedAt { get; set; }

    /// <summary>
    /// Why the last stream-leg verification FAILED.
    ///
    /// NOTHING WRITES THIS EITHER - see <see cref="StreamVerifiedAt"/> and issue #1570. Always null on
    /// the wire, so the Cockpit's "DOWN - WebSocket stream unreachable" branch can never fire: the row it
    /// feeds cannot report a problem, only silence.
    /// </summary>
    public string? StreamVerifyError { get; set; }

    /// <summary>Value of <see cref="AdvertisedEndpointState"/> when the last probe answered as this Director.</summary>
    public const string EndpointStateOk = "ok";

    /// <summary>Value of <see cref="AdvertisedEndpointState"/> when the advertised name stopped answering (or answered as the wrong process).</summary>
    public const string EndpointStateUnreachableByName = "unreachable-by-name";

    /// <summary>
    /// Issue #325: result of the Gateway's PERIODIC re-verification of the advertised
    /// endpoint (every heartbeat cycle, not just at registration like #223/#224).
    /// <see cref="EndpointStateOk"/> = the last probe answered /healthz as this Director;
    /// <see cref="EndpointStateUnreachableByName"/> = the Director is alive (heartbeating)
    /// but its advertised name no longer answers - distinct from heartbeat loss.
    /// Null = not probed yet, or not applicable (FSW-discovered local, or a #324 flagged
    /// no-endpoint registration). Server-side stamp; resets on re-register.
    /// </summary>
    public string? AdvertisedEndpointState { get; set; }

    /// <summary>UTC timestamp of the most recent advertised-endpoint probe (issue #325). Server-side stamp.</summary>
    public DateTime? AdvertisedEndpointCheckedAt { get; set; }

    /// <summary>UTC timestamp when the advertised endpoint STARTED failing probes (issue #325).
    /// Null while the state is not <see cref="EndpointStateUnreachableByName"/>.</summary>
    public DateTime? AdvertisedEndpointUnreachableSince { get; set; }

    /// <summary>Why the last advertised-endpoint probe failed (issue #325). Null while reachable.</summary>
    public string? AdvertisedEndpointError { get; set; }

    /// <summary>
    /// When this Director told the Gateway it was shutting down ON PURPOSE - the tunnel farewell
    /// (<c>DirectorHub.DirectorStopping</c>), stamped by <c>DirectorRegistry.MarkStopped</c>. Null while the
    /// Director is running, and null again from its next Hello.
    ///
    /// WHY THIS EXISTS. A Director's registration outlives the process by design: the entry is deliberately
    /// NOT dropped when its tunnel closes (a dead machine's cached roster has to survive a reconnect blip, and
    /// a Gateway-owned snooze still has to be able to fire against it), and the only thing that finally removes
    /// it is the eviction horizon a day later. The graceful <c>DELETE /directors/{id}/registration</c> that used
    /// to clear it belongs to the legacy same-machine discovery plane and is refused outright on hosted, so on
    /// the tunnel-only architecture NOTHING cleared a registration on shutdown. Every orderly stop therefore
    /// left an entry the Gateway went on expecting to reach for twenty-four hours, and reported as unreachable
    /// the whole time.
    ///
    /// The farewell already arrived - it just only told the work-history recorder. This is the same signal,
    /// read by discovery: a Director that said goodbye is NOT RUNNING, which is a different fact from one that
    /// cannot be reached, and only the second is a problem worth showing the owner. A Director that dies
    /// without saying goodbye still has no stamp here and still reads unreachable, which is exactly the case
    /// the owner wants to see.
    /// </summary>
    public DateTime? StoppedAtUtc { get; set; }
}
