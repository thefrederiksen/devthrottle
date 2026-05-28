using System;
using System.Collections.Generic;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Pure diff logic for the Clean view's incremental update (#5). Kept free of any
/// Avalonia types so it can be unit-tested in isolation: it works only on the
/// structural "signatures" of the widgets currently shown vs. the freshly parsed
/// list, and classifies how the bound collection should be updated.
///
/// The classification drives whether <see cref="CleanView"/> does nothing, appends
/// a few cards, or rebuilds the whole feed - the difference between a smooth tail
/// follow and tearing the transcript down every two seconds.
/// </summary>
internal static class CleanViewDiff
{
    // Unit-separator (U+001F) between signature fields so values cannot collide
    // across boundaries. Declared as an ASCII escape so the source stays ASCII-only;
    // the separator only ever lives inside the internal signature string.
    private const char FieldSep = (char)0x1F;

    /// <summary>How the on-screen feed should be reconciled with the new parse.</summary>
    internal enum Update
    {
        /// <summary>Content is unchanged - skip entirely (no clear, no scroll, no persist).</summary>
        None,

        /// <summary>Every existing card is unchanged; only new cards were added at the tail.</summary>
        Append,

        /// <summary>An earlier card changed (tool result arrived, or history was rewound) - full rebuild.</summary>
        Rebuild,
    }

    /// <summary>
    /// Structural signature of a widget: everything that affects what is rendered, so a
    /// pending tool gaining a result, or an edited message, counts as a change.
    /// </summary>
    internal static string Signature(CleanWidgetViewModel w)
    {
        return string.Join(FieldSep,
            ((int)w.Kind).ToString(),
            w.IsPending ? "1" : "0",
            w.IsError ? "1" : "0",
            w.SnapshotEntryNumber.ToString(),
            w.Header,
            w.Subheader,
            w.Content,
            w.Result);
    }

    /// <summary>
    /// Decide how to reconcile <paramref name="current"/> (the content signatures already
    /// shown, excluding any trailing pending-question card) with <paramref name="incoming"/>
    /// (signatures of the freshly parsed widgets). On <see cref="Update.Append"/>,
    /// <paramref name="appendFrom"/> is the index in the incoming list where the new tail
    /// begins.
    /// </summary>
    internal static Update Classify(
        IReadOnlyList<string> current,
        IReadOnlyList<string> incoming,
        out int appendFrom)
    {
        appendFrom = current.Count;

        // Identical content -> nothing to do.
        if (current.Count == incoming.Count)
        {
            bool same = true;
            for (int i = 0; i < current.Count; i++)
            {
                if (!string.Equals(current[i], incoming[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return Update.None;
        }

        // Longest common prefix of unchanged cards.
        int common = 0;
        int min = Math.Min(current.Count, incoming.Count);
        while (common < min && string.Equals(current[common], incoming[common], StringComparison.Ordinal))
            common++;

        // Pure append: all existing cards survived and the new list only grew at the tail.
        if (common == current.Count && incoming.Count > current.Count)
        {
            appendFrom = current.Count;
            return Update.Append;
        }

        // Anything else (a card changed in place, or the list shrank) -> full rebuild.
        return Update.Rebuild;
    }

    /// <summary>
    /// Select which widgets the Wingman tab's response-only view should show, given the
    /// kinds of every parsed widget in feed order. The Wingman tab is NOT a chat transcript:
    /// it shows only Claude's full final answer, with no prompt echo, no narration, and no
    /// tool/bash/thinking cards. The rule:
    ///   - keep the trailing run of assistant Text widgets of the CURRENT turn, i.e. the
    ///     contiguous block of text immediately following the last tool call (a final answer
    ///     can span more than one text block). Everything after the last user prompt counts
    ///     as the current turn; earlier "Let me read..." narration that was split off by a
    ///     tool call is dropped, leaving only the actual answer;
    ///   - if the user just sent a prompt and Claude has not replied yet (the last text
    ///     precedes the last user message), show no answer - the stale previous reply is gone;
    ///   - always keep the orange pending-question card when present (the "Claude is waiting"
    ///     callout is the most important thing on screen).
    /// Returns the indices to keep, in order. Pure (operates on kinds only) so it is
    /// unit-testable without the Avalonia view models.
    /// </summary>
    internal static List<int> SelectResponseOnly(IReadOnlyList<WidgetKind> kinds)
    {
        var keep = new List<int>();

        int lastUser = -1, lastText = -1;
        for (int i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == WidgetKind.UserMessage) lastUser = i;
            else if (kinds[i] == WidgetKind.Text) lastText = i;
        }

        // Final answer = the trailing run of text blocks of the current turn. Walk back
        // from the last text while the previous widget is also text (a multi-block answer),
        // but never cross the last user prompt.
        if (lastText > lastUser)
        {
            int start = lastText;
            while (start - 1 > lastUser && kinds[start - 1] == WidgetKind.Text)
                start--;
            for (int i = start; i <= lastText; i++)
                keep.Add(i);
        }

        // The orange "Claude is waiting on your answer" card always shows through. It is
        // pinned at the tail, so its index comes after the answer run - order is preserved.
        for (int i = 0; i < kinds.Count; i++)
            if (kinds[i] == WidgetKind.PendingQuestion)
                keep.Add(i);

        return keep;
    }
}
