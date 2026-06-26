using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
}

/// <summary>
/// Builds the link context menu shared by the terminal and the History tab (GitHub #735). Both the
/// terminal's <c>ShowLinkContextMenu</c> and the History bubbles call this single implementation, so
/// a path or URL offers the exact same actions in either place - there is no divergent copy of the
/// menu. For a file path: View File (when viewable), Open in Browser (when HTML), Copy Path, Open in
/// File Manager. For a URL: Copy URL, Open in Browser (with the browser/profile submenu).
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
                menu.Items.Add(BuildOpenInBrowserMenuItem(menu, context, htmlTarget));
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

            menu.Items.Add(BuildOpenInBrowserMenuItem(menu, context, context.Link));
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
    /// Builds the "Open in Browser" menu item for <paramref name="target"/> (a URL or a local file
    /// path). A plain click reopens the remembered default; hovering expands a submenu of "System
    /// default" plus each installed browser with its real profiles (re-read from each browser's Local
    /// State so newly added profiles appear without a restart).
    /// </summary>
    private static MenuItem BuildOpenInBrowserMenuItem(ContextMenu menu, LinkMenuContext context, string target)
    {
        var parent = new MenuItem { Header = "Open in Browser" };

        // A submenu parent does NOT raise Click on its header - pressing the header just expands the
        // submenu. To make a plain click on the parent reopen the remembered default (while HOVER
        // still expands the submenu), intercept the header press in the tunnel phase. The tunnel
        // route also passes through this parent for presses on its OWN submenu leaves (descendants),
        // so launch the default ONLY when the press lands on the parent's own header rectangle.
        parent.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var pos = e.GetPosition(parent);
            bool onHeader = pos.X >= 0 && pos.Y >= 0
                && pos.X <= parent.Bounds.Width && pos.Y <= parent.Bounds.Height;
            if (!onHeader)
                return;

            e.Handled = true;
            menu.Close();
            OpenInBrowserDefault(context, target);
        }, RoutingStrategies.Tunnel);

        var systemItem = new MenuItem { Header = "System default" };
        systemItem.Click += (_, e) =>
        {
            e.Handled = true;
            OpenInBrowserSystemDefault(context, target);
        };
        parent.Items.Add(systemItem);

        foreach (var browser in BrowserLauncher.DetectBrowsers())
        {
            var browserItem = new MenuItem { Header = browser.DisplayName };

            var profiles = BrowserLauncher.GetProfiles(browser);
            if (profiles.Count == 0)
            {
                browserItem.Items.Add(new MenuItem { Header = "(no profiles found)", IsEnabled = false });
            }
            else
            {
                foreach (var profile in profiles)
                {
                    string header = profile.Account is null
                        ? profile.DisplayName
                        : $"{profile.DisplayName} ({profile.Account})";

                    var profileItem = new MenuItem { Header = header };
                    var capturedBrowser = browser;
                    var capturedFolder = profile.FolderName;
                    profileItem.Click += (_, e) =>
                    {
                        e.Handled = true;
                        OpenInBrowserProfile(context, target, capturedBrowser, capturedFolder);
                    };
                    browserItem.Items.Add(profileItem);
                }
            }

            parent.Items.Add(browserItem);
        }

        return parent;
    }

    private static void OpenInBrowserDefault(LinkMenuContext context, string target)
    {
        try
        {
            var remembered = BrowserDefaultStore.Load();
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

    private static void OpenInBrowserSystemDefault(LinkMenuContext context, string target)
    {
        try
        {
            BrowserLauncher.OpenSystemDefault(target);
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserSystemDefault: {target}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserSystemDefault FAILED: {ex.Message}");
            context.OnBrowserError?.Invoke($"Could not open in the system default browser.\n\n{ex.Message}");
        }
    }

    private static void OpenInBrowserProfile(LinkMenuContext context, string target, BrowserInfo browser, string profileFolder)
    {
        try
        {
            BrowserLauncher.OpenWithProfile(target, browser, profileFolder);
            BrowserDefaultStore.Save(new BrowserDefault(browser.ExePath, profileFolder));
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserProfile: opened {target} in {browser.DisplayName}/{profileFolder}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkContextMenuBuilder] OpenInBrowserProfile FAILED: {ex.Message}");
            context.OnBrowserError?.Invoke($"Could not open in {browser.DisplayName}.\n\n{ex.Message}");
        }
    }

    private static string ResolvePath(LinkMenuContext context, string path)
        => LinkDetector.ResolvePath(path, context.RepoPath);
}
