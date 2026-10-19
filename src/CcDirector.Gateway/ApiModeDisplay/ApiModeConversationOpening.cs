using CcDirector.Gateway.ApiModeConversation;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Receipts;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// THE OPENING OF THE LIVE STREAM FOR A READER THAT HOLDS NOTHING (mission #4070, phase 2 follow-on): the session's
/// stored conversation (<see cref="ApiModeConversationStore"/>) with its stored receipts merged in, as one list of items
/// in the order a client applies them, and the ruling that reader continues with. Built from one snapshot, read under the
/// event feed's session lock (<see cref="IApiModeEventFeed.Snapshot"/>), so the stores reflect exactly the held events
/// handed in, and every event after them is one the reader is sent live: no gap and no double.
///
/// <list type="number">
/// <item>The ruling starts from the stored receipts and the stored conversation (so a tool still running keeps its
/// title when it finishes), and is caught up on the held events without anything being sent for them: their effect is
/// already in the stores. The one exception is a reply whose final is not stored yet - its deltas are folded into it, so
/// a reader joining mid-reply sees what has streamed so far.</item>
/// <item>The items are the stored ones in the Gateway's order, with each receipt placed by the position of the event that
/// created it (the order the receipt store reads them in; on a tie the receipt goes first). A row that only holds a
/// reply's place shows that reply's deltas so far, or nothing.</item>
/// <item>When the bound let older items go, the opening starts with <see cref="EarlierNotKeptWords"/>, and receipts placed
/// before the first kept item are left out of it (they stay on <c>GET /sessions/{sid}/receipts</c>), so the line is
/// truly first.</item>
/// </list>
///
/// The words are the Gateway's (the project rule 7); the client renders them.
/// </summary>
public static class ApiModeConversationOpening
{
    /// <summary>The item id of the line that says earlier conversation is not kept.</summary>
    public const string EarlierNotKeptItemId = "conversation:earlier-not-kept";

    /// <summary>The words of that line.</summary>
    public const string EarlierNotKeptWords = "Earlier conversation in this session is not kept.";

    /// <summary>
    /// The opening's items and the ruling to continue with. <paramref name="receipts"/> in the store's order with their
    /// places in line set, <paramref name="conversation"/> in the Gateway's order, <paramref name="held"/> every event the
    /// feed held at the snapshot, oldest first.
    /// </summary>
    public static (ApiModeLiveRuling Ruling, IReadOnlyList<ApiModeLiveItem> Items) Build(
        IReadOnlyList<ApiModeStoredReceipt> receipts,
        IReadOnlyList<ApiModeStoredConversationItem> conversation,
        IReadOnlyList<ApiModeHeldEvent> held)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(held);

        var ruling = new ApiModeLiveRuling(receipts, conversation.Where(c => c.HasFinal).Select(c => c.Item));
        var finals = conversation.Where(c => c.HasFinal).Select(c => c.Item.ItemId).ToHashSet(StringComparer.Ordinal);
        var streaming = new Dictionary<string, ApiModeLiveItem>(StringComparer.Ordinal);
        var streamingOrder = new List<string>();
        foreach (var e in held)
            foreach (var item in ruling.Rule(e))
            {
                if (item.Mode != ApiModeLiveItemModes.Append || finals.Contains(item.ItemId)) continue;
                if (streaming.TryGetValue(item.ItemId, out var sofar))
                {
                    sofar.Text += item.Text;
                    sofar.Sequence = item.Sequence;
                    continue;
                }
                streaming[item.ItemId] = new ApiModeLiveItem
                {
                    ItemId = item.ItemId,
                    Kind = item.Kind,
                    Mode = ApiModeLiveItemModes.Replace,
                    Text = item.Text,
                    Tone = item.Tone,
                    TurnId = item.TurnId,
                    Sequence = item.Sequence,
                };
                streamingOrder.Add(item.ItemId);
            }

        var items = new List<ApiModeLiveItem>();
        ApiModeConversationPlace? cut = null;
        if (conversation.Count > 0 && conversation[0].EarlierDropped)
        {
            cut = conversation[0].First;
            items.Add(new ApiModeLiveItem
            {
                ItemId = EarlierNotKeptItemId,
                Kind = ApiModeLiveItemKinds.Notice,
                Mode = ApiModeLiveItemModes.Replace,
                Text = EarlierNotKeptWords,
                Tone = "neutral",
                Sequence = 0,
            });
        }

        var placed = receipts
            .Select(r => (Receipt: r, First: r.First ?? throw new InvalidOperationException($"ApiModeConversationOpening: the receipt of prompt={r.Receipt.PromptId} has no place; read it from the store")))
            .Where(r => cut is not { } from || r.First.CompareTo(from) >= 0)
            .ToList();

        var next = 0;
        foreach (var stored in conversation)
        {
            while (next < placed.Count && placed[next].First.CompareTo(stored.First) <= 0)
                items.Add(ApiModeLiveRuling.ReceiptItem(placed[next++].Receipt.Receipt, 0));
            if (stored.HasFinal)
                items.Add(stored.Item);
            else if (streaming.Remove(stored.Item.ItemId, out var sofar))
                items.Add(sofar);
        }
        while (next < placed.Count)
            items.Add(ApiModeLiveRuling.ReceiptItem(placed[next++].Receipt.Receipt, 0));
        // A reply streaming with no stored place (the bound let its place go): where a live reader would next see it.
        items.AddRange(streamingOrder.Where(streaming.ContainsKey).Select(id => streaming[id]));

        return (ruling, items);
    }
}
