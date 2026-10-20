namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The limits of the hook-firings post (wire contract W5), held in ONE place because both ends read them
/// (Tech Lead ruling P17): the Director's forwarder sizes its batches by this, and the Gateway refuses a post
/// larger than it. Two copies would drift, and a Director batch larger than the Gateway's limit would be a
/// post refused on every sweep.
/// </summary>
public static class HookFiringLimits
{
    /// <summary>The most decisions one post may carry. A post over it is refused WHOLE, never decision by
    /// decision: the Director deletes a decision refused by id, so a per-decision refusal for being past the
    /// limit would destroy real records, while a whole refusal is a failed post and every drop is kept.</summary>
    public const int MaxDecisionsPerPost = 100;
}
