using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The desktop's way to reach the workspaces (issue #2722). Four calls, and every one of them goes to the
/// Gateway - there is no local store behind this and no local copy to fall back to.
///
/// The interface exists so the dialogs can be driven in a test without a Gateway, not so a second
/// implementation can quietly serve workspaces from somewhere else. A workspace read from this machine
/// would be exactly the thing the move to the Gateway removed: a record that vanishes with the machine it
/// describes.
/// </summary>
public interface IWorkspaceCatalog
{
    /// <summary>Every workspace, newest first.</summary>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<WorkspaceSummaryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>One workspace document, or null when there is none with that id.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WorkspaceDocument?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Create or replace a workspace.</summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct = default);

    /// <summary>Delete a workspace. False when there was none with that id.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="ct">Cancellation.</param>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// The real catalog: the Director's own Gateway connection.
///
/// When this Director has no Gateway configured, every call throws with the reason and the fix named,
/// rather than returning an empty list. An empty list reads as "you have no workspaces", which is a lie
/// that would look exactly like the user's saved work having been deleted by the upgrade.
/// </summary>
public sealed class GatewayWorkspaceCatalog : IWorkspaceCatalog
{
    private readonly ControlApiHost? _host;

    /// <param name="host">The Director's Control API host, which owns the Gateway client. Null when the
    /// Director is running without one.</param>
    public GatewayWorkspaceCatalog(ControlApiHost? host)
    {
        _host = host;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceSummaryDto>> ListAsync(CancellationToken ct = default)
        => await Require(_host?.ListWorkspacesAsync(ct));

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetAsync(string id, CancellationToken ct = default)
        => Require(_host?.GetWorkspaceAsync(id, ct));

    /// <inheritdoc />
    public Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct = default)
        => Require(_host?.SaveWorkspaceAsync(doc, ct));

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        => Require(_host?.DeleteWorkspaceAsync(id, ct));

    private static Task<T> Require<T>(Task<T>? task)
        => task ?? Task.FromException<T>(new System.InvalidOperationException(
            "Workspaces are stored on the Gateway, and this Director is not connected to one. " +
            "Connect it to a Gateway in Settings, then open this again."));
}
