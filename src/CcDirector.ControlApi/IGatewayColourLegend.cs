using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The ONE seam through which this desktop asks its Gateway what every session colour MEANS
/// (<c>GET /gateway/session-colours</c>).
///
/// It is a seam of its own, beside <see cref="IGatewayHold"/>, for the reason that interface's own docs
/// give: the Gateway Cleanup mission is moving all Director-to-Gateway traffic onto the tunnel, and a
/// named seam makes that swap one line rather than a hunt. It is narrow on purpose - one read, no
/// writes: the legend is the Gateway's to write and this machine's only to render.
///
/// WHY ASK AT ALL, WHEN THE WORDS ARE IN THIS SOLUTION. <see cref="SessionColourLegend"/> compiles into
/// the desktop, so a compile-time read would be shorter and it would be wrong. A Director is routinely
/// older than its Gateway, and a legend baked into an old build explains the colours that build knows
/// rather than the ones its Gateway is sending - the exact version gap this mission exists to close. The
/// legend and the dots have to come from the same place, and that place is the Gateway.
/// </summary>
public interface IGatewayColourLegend
{
    /// <summary>
    /// Read the legend from the Gateway, or null when no Gateway is configured. Throws when one is
    /// configured and the read fails, so a caller that needs the real answer fails loud; the desktop's
    /// cache catches that and keeps its last-known words rather than inventing any.
    /// </summary>
    Task<SessionColourLegendDto?> GetSessionColourLegendAsync(CancellationToken ct = default);
}
