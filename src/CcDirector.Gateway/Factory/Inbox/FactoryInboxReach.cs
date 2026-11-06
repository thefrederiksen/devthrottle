using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// WHICH FACTORIES HAVE AN INBOX, ONCE (Factory Inbox mission, section 3): "Every factory with a Boss seat gets an inbox,
/// defined or not. Refused in words: unknown, archived, or Boss-less factory." The Inbox list, the wake, and the factory
/// page's "Leave a message" all ask this one question, so a factory can never be listed where its send is refused, or be
/// offered a message the Gateway will not take. The send itself names WHICH of the two reasons applies, in its own words
/// (<c>FactoryInboxEndpoints.RefuseOwnerSend</c>), and checks the same two facts.
/// </summary>
public static class FactoryInboxReach
{
    /// <summary>The label of the factory page's button that opens the composer for that factory.</summary>
    public const string LeaveMessageLabel = "Leave a message";

    /// <summary>True when the factory takes messages: it is not archived and it names a Boss seat.</summary>
    public static bool TakesMessages(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.ArchivedAtUtc is null && !string.IsNullOrWhiteSpace(factory.BossSeat);
    }
}
