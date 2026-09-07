using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// Where the drain's record is captured and kept: the workspace object on the Gateway (issue #2722).
///
/// A seam for the same reason as <see cref="IDrainSessionControl"/> - the drain's behaviour has to be
/// provable without a Gateway - and for one more: the record MUST live off this machine. The one moment a
/// restart index is worth having is the moment the machine it describes has been restarted out from under
/// its fleet, so a drain with nowhere off-machine to write is a drain that refuses to start.
/// </summary>
public interface IDrainWorkspaceSink
{
    /// <summary>
    /// Fold this Director's live sessions into a new workspace document. The Gateway does the fold, from
    /// facts it already holds firsthand: a caller that assembled them is a caller that can get them wrong,
    /// and this document is read after the sessions are gone, when nobody can check.
    /// </summary>
    /// <param name="request">What to call it, which Director, and why the drain is being run.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WorkspaceDocument> CaptureAsync(WorkspaceCaptureRequest request, CancellationToken ct);

    /// <summary>Store the document as it stands. Called after every state change, not once at the end: a
    /// drain that only writes its record at the end has no record of a drain that was interrupted.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct);
}

/// <summary>The real sink, over the Director's existing outbound Gateway client.</summary>
public sealed class GatewayDrainWorkspaceSink : IDrainWorkspaceSink
{
    private readonly GatewayClient _client;

    /// <summary>Create the sink over a connected Gateway client.</summary>
    /// <param name="client">The Director's Gateway client.</param>
    public GatewayDrainWorkspaceSink(GatewayClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task<WorkspaceDocument> CaptureAsync(WorkspaceCaptureRequest request, CancellationToken ct)
        => _client.CaptureWorkspaceAsync(request, ct);

    /// <inheritdoc />
    public Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct)
        => _client.SaveWorkspaceAsync(doc, ct);
}
