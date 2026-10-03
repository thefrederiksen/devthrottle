using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Terminal.Avalonia;

/// <summary>
/// Inputs the shared link context menu needs to build and act on its items. The owner is any visual
/// in the live tree (used to reach the clipboard and to host browser-launch errors); the callbacks
/// let each caller route "View File" and browser-launch failures into its own surface.
/// </summary>
public sealed class LinkMenuContext
{
    /// <summary>The detected link text (a path or a URL), exactly as <see cref="LinkDetector"/> returned it.</summary>
    public required string Link { get; init; }

    /// <summary>Whether <see cref="Link"/> is a file path or a URL.</summary>
    public required LinkDetector.LinkType Type { get; init; }

    /// <summary>Repo root for resolving relative paths, or null.</summary>
    public string? RepoPath { get; init; }

    /// <summary>A visual in the live tree, used to reach the clipboard.</summary>
    public required Control Owner { get; init; }

    /// <summary>Called with the resolved absolute path when the user picks "View File".</summary>
    public Action<string>? OnViewFile { get; init; }

    /// <summary>Called with a human-readable message when a browser launch fails.</summary>
    public Action<string>? OnBrowserError { get; init; }

    /// <summary>
    /// The address a program's own link (OSC 8) opens, when the text on screen says something else.
    /// It is shown, whole, as the menu's first line, so nothing is opened or copied from a link
    /// whose real address the user has not been shown. Null for every other link.
    /// </summary>
    public string? Address { get; init; }
}

/// <summary>
/// Builds the link context menu shared by the terminal and the History tab (GitHub #735). Both the
/// terminal's <c>ShowLinkContextMenu</c> and the History bubbles call this single implementation, so
/// a path or URL offers the exact same actions in either place - there is no divergent copy of the
/// menu. For a file path: View File (when viewable), Open in Browser + Choose Browser (when HTML),
/// Copy Path, Open in File Manager. For a URL: Copy URL, Open in Browser, Choose Browser.
///
/// The menu is deliberately FLAT - every item does its thing on one press, and no item opens a
/// submenu. Picking among browsers and profiles is a dialog (<see cref="BrowserPickerDialog"/>),
/// not a hover-chain; see <see cref="AddOpenInBrowserItems"/> for why.
/// </summary>
public static class LinkContextMenuBuilder
{
    /// <summary>Build a fresh <see cref="ContextMenu"/> populated with the link items for this context.</summary>
    public static ContextMenu Build(LinkMenuContext context)
    {
        var menu = new ContextMenu();
        PopulateLinkItems(menu, context);
        return menu;
    }

