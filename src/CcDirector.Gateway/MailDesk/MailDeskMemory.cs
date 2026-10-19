using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE MEMORY THE GATEWAY HANDS THE SCREENING CHECK, AND HOW WHAT COMES BACK IS KEPT (Mail Desk phase 3 contract,
/// section 4). Pure: every fact arrives as an argument.
///
/// The memory is kept on the Gateway (<c>factory_check_state</c>) BY CHANNEL ID: one JSON object whose <c>channels</c>
/// list holds one entry per channel any machine has checked. What a Director is handed is MADE UP from it:
/// <list type="bullet">
/// <item>the account's active channels whose machine list names the Director's machine, in creation order;</item>
/// <item>each entry carries the Gateway's fields (<c>id</c>, <c>address</c>, <c>provider</c>, <c>mode</c>), taken from the
/// channel row now, and the check's own fields (<c>cursor</c>, <c>sentCache</c>, <c>keys</c>) and <c>filingQueue</c>
/// exactly as the kept memory last held them for that id;</item>
/// <item>a channel the kept memory has never seen gets only the Gateway's fields, so the check takes it as a first run.</item>
/// </list>
///
/// What comes back is MERGED, never stored whole: the channels this machine was handed take what the check returned;
/// a channel NOT handed to it (it names other machines only) keeps its kept entry untouched, so a backup machine
/// (phase 5) never erases another machine's cursor; a removed channel's entry is kept for
/// <see cref="RemovedKeptFor"/> and then dropped. A memory over <see cref="MaxBytes"/> is not kept.
/// </summary>
public static class MailDeskMemory
{
    /// <summary>The most the kept memory may hold: the Director keeps no memory above this (phase 2, section 2).</summary>
    public const int MaxBytes = TriggerCheckLimits.StateCapBytes;

    /// <summary>How long a removed channel's kept fields stay in the memory before they are dropped.</summary>
    public static readonly TimeSpan RemovedKeptFor = TimeSpan.FromDays(30);

    /// <summary>The check's own fields of a channel entry: what a model failure on one mailbox writes back as it was.</summary>
    public static readonly IReadOnlyList<string> CheckFields = new[] { "cursor", "sentCache", "keys" };

    /// <summary>The fields the kept memory carries into a handed entry: the check's own, and the filing queue.</summary>
    public static readonly IReadOnlyList<string> KeptFields = new[] { "cursor", "sentCache", "keys", "filingQueue" };

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The memory handed to a Director on <paramref name="machine"/>: the active channels that name it, each with its
    /// kept fields. No channel naming the machine hands an empty <c>channels</c> list, so the check fails with phase 2's
    /// "no channels" sentence.
    /// </summary>
    /// <param name="kept">The kept memory, or null when nothing was kept yet.</param>
    /// <param name="activeChannels">The account's active channels, in creation order.</param>
    /// <param name="machine">The machine of the Director being handed the assignment.</param>
    public static string Hand(string? kept, IReadOnlyList<MailDeskChannelEntity> activeChannels, string machine)
    {
        ArgumentNullException.ThrowIfNull(activeChannels);
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        var entries = Entries(kept);
        var handed = new JsonArray();
        foreach (var channel in HandedTo(activeChannels, machine))
            handed.Add(HandedEntry(channel, entries.TryGetValue(channel.Id, out var entry) ? entry : null));
        return new JsonObject { ["channels"] = handed }.ToJsonString(Compact);
    }

    /// <summary>The active channels whose machine list names <paramref name="machine"/> (case-insensitive), in order.</summary>
    public static IReadOnlyList<MailDeskChannelEntity> HandedTo(IReadOnlyList<MailDeskChannelEntity> activeChannels, string machine)
        => activeChannels
            .Where(c => c.RemovedUtc is null
                        && MailDeskChannelStore.Machines(c).Any(m => string.Equals(m, machine, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>
    /// Merge what the check returned into the kept memory. Answers the memory to keep, or why none may be kept - a
    /// refusal is a defect in the check or a memory too large, and the run that brought it keeps nothing.
    /// </summary>
    /// <param name="kept">The kept memory before this run, or null.</param>
    /// <param name="returned">The memory the check wrote back (the report's state).</param>
    /// <param name="handedIds">The channels this machine was handed for this run.</param>
    /// <param name="writtenBackAsKept">Channels whose check fields are written back exactly as <paramref name="kept"/>
    /// held them, so the check reads their same mail again (a model failure on that mailbox, section 1).</param>
    /// <param name="allChannels">Every channel of the account, removed ones included - to drop a removed channel's
    /// entry after <see cref="RemovedKeptFor"/>.</param>
    /// <param name="nowUtc">Now, for the removed channels' 30 days.</param>
    public static MailDeskMemoryMerge Merge(string? kept, string? returned, IReadOnlyCollection<Guid> handedIds,
        IReadOnlyCollection<Guid> writtenBackAsKept, IReadOnlyList<MailDeskChannelEntity> allChannels, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(handedIds);
        ArgumentNullException.ThrowIfNull(writtenBackAsKept);
        ArgumentNullException.ThrowIfNull(allChannels);
        if (string.IsNullOrWhiteSpace(returned))
            return MailDeskMemoryMerge.Refused("the screening check wrote back no memory, so none was kept");

        Dictionary<Guid, JsonObject> back;
        try
        {
            back = Entries(returned);
        }
        catch (InvalidOperationException ex)
        {
            return MailDeskMemoryMerge.Refused($"the screening check wrote back a memory the Gateway cannot read ({ex.Message}), so none was kept");
        }
        foreach (var id in handedIds)
            if (!back.ContainsKey(id))
                return MailDeskMemoryMerge.Refused($"the screening check left channel {id} out of the memory it wrote back, so none was kept");
        foreach (var id in back.Keys)
            if (!handedIds.Contains(id))
                return MailDeskMemoryMerge.Refused($"the screening check wrote back channel {id}, which it was not handed, so no memory was kept");

        var before = Entries(kept);
        var byId = allChannels.ToDictionary(c => c.Id);
        var merged = new Dictionary<Guid, JsonObject>();
        foreach (var (id, entry) in before)
        {
            if (!byId.TryGetValue(id, out var channel)) continue;          // a channel this account no longer has
            if (channel.RemovedUtc is { } removed && Utc(nowUtc) - Utc(removed) >= RemovedKeptFor) continue;
            merged[id] = (JsonObject)entry.DeepClone();
        }
        foreach (var id in handedIds)
        {
            var entry = (JsonObject)back[id].DeepClone();
            if (writtenBackAsKept.Contains(id))
            {
                var was = before.TryGetValue(id, out var keptEntry) ? keptEntry : null;
                foreach (var field in CheckFields)
                {
                    entry.Remove(field);
                    if (was?[field] is { } value) entry[field] = value.DeepClone();
                }
            }
            merged[id] = entry;
        }

        var ordered = new JsonArray();
        foreach (var channel in allChannels.Where(c => merged.ContainsKey(c.Id)))
            ordered.Add(merged[channel.Id]);
        var text = new JsonObject { ["channels"] = ordered }.ToJsonString(Compact);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBytes)
            return MailDeskMemoryMerge.Refused(
                $"the Mail Desk memory would be {bytes.ToString(CultureInfo.InvariantCulture)} bytes, over the {MaxBytes.ToString(CultureInfo.InvariantCulture)} the check can hold, so it was not written; remove a channel or give one to another machine");
        return new MailDeskMemoryMerge(text, null);
    }

    /// <summary>The kept memory's entries by channel id. Null or blank is no entries; anything else that is not the
    /// shape this class writes throws, because only this class writes it.</summary>
    internal static Dictionary<Guid, JsonObject> Entries(string? memory)
    {
        var entries = new Dictionary<Guid, JsonObject>();
        if (string.IsNullOrWhiteSpace(memory)) return entries;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(memory);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the memory is not JSON: {ex.Message}");
        }
        if (root is not JsonObject obj || obj["channels"] is not JsonArray channels)
            throw new InvalidOperationException("the memory is not an object with a channels list");
        foreach (var node in channels)
        {
            if (node is not JsonObject entry)
                throw new InvalidOperationException("a channel entry of the memory is not an object");
            var idText = entry["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (!Guid.TryParse(idText, out var id))
                throw new InvalidOperationException($"a channel entry of the memory has no channel id ('{idText}')");
            if (!entries.TryAdd(id, entry))
                throw new InvalidOperationException($"channel {id} is in the memory twice");
        }
        return entries;
    }

    private static JsonObject HandedEntry(MailDeskChannelEntity channel, JsonObject? kept)
    {
        var entry = new JsonObject
        {
            ["id"] = channel.Id.ToString("D"),
            ["address"] = channel.Address,
            ["provider"] = channel.Provider,
            ["mode"] = channel.Mode,
        };
        if (kept is not null)
            foreach (var field in KeptFields)
                if (kept[field] is { } value)
                    entry[field] = value.DeepClone();
        return entry;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>What <see cref="MailDeskMemory.Merge"/> came to: the memory to keep, or why none may be kept.</summary>
public sealed record MailDeskMemoryMerge(string? Memory, string? Refusal)
{
    public static MailDeskMemoryMerge Refused(string why) => new(null, why);
}
