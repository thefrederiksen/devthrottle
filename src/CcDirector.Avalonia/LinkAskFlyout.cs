using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.ControlApi;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The small popup the amber phone badge and the header chip open (issue #3851): for each waiting request from the
/// session, who it wants to talk to, why, what it asks for, and Approve / Deny. The Cockpit's popover, in the Director.
///
/// The session named what it needs when it asked; the owner only approves or denies that. Approving sets up an ordinary
/// message link and the Gateway tells the session; denying tells it no. Nothing here sends a message.
/// </summary>
internal static class LinkAskFlyout
{
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
    private static readonly IBrush ErrorRed = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public static void ShowAt(Control anchor, SessionViewModel vm, LinkRequestsCache cache, Func<string, string?> localName)
    {
        var mine = vm.LinkAsks;
        if (mine.Count == 0) return;
        FileLog.Write($"[LinkAskFlyout] ShowAt: session={vm.Session.Id}, requests={mine.Count}");

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 380, Margin = new Thickness(4) };
        if (cache.Error is { } listError)
            panel.Children.Add(Text($"The list could not be read just now: {listError}", ErrorRed, wrap: true));
        foreach (var request in mine)
            panel.Children.Add(Item(request, cache, localName, flyout));
        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    private static Control Item(FleetMessageLinkRequestDto request, LinkRequestsCache cache, Func<string, string?> localName, Flyout flyout)
    {
        var item = new StackPanel { Spacing = 6 };

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        title.Inlines!.Add(new global::Avalonia.Controls.Documents.Run("Wants to talk to "));
        title.Inlines.Add(new global::Avalonia.Controls.Documents.Run(LinkAskWords.Target(request, localName)) { FontWeight = FontWeight.Bold });
        item.Children.Add(title);

        item.Children.Add(Text($"\"{request.Reason}\"", Muted, wrap: true, italic: true));

        var amount = new TextBlock { FontSize = 12 };
        amount.Inlines!.Add(new global::Avalonia.Controls.Documents.Run("Asks for: "));
        amount.Inlines.Add(new global::Avalonia.Controls.Documents.Run(LinkAskWords.Amount(request.RequestedAmount)) { Foreground = Amber, FontWeight = FontWeight.SemiBold });
        item.Children.Add(amount);

        var error = Text("", ErrorRed, wrap: true);
        error.IsVisible = false;

        var approve = new Button { Content = "Approve", Padding = new Thickness(12, 4), Background = Amber, Foreground = Brushes.Black, FontWeight = FontWeight.SemiBold };
        var deny = new Button { Content = "Deny", Padding = new Thickness(12, 4) };
        async void Answer(bool yes)
        {
            approve.IsEnabled = deny.IsEnabled = false;
            error.IsVisible = false;
            try
            {
                await cache.AnswerAsync(request.RequestId, yes);
                flyout.Hide();
            }
            catch (Exception ex)
            {
                // The Gateway's own words: an answer that quietly failed would look exactly like one that worked.
                FileLog.Write($"[LinkAskFlyout] answer FAILED: request={request.RequestId} approve={yes}: {ex.Message}");
                error.Text = ShownError.Report("link request", yes ? "approve the request" : "deny the request", ex.Message, ex);
                error.IsVisible = true;
                approve.IsEnabled = deny.IsEnabled = true;
            }
        }
        approve.Click += (_, _) => Answer(true);
        deny.Click += (_, _) => Answer(false);

        item.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { approve, deny } });
        item.Children.Add(error);
        return item;
    }

    private static TextBlock Text(string text, IBrush brush, bool wrap = false, bool italic = false) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = 12,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}
