using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHO CHECKS A MAILBOX, AND WHERE ITS SIGN-IN IS KEPT, FOLDED ONCE (mission "Zoho Mail Skill", section 4; repository
/// rule 7). The Mail Desk page shows, for every mailbox, "Checked by" - its main machine and its backup machines, or that
/// it has none and how to add one - a "?" that explains how a check works, and "Sign-in held". Every one of those words is
/// decided here; a client renders them verbatim.
///
/// WHICH MACHINE IS THE MAIN ONE. A mailbox is read by the Director that holds the Mail Desk start's claim, and only if
/// the channel names that Director's machine (<see cref="MailDeskMemory.HandedTo"/>). The start runs on its own machine
/// first and fails over to its backups in order (<see cref="TriggerMachines"/>). So the main machine of a mailbox is the
/// first machine on the start's list that the channel names, and its backups are the others the channel names, in the
/// start's order. The order the channel lists its machines in decides nothing, and is never claimed. A machine the
/// channel names that is not on the start's list never checks the mailbox, and the card says so.
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
        var checking = named
            .Select(m => (Machine: m, Position: TriggerMachines.PositionOf(start, m)))
            .Where(m => m.Position >= 0)
            .OrderBy(m => m.Position)
            .Select(m => m.Machine)
            .ToList();
        var never = named.Where(m => TriggerMachines.PositionOf(start, m) < 0).ToList();

        if (checking.Count == 0)
        {
            return new MailDeskCheckedByRow("No machine checks it",
                $"This mailbox names {And(never)}, and the Mail Desk check runs only on {And(order)}. "
                + "Ask your agent to name one of those machines on this mailbox.",
                MailDeskChannelFold.Tones.Bad, help);
        }

        var label = $"{checking[0]} (main)" + string.Concat(checking.Skip(1).Select(b => $", {b} (backup)"));
        if (never.Count > 0)
        {
            return new MailDeskCheckedByRow(label,
                $"{And(never)} never {(never.Count == 1 ? "checks" : "check")} it: the Mail Desk check does not run there. "
                + "Ask your agent to add it to the check, or take it off this mailbox.",
                MailDeskChannelFold.Tones.Bad, help);
        }
        return checking.Count == 1
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
           + "check this mailbox. If that machine is off, a backup machine checks it. The sign-in for this mailbox is "
           + $"{KeeperOf(channel, provider).ToLowerInvariant()} on those machines, never on the Gateway.";

    /// <summary>Who keeps the sign-in on the machine, as the start of a sentence. A provider with none is refused.</summary>
    private static string KeeperOf(Guid channel, string provider)
        => MailDeskWords.Providers.SignInKeepers.TryGetValue(provider, out var keeper)
            ? keeper
            : throw new InvalidOperationException($"Mail Desk channel {channel} holds the provider '{provider}', which names no keeper of its sign-in.");

    private static string And(IReadOnlyList<string> names)
        => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}

/// <summary>
/// "Checked by" on a mailbox card: the machines in words, a note when something is missing or wrong (null when nothing
/// is), the note's tone (null with no note), and the words behind the "?".
/// </summary>
public sealed record MailDeskCheckedByRow(string Label, string? Note, string? NoteTone, string Help);
