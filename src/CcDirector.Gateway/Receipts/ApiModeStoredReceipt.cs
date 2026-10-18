using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Receipts;

/// <summary>
/// A stored receipt with the position of the last event the store applied to it (the row's <c>LastGeneration</c>,
/// <c>LastGenerationStartedUtc</c> and <c>LastSequence</c>). The Gateway's own read path, for a live ruling that starts
/// from the store: an event at or before this position is one the store has already ruled on. The position never
/// travels to a client, which reads <see cref="ApiModeReceiptDto"/> alone.
/// </summary>
public sealed record ApiModeStoredReceipt(ApiModeReceiptDto Receipt, ApiModeEventPosition LastPosition);
