namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body for <c>POST /sessions/{sid}/hold</c>: park or un-park a session in the FIFO
/// voice queue. An empty body defaults to <see cref="OnHold"/> = true (the common case
/// is "hold this one"). Shared by the Director endpoint and the Gateway forwarder.
/// </summary>
public sealed class HoldRequest
{
    public bool OnHold { get; set; } = true;
}
