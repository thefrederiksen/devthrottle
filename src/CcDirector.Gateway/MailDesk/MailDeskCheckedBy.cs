using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHO CHECKS A MAILBOX, AND WHERE ITS SIGN-IN IS KEPT, FOLDED ONCE (mission "Zoho Mail Skill", section 4; repository
/// rule 7). The Mail Desk page shows, for every mailbox, "Checked by" - its main machine and its backup machines, or that
/// it has none and how to add one - a "?" that explains how a check works, and "Sign-in held". Every one of those words is
/// decided here; a client renders them verbatim.
///
/// WHICH MACHINE IS THE MAIN ONE. The Mail Desk start runs on its own machine whenever that machine is up, and moves
/// to its backups, in order, only while every machine before them is away: the start's own machine always takes the
/// claim back (<see cref="TriggerStore.ClaimForDirectorAsync"/>). The Director holding the claim reads only the mailboxes
/// that name its machine (<see cref="MailDeskMemory.HandedTo"/>). So, walking the start's list in order: the start's
/// own machine is a mailbox's main machine when the mailbox names it, and every later machine the mailbox names is a
/// backup. A machine on the start's list that the mailbox leaves out, ahead of one it names, is a gap: while that
/// machine holds the check the mailbox is not read, and the card says so. The order the channel lists its machines in
/// decides nothing. A machine the channel names that is not on the start's list never checks the mailbox.
/// </summary>
public static class MailDeskCheckedBy
{
    /// <summary>The note when a mailbox has a main machine and no backup.</summary>
    public const string NoBackupNote = "No backup machine. Ask your agent to add one.";

    /// <summary>What "Checked by" says when the account has no Mail Desk start: nothing runs the check yet.</summary>
    public const string NotPublishedNote = "Nothing checks it yet: the mail-desk factory is not published.";

    /// <summary>
    /// Fold who checks the mailbox of <paramref name="channel"/>. <paramref name="start"/> is the account's Mail Desk
    /// start, or null when the mail-desk factory is not published.
    /// </summary>
    public static MailDeskCheckedByRow Of(MailDeskChannelEntity channel, TriggerEntity? start)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var named = MailDeskChannelStore.Machines(channel);
        if (named.Count == 0)
            throw new InvalidOperationException($"Mail Desk channel {channel.Id} names no machine to read it.");
        var help = HelpOf(channel.Id, channel.Provider);

        if (start is null)
            return new MailDeskCheckedByRow(string.Join(", ", named), NotPublishedNote, MailDeskChannelFold.Tones.Neutral, help);

        var order = TriggerMachines.AllOf(start);
        bool Names(string machine) => named.Any(m => string.Equals(m, machine, StringComparison.OrdinalIgnoreCase));
        var never = named.Where(m => TriggerMachines.PositionOf(start, m) < 0).ToList();
        var lastNamed = order.ToList().FindLastIndex(m => Names(m));

        if (lastNamed < 0)
        {
            return new MailDeskCheckedByRow("No machine checks it",
                $"This mailbox names {And(never)}, and the Mail Desk check runs only on {And(order)}. "
                + "Ask your agent to name one of those machines on this mailbox.",
                MailDeskChannelFold.Tones.Bad, help);
        }

        // Walk the start's list up to the last machine the mailbox names: each is a reader (main or backup) or a gap.
        var readers = new List<string>();
        var gaps = new List<string>();
        for (var i = 0; i <= lastNamed; i++)
        {
            if (Names(order[i])) readers.Add($"{named.First(m => string.Equals(m, order[i], StringComparison.OrdinalIgnoreCase))} ({(i == 0 ? "main" : "backup")})");
            else gaps.Add(order[i]);
        }
        var label = string.Join(", ", readers);

        var problems = new List<string>();
        if (gaps.Count > 0)
            problems.Add($"While {Or(gaps)} {(gaps.Count == 1 ? "runs" : "run")} the Mail Desk check, this mailbox is not read, "
                         + $"because it does not name {(gaps.Count == 1 ? "that machine" : "those machines")}. "
                         + $"Ask your agent to add {And(gaps)} to this mailbox.");
        if (never.Count > 0)
            problems.Add($"{And(never)} never {(never.Count == 1 ? "checks" : "check")} it: the Mail Desk check does not run there. "
                         + $"Ask your agent to add {(never.Count == 1 ? "it" : "them")} to the check, or take {(never.Count == 1 ? "it" : "them")} off this mailbox.");
        if (problems.Count > 0)
            return new MailDeskCheckedByRow(label, string.Join(" ", problems), MailDeskChannelFold.Tones.Bad, help);

        return readers.Count == 1
            ? new MailDeskCheckedByRow(label, NoBackupNote, MailDeskChannelFold.Tones.Attention, help)
            : new MailDeskCheckedByRow(label, null, null, help);
    }

    /// <summary>
    /// Where the mailbox's sign-in is kept, naming every machine the channel names: each of them needs it, because each
    /// may be the one that checks.
    /// </summary>
    public static string SignInHeldOf(MailDeskChannelEntity channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var named = MailDeskChannelStore.Machines(channel);
        if (named.Count == 0)
            throw new InvalidOperationException($"Mail Desk channel {channel.Id} names no machine to read it.");
        return $"{KeeperOf(channel.Id, channel.Provider)} on {And(named)}, not on the Gateway";
    }

    /// <summary>The words behind the "?" beside "Checked by": how a check works, and where this provider's sign-in is kept.</summary>
    internal static string HelpOf(Guid channel, string provider)
        => "The Gateway does not read your mail. On every Mail Desk check it asks the Director on the main machine to "
           + "check this mailbox. While that machine is off, the check moves to the next machine on its list, which reads "
           + "this mailbox if the mailbox names it: a backup. The sign-in for this mailbox is "
           + $"{KeeperOf(channel, provider).ToLowerInvariant()} on those machines, never on the Gateway.";

    /// <summary>Who keeps the sign-in on the machine, as the start of a sentence. A provider with none is refused.</summary>
    private static string KeeperOf(Guid channel, string provider)
        => MailDeskWords.Providers.SignInKeepers.TryGetValue(provider, out var keeper)
            ? keeper
            : throw new InvalidOperationException($"Mail Desk channel {channel} holds the provider '{provider}', which names no keeper of its sign-in.");

    private static string And(IReadOnlyList<string> names) => Join(names, "and");

    private static string Or(IReadOnlyList<string> names) => Join(names, "or");

    private static string Join(IReadOnlyList<string> names, string word)
        => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + $" {word} " + names[^1];
}

/// <summary>
/// "Checked by" on a mailbox card: the machines in words, a note when something is missing or wrong (null when nothing
/// is), the note's tone (null with no note), and the words behind the "?".
/// </summary>
public sealed record MailDeskCheckedByRow(string Label, string? Note, string? NoteTone, string Help);
