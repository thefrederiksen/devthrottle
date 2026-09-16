using System.Text;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// THE ONE PLACE THE PROMPT A SESSION RECEIVES FOR THE OWNER'S NOTES AND ANSWERS IS WRITTEN (issue #2958,
/// mission ruling 4). A pure function of the reports and their items in send order; its tests pin it byte for
/// byte.
///
/// THE OWNER'S WORDS GO THROUGH VERBATIM. A note's text and an answer's comment are written between a line
/// <c>&lt;&lt;&lt;</c> and a line <c>&gt;&gt;&gt;</c>, unindented and untouched - never trimmed, re-wrapped,
/// re-indented or shortened, so leading spaces and line breaks arrive exactly as typed. The page's own words
/// (the quoted cell, the question, the option) are quoted in full as well: a shortened quote can point at the
/// wrong thing.
///
/// A label the page could not work out is sent as an empty string (CONTRACT.md section 2), and an empty label
/// is left out of the sentence rather than written as <c>row ""</c>.
/// </summary>
internal static class DevReportPromptFold
{
    /// <summary>One item as the fold reads it: the item, and whether it changes an answer already delivered.</summary>
    internal sealed record FoldItem(DevReportItem Item, bool ChangesDeliveredAnswer);

    /// <summary>One report's block: its id, key (the file), title, latest version, and its items in send order.</summary>
    internal sealed record FoldReport(Guid ReportId, string Key, string Title, int Version, IReadOnlyList<FoldItem> Items);

    public const string WordsOpen = "<<<";
    public const string WordsClose = ">>>";

    /// <summary>The prompt for everything being delivered to one session at once - one block per report, in
    /// the order given.</summary>
    /// <exception cref="ArgumentException">There is nothing to deliver, or a report has no items.</exception>
    public static string Compose(IReadOnlyList<FoldReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0)
            throw new ArgumentException("a prompt needs at least one report with items", nameof(reports));

        var sb = new StringBuilder();
        for (var r = 0; r < reports.Count; r++)
        {
            var report = reports[r];
            if (report.Items.Count == 0)
                throw new ArgumentException($"report {report.ReportId} has no items to deliver", nameof(reports));
            if (r > 0) sb.Append('\n');
            AppendReport(sb, report);
        }
        return sb.ToString();
    }

    private static void AppendReport(StringBuilder sb, FoldReport report)
    {
        sb.Append("The owner answered your dev report \"").Append(report.Title)
          .Append("\" (version ").Append(report.Version).Append(", file ").Append(report.Key).Append(").\n\n");

        for (var i = 0; i < report.Items.Count; i++)
        {
            var (item, changes) = (report.Items[i].Item, report.Items[i].ChangesDeliveredAnswer);
            sb.Append(i + 1).Append(". ");
            if (item.Kind == DevReportItem.Note)
            {
                sb.Append("A note ").Append(Where(item.Anchor!)).Append(". The owner wrote:\n");
                AppendWords(sb, item.Text);
            }
            else
            {
                sb.Append("An answer to \"").Append(item.Question).Append("\": ")
                  .Append(item.OptionLabel).Append(" (value \"").Append(item.OptionValue).Append("\").");
                if (changes) sb.Append(" This changes the owner's earlier answer to this question.");
                sb.Append('\n');
                if (item.Comment.Length > 0)
                {
                    sb.Append("The owner's comment:\n");
                    AppendWords(sb, item.Comment);
                }
            }
            sb.Append('\n');
        }

        sb.Append("Reply in the report with: cc-dev-reports reply --report ").Append(report.ReportId.ToString("D"))
          .Append(" \"<your reply>\"\n");
        sb.Append("Then update the report file and publish it again with: cc-dev-reports open \"")
          .Append(report.Key).Append("\"\n");
    }

    /// <summary>Where a note points, as a phrase that follows "A note".</summary>
    internal static string Where(DevReportAnchor anchor)
    {
        switch (anchor.Type)
        {
            case DevReportAnchor.TableCell:
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(anchor.RowLabel)) parts.Add($"row \"{anchor.RowLabel}\"");
                if (!string.IsNullOrEmpty(anchor.ColumnLabel)) parts.Add($"column \"{anchor.ColumnLabel}\"");
                var labels = parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
                return $"on a table cell{labels} that reads \"{anchor.Quote}\"";
            }
            case DevReportAnchor.SvgPart:
            {
                var named = string.IsNullOrEmpty(anchor.Label) ? "a diagram part" : $"the diagram part \"{anchor.Label}\"";
                return anchor.Quote.Length == 0 ? $"on {named}" : $"on {named} that reads \"{anchor.Quote}\"";
            }
            case DevReportAnchor.Text:
                return $"on the selected text \"{anchor.Quote}\"";
            case DevReportAnchor.Element:
                return $"on the part of the report that reads \"{anchor.Quote}\"";
            default:
                throw new ArgumentException($"anchor type \"{anchor.Type}\" is not one the fold knows", nameof(anchor));
        }
    }

    private static void AppendWords(StringBuilder sb, string words)
        => sb.Append(WordsOpen).Append('\n').Append(words).Append('\n').Append(WordsClose).Append('\n');
}