    /// <summary>
    /// Append the link items to an existing menu. The terminal uses this so it can add its own paste
    /// items after the shared link items; a standalone caller can use <see cref="Build"/> instead.
    /// </summary>
    public static void PopulateLinkItems(ContextMenu menu, LinkMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Address != null)
        {
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = context.Address, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, MaxWidth = 520 },
                IsEnabled = false,
            });
            menu.Items.Add(new Separator());
        }

        if (context.Type == LinkDetector.LinkType.Path)
        {
            bool addedViewerItem = false;

            if (FileExtensions.IsViewable(context.Link))
            {
                var viewItem = new MenuItem { Header = "View File" };
                viewItem.Click += (_, _) => OpenFileViewer(context);
                menu.Items.Add(viewItem);
                addedViewerItem = true;
            }

            if (FileExtensions.IsHtml(context.Link))
            {
                string htmlTarget = ResolvePath(context, context.Link).Replace('/', '\\').TrimEnd('\\');
                AddOpenInBrowserItems(menu, context, htmlTarget);
                addedViewerItem = true;
            }

            if (addedViewerItem)
                menu.Items.Add(new Separator());

            var copyItem = new MenuItem { Header = "Copy Path" };
            copyItem.Click += (_, _) => _ = CopyLinkToClipboardAsync(context);
            menu.Items.Add(copyItem);

            var explorerItem = new MenuItem { Header = "Open in File Manager" };
            explorerItem.Click += (_, _) => OpenInFileManager(context);
            menu.Items.Add(explorerItem);
        }
        else if (context.Type == LinkDetector.LinkType.Url)
        {
            var copyItem = new MenuItem { Header = "Copy URL" };
            copyItem.Click += (_, _) => _ = CopyLinkToClipboardAsync(context);
            menu.Items.Add(copyItem);

            AddOpenInBrowserItems(menu, context, context.Link);
        }
    }

    private static async Task CopyLinkToClipboardAsync(LinkMenuContext context)
    {
        if (string.IsNullOrEmpty(context.Link))
            return;

        string textToCopy = context.Type == LinkDetector.LinkType.Path
            ? ResolvePath(context, context.Link).Replace('/', '\\').TrimEnd('\\')
            : context.Link;

        var clipboard = TopLevel.GetTopLevel(context.Owner)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(textToCopy);
            FileLog.Write($"[LinkContextMenuBuilder] Copied link: {textToCopy}");
        }
    }

    private static void OpenInFileManager(LinkMenuContext context)
    {
        if (string.IsNullOrEmpty(context.Link))
            return;

        try
        {
            string path = ResolvePath(context, context.Link).Replace('/', '\\').TrimEnd('\\');
            string target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", $"\"{target}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"\"{target}\"");
            else
                Process.Start("xdg-open", $"\"{target}\"");

            FileLog.Write($"[LinkContextMenuBuilder] Opened in file manager: {target}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] OpenInFileManager FAILED: {ex.Message}");
        }
    }

    private static void OpenFileViewer(LinkMenuContext context)
    {
        if (string.IsNullOrEmpty(context.Link))
            return;

        string path = ResolvePath(context, context.Link);
        FileLog.Write($"[LinkContextMenuBuilder] OpenFileViewer: {path}");
        context.OnViewFile?.Invoke(path);
    }

    /// <summary>
    /// Adds the two flat "Open in Browser" items for <paramref name="target"/> (a URL or a local
    /// file path): one that opens the resolved default straight away, and one that opens the
    /// <see cref="BrowserPickerDialog"/>.
    ///
    /// This replaces the cascading submenu that shipped with the per-repository default (#1533),
    /// where every real action sat four popups deep - "Open in Browser" -&gt; browser -&gt; profile
    /// -&gt; intent. Avalonia's MenuItem has no hover-intent safe area, so moving the pointer
    /// diagonally toward a submenu crossed the sibling row and collapsed the chain: the actions were
    /// unreachable in practice. The picker dialog holds the same three intents (open once, remember
    /// for this repository, remember everywhere) as one list plus a checkbox, so no action needs a
    /// submenu at all and every one is a single press.
    ///
    /// Both items are pure: neither touches the disk while the menu is being built, so the menu
    /// pops instantly. Browser detection and default resolution happen inside the dialog, in the
    /// background, after it is on screen.
    /// </summary>
    private static void AddOpenInBrowserItems(ContextMenu menu, LinkMenuContext context, string target)
    {
        var openItem = new MenuItem { Header = "Open in Browser" };
        openItem.Click += (_, _) => OpenInBrowserDefault(context, target);
        menu.Items.Add(openItem);

        var chooseItem = new MenuItem { Header = "Choose Browser..." };
        chooseItem.Click += (_, _) => _ = ChooseBrowserAsync(context, target);
        menu.Items.Add(chooseItem);
    }

    /// <summary>
    /// Opens the browser picker and acts on what the user chose. Cancelling does nothing at all -
    /// no launch, no remembered default.
    /// </summary>
    private static async Task ChooseBrowserAsync(LinkMenuContext context, string target)
    {
        try
        {
            if (TopLevel.GetTopLevel(context.Owner) is not Window owner)
                throw new InvalidOperationException(
                    "The link menu is not hosted in a window, so the browser picker cannot be opened.");

            var choice = await BrowserPickerDialog.ShowAsync(owner, target, context.RepoPath);
            if (choice is null)
            {
                FileLog.Write("[LinkContextMenuBuilder] ChooseBrowserAsync: cancelled");
                return;
            }

            ApplyChoice(context, target, choice);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] ChooseBrowserAsync FAILED: {ex.Message}");
            context.OnBrowserError?.Invoke($"Could not open the browser picker.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Launches the picked browser and persists the picked scope. The launch happens FIRST: if the
    /// browser will not start, we surface that and never write a default the user would then be
    /// stuck with.
    /// </summary>
    private static void ApplyChoice(LinkMenuContext context, string target, BrowserChoice choice)
    {
        try
        {
            if (choice.Browser is null)
                BrowserLauncher.OpenSystemDefault(target);
            else
                BrowserLauncher.OpenWithProfile(target, choice.Browser, RequireProfileFolder(choice));

            RememberChoice(context, choice);

            FileLog.Write($"[LinkContextMenuBuilder] ApplyChoice: opened {target} in "
                + $"{choice.Browser?.DisplayName ?? "the system default browser"}, scope={choice.Scope}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] ApplyChoice FAILED: {ex.Message}");
            var where = choice.Browser?.DisplayName ?? "the system default browser";
            context.OnBrowserError?.Invoke($"Could not open in {where}.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Writes (or erases) the remembered default for the chosen scope. Choosing the system default
    /// browser and asking to remember it ERASES the stored default for that scope - that is how a
    /// default gets taken back, and the store then resolves through to the operating system again.
    /// </summary>
    private static void RememberChoice(LinkMenuContext context, BrowserChoice choice)
    {
        switch (choice.Scope)
        {
            case BrowserRememberScope.None:
                return;

            case BrowserRememberScope.Repository:
                if (string.IsNullOrWhiteSpace(context.RepoPath))
                    throw new InvalidOperationException(
                        "This link has no owning repository, so it has no repository default to set.");

                if (choice.Browser is null)
                    BrowserDefaultStore.ClearForRepo(context.RepoPath);
                else
                    BrowserDefaultStore.SaveForRepo(
                        context.RepoPath, new BrowserDefault(choice.Browser.ExePath, RequireProfileFolder(choice)));
                return;

            case BrowserRememberScope.Application:
                if (choice.Browser is null)
                    BrowserDefaultStore.Clear();
                else
                    BrowserDefaultStore.Save(
                        new BrowserDefault(choice.Browser.ExePath, RequireProfileFolder(choice)));
                return;

            default:
                throw new InvalidOperationException($"Unknown remember scope: {choice.Scope}");
        }
    }

    /// <summary>A choice naming a browser must name the profile to launch it with.</summary>
    private static string RequireProfileFolder(BrowserChoice choice)
        => string.IsNullOrWhiteSpace(choice.ProfileFolder)
            ? throw new InvalidOperationException(
                $"The choice named {choice.Browser?.DisplayName} but no profile folder.")
            : choice.ProfileFolder;

    private static void OpenInBrowserDefault(LinkMenuContext context, string target)
    {
        try
        {
            // Resolve order: this repository's default, then the application-wide default, then the OS
            // default (a null result). A repo with no default behaves exactly as it did before.
            var remembered = BrowserDefaultStore.Resolve(context.RepoPath);
            if (remembered is null)
            {
                FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserDefault: no remembered default, using system default: {target}");
                BrowserLauncher.OpenSystemDefault(target);
                return;
            }

            var browser = BrowserDefaultStore.ResolveBrowser(remembered.ExePath);
            BrowserLauncher.OpenWithProfile(target, browser, remembered.ProfileFolder);
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserDefault: opened {target} in {browser.DisplayName}/{remembered.ProfileFolder}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserDefault FAILED: {ex.Message}");
            context.OnBrowserError?.Invoke($"Could not open in browser.\n\n{ex.Message}");
        }
    }

    private static string ResolvePath(LinkMenuContext context, string path)
        => LinkDetector.ResolvePath(path, context.RepoPath);
}
