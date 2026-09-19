namespace CcDirector.Reclaim.Rules;

/// <summary>
/// The proof a rule holds. There are exactly three, the engine implements exactly these three, and a
/// rule may hold no other.
///
/// This is the one idea the whole tool is built on. Every other disk cleaner in the world either
/// shows a map and leaves the judgement to the person, or deletes by path patterns that "look like
/// cache". Neither is safe to hand to an agent. A rule here may only recommend removing something
/// when it holds one of these three proofs, and anything matched by no rule is reported as
/// unclassified and never offered for removal. It is an allow-list: the tool enumerates what to
/// remove, never what to skip.
/// </summary>
public enum ProofKind
{
    /// <summary>
    /// A record says nothing needs it. The system keeps its own record of what it still needs, and
    /// the item is not in it. Windows records, for every installed product and patch, the cached
    /// package it needs to repair or uninstall it; a package nothing points at is an orphan.
    /// </summary>
    SystemRecord,

    /// <summary>
    /// The owner of the data has its own cleanup command. The tool that created the data ships a
    /// command that clears it, and that command is what runs - we never delete inside it ourselves,
    /// because only that tool knows what its own store still needs.
    /// </summary>
    OwnersOwnCommand,

    /// <summary>
    /// We made it, by an exact name, and it is old and closed. A folder whose name matches a pattern
    /// DevThrottle's own code creates, older than the rule's age gate, with no file in it open.
    /// </summary>
    WeMadeIt
}
