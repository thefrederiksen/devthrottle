using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// An architecture fitness function for the ONE rule the mobile app keeps breaking: a session screen
/// must FIT THE VISIBLE SCREEN, and the PAGE must never scroll.
///
/// Why this test exists, in the owner's words: "this is the tenth time or something that this bug has
/// been reintroduced into our mobile application. We need to figure out a more consistent way of
/// never having this issue."
///
/// The history, every entry the same bug wearing a different hat:
///   #1058  session screens: swap vh -> dvh
///   #1349  voice speaking state: fit the viewport
///   #1351  Terminal + Chat: overflow-clip shell, no page scroll
///   #1404  Car Mode v4: the primary button renders below the toolbar, cut off
///   #1408  Car Mode v5: dvh ABANDONED - pin to window.visualViewport.height instead
///
/// The root cause of the RECURRENCE (not of any single instance): on the owner's Android PWA, CSS
/// `100dvh` does not track the visible height - the box stays taller than the screen, the page gains
/// a sliver of scroll, and one swipe slides the top row away. It cannot be caught by eye in an
/// emulator, which reports zero page scroll and renders it perfectly. Every fix that trusted a CSS
/// viewport unit came back. Only #1408's approach - taking the height from
/// window.visualViewport.height - has held on the real device.
///
/// So the contract is: BOTH full-screen shells take their height from --app-vh (published by the one
/// shared useVisibleViewportHeight hook, mounted once in the app shell), never from a bare viewport
/// unit; and neither over-constrains itself with `inset: 0`, which silently makes the height
/// declaration a no-op (that is precisely what Car Mode v4 shipped).
///
/// This is a C# test on purpose. CI runs `dotnet test` and NOTHING ELSE - there is no JavaScript test
/// step in .github/workflows/ci.yml - so a vitest guard would never run, and a guard nobody runs is
/// not a guard. It reads the mobile source as text, the same way NoCrossMachineLoopbackGuardTests
/// pins the loopback policy.
///
/// Scope, stated rather than hidden: this pins the MECHANISM (the shells size from the shared
/// variable, the hook is mounted, nobody reintroduces a private one). It cannot prove pixels on a
/// real phone. It exists to stop the specific regression that has actually happened five times -
/// someone "simplifying" a shell back to a plain dvh box.
/// </summary>
public sealed class MobileViewportContractTests
{
    private const string StylesPath = "apps/mobile/src/styles.css";

    /// <summary>
    /// The file holding the app shell layout every gated screen hangs off - the one place the hook is
    /// mounted. This was main.tsx until the route table moved out of it (dev reports phase 3b, #3025) so the
    /// tests could mount the table the app mounts; the gated layout went with it. EntryPath below still pins
    /// the other half of the chain, so the split cannot quietly break the mounting.
    /// </summary>
    private const string ShellPath = "apps/mobile/src/routes.tsx";

    /// <summary>The app's entry point, which must actually mount the table the shell layout sits in.</summary>
    private const string EntryPath = "apps/mobile/src/main.tsx";

    private const string HookPath = "apps/mobile/src/hooks/useVisibleViewportHeight.ts";

    /// <summary>
    /// The full-screen shells. Every screen that fills the window must use one of these.
    ///
    /// There were two. Car Mode's shell went when Car Mode was removed from the product (#1028), so the list is
    /// one entry - and it stays a LIST because the mechanism it pins is about "every full-screen shell", not
    /// about the terminal. The next pinned shell is added here and inherits both checks below.
    /// </summary>
    private static readonly string[] FullScreenShells = [".terminal-screen"];

    private const string Why =
        "\n\nWHY: on the owner's Android PWA, CSS 100dvh does not track the visible height - the box stays "
        + "TALLER than the screen, the page gains a sliver of scroll, and one swipe hides the top row. "
        + "It looks perfect in an emulator. This has been re-fixed at least five times (#1058, #1349, "
        + "#1351, #1404, #1408); only pinning to window.visualViewport.height (#1408) has ever held. "
        + "Take the height from --app-vh (see apps/mobile/src/hooks/useVisibleViewportHeight.ts), not "
        + "from a CSS viewport unit.";

    [Fact]
    public void FullScreenShells_TakeTheirHeightFromTheSharedVisibleViewportVariable()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, shell);
            Assert.False(block is null, $"{StylesPath} no longer defines a `{shell}` rule. If a shell was renamed, update this test - do not delete the contract.{Why}");

            // The LAST height declaration wins in CSS. That one must be the pinned variable; earlier
            // vh/dvh lines are legitimate fallbacks for the first paint.
            var heights = Regex.Matches(block!, @"(?<!max-|min-)\bheight\s*:\s*([^;]+);")
                               .Select(m => m.Groups[1].Value.Trim())
                               .ToList();
            if (heights.Count == 0)
                offenders.Add($"{shell}: declares no height at all.");
            else if (!heights[^1].Contains("var(--app-vh", StringComparison.Ordinal))
                offenders.Add($"{shell}: its EFFECTIVE height is `{heights[^1]}`, not var(--app-vh, ...). A CSS viewport unit is exactly what keeps regressing.");

            var maxHeights = Regex.Matches(block!, @"\bmax-height\s*:\s*([^;]+);")
                                  .Select(m => m.Groups[1].Value.Trim())
                                  .ToList();
            if (maxHeights.Count > 0 && !maxHeights[^1].Contains("var(--app-vh", StringComparison.Ordinal))
                offenders.Add($"{shell}: its EFFECTIVE max-height is `{maxHeights[^1]}`, not var(--app-vh, ...).");
        }

        Assert.True(offenders.Count == 0, "A full-screen mobile shell is not pinned to the visible viewport:\n  " + string.Join("\n  ", offenders) + Why);
    }

    [Fact]
    public void FullScreenShells_DoNotOverConstrainThemselvesWithInsetZero()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, shell);
            if (block is null) continue;

            // `inset: 0` (or an explicit bottom) sets top AND bottom, so the height is derived from the
            // tall toolbar-hidden layout viewport and the height declaration is IGNORED. Car Mode v4
            // shipped this and the primary button rendered off-screen (#1404 -> #1408).
            if (Regex.IsMatch(block, @"\binset\s*:\s*0"))
                offenders.Add($"{shell}: uses `inset: 0`, which over-constrains the box so its height declaration is ignored. Anchor top/left/right only.");
            if (Regex.IsMatch(block, @"\bposition\s*:\s*fixed") && Regex.IsMatch(block, @"(?<!-)\bbottom\s*:\s*0"))
                offenders.Add($"{shell}: is fixed AND anchors `bottom: 0`, which over-constrains its height the same way `inset: 0` does.");
        }

        Assert.True(offenders.Count == 0, "A full-screen mobile shell over-constrains its own height:\n  " + string.Join("\n  ", offenders) + Why);
    }

    [Fact]
    public void TheVisibleViewportHook_ExistsAndIsMountedOnceInTheAppShell()
    {
        var root = GetRepoRoot();

        var hookFull = Path.Combine(root, HookPath);
        Assert.True(File.Exists(hookFull), $"{HookPath} is missing. It is the ONE place the app learns how tall the screen actually is.{Why}");

        var hook = File.ReadAllText(hookFull);
        Assert.True(hook.Contains("visualViewport", StringComparison.Ordinal),
            $"{HookPath} no longer reads window.visualViewport. That reading is the whole point - it is the only source that has held on the real device.{Why}");
        Assert.True(hook.Contains("--app-vh", StringComparison.Ordinal),
            $"{HookPath} no longer publishes --app-vh, which the stylesheet sizes the shells from.{Why}");

        // A plain Contains() here was a FAKE guard: commenting the call out (`// useVisibleViewportHeight();`)
        // still contains the string, so the test passed while the hook was not mounted at all - the exact
        // regression it is supposed to catch. Only a real, uncommented CALL counts.
        var shellLines = File.ReadAllLines(Path.Combine(root, ShellPath));
        var mounted = shellLines.Any(line =>
        {
            var code = line.Trim();
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) return false;
            return Regex.IsMatch(code, @"(^|[^.\w])useVisibleViewportHeight\s*\(\s*\)\s*;");
        });
        Assert.True(mounted,
            $"{ShellPath} does not CALL useVisibleViewportHeight(). Mounted once in the app shell, it fits EVERY screen; if it is not mounted, --app-vh is never set and every shell silently falls back to the dvh that does not work on the device.{Why}");

        // ...AND the app actually mounts that table. The shell layout living in its own file is only worth
        // anything while the entry point renders it; without this, deleting the mount in main.tsx would leave
        // a hook that is mounted in a file nothing runs, and the check above would still be green.
        var entry = File.ReadAllText(Path.Combine(root, EntryPath));
        Assert.True(entry.Contains("MOBILE_ROUTES", StringComparison.Ordinal)
                    && entry.Contains("createBrowserRouter", StringComparison.Ordinal),
            $"{EntryPath} no longer hands MOBILE_ROUTES to createBrowserRouter, so the app shell layout in {ShellPath} - and the hook mounted in it - is never rendered.{Why}");
    }

    [Fact]
    public void NoScreen_ReintroducesItsOwnPrivateViewportHeightVariable()
    {
        var root = GetRepoRoot();
        var mobileSrc = Path.Combine(root, "apps", "mobile", "src");
        var offenders = new List<string>();

        // One mechanism. Car Mode used to own a private --car-vh and Chat/Terminal/Voice trusted dvh;
        // that split is exactly how a fix lands on one screen and the bug survives on the others.
        var privateVar = new Regex(@"--(?!app-vh)[a-z0-9-]*-vh\b");

        foreach (var file in Directory.EnumerateFiles(mobileSrc, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is not (".ts" or ".tsx" or ".css")) continue;

            foreach (var (line, i) in File.ReadAllLines(file).Select((l, i) => (l, i)))
            {
                // A prose mention of the retired name in a comment is fine; a real declaration is not.
                if (!privateVar.IsMatch(line)) continue;
                var isDeclarationOrUse = line.Contains("setProperty", StringComparison.Ordinal)
                                         || Regex.IsMatch(line, @"var\(--(?!app-vh)[a-z0-9-]*-vh")
                                         || Regex.IsMatch(line, @"^\s*--(?!app-vh)[a-z0-9-]*-vh\s*:");
                if (isDeclarationOrUse)
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A screen has reintroduced its own private viewport-height variable. There is ONE: --app-vh, from the shared hook. "
            + "A per-screen copy is how this bug survives on the screens that did not get the fix:\n  "
            + string.Join("\n  ", offenders) + Why);
    }

    /// <summary>
    /// A full-height bar at the top of the app must MOVE the pinned shells down, not paint over them.
    ///
    /// The voice-mode banner shipped as a plain sticky bar and covered the session screen's own header -
    /// the back arrow to the roster and the overflow menu went under it. You could Respond and Snooze on a
    /// session, and you could not leave it. The cause is the same property this whole file exists to
    /// protect: the shells are pinned OUT OF THE DOCUMENT FLOW, so nothing above them in the markup can
    /// push them down. Rendering order does not move a fixed box; only its own `top` does.
    ///
    /// So the contract is: every pinned shell starts at the top bars' height and gives up that much height.
    /// There are TWO such bars - the voice-mode banner and the network banner - and each measures itself and
    /// publishes its own height (--voicemode-h, --connbanner-h; 0px when it is not on screen), because a bar
    /// wraps to two lines on a narrow phone and a hard-coded height would be right on one device. A shell
    /// reads their SUM, --topbars-h: accounting for one bar and not the other loses the header to the other.
    /// </summary>
    [Fact]
    public void PinnedShells_StartBelowTheTopBars_AndGiveUpTheirHeight()
    {
        var root = GetRepoRoot();
        var css = File.ReadAllText(Path.Combine(root, StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, shell);
            if (block is null) continue;

            // Pinned to the top of the window: `top` must be the bars' offset, not 0.
            var tops = Regex.Matches(block, @"(?<!-)\btop\s*:\s*([^;]+);").Select(m => m.Groups[1].Value.Trim()).ToList();
            if (tops.Count == 0 || !tops[^1].Contains("var(--topbars-h", StringComparison.Ordinal))
                offenders.Add($"{shell}: its effective `top` is `{(tops.Count == 0 ? "unset" : tops[^1])}`, not var(--topbars-h, 0px) - a top bar will paint over this screen's header, hiding its back button.");

            // And the height must shrink by the same amount, or the bottom of the screen goes off-window.
            var heights = Regex.Matches(block, @"(?<!max-|min-)\bheight\s*:\s*([^;]+);").Select(m => m.Groups[1].Value.Trim()).ToList();
            if (heights.Count > 0 && !heights[^1].Contains("var(--topbars-h", StringComparison.Ordinal))
                offenders.Add($"{shell}: its effective height `{heights[^1]}` does not subtract var(--topbars-h, 0px), so the bottom of the screen is pushed off-window while a bar is up.");
        }

        Assert.True(offenders.Count == 0,
            "A pinned mobile shell does not make room for the top bars:\n  " + string.Join("\n  ", offenders)
            + "\n\nWHY: these shells are position:fixed and sized to the whole visible height, so a bar rendered "
            + "ABOVE them in the markup cannot push them down - it paints over their header. That is exactly how "
            + "the voice-mode banner hid the session screen's back arrow and overflow menu: Respond and Snooze "
            + "worked, and there was no way back to the roster.");
    }

    /// <summary>
    /// --topbars-h must be the SUM of every bar that can be pinned above a screen, or a shell that reads it
    /// makes room for one bar and loses its header to the other. The network banner and the voice-mode
    /// banner appear independently and can be up at the same time (a slow connection while the fleet is
    /// narrating), so neither is safe to leave out.
    /// </summary>
    [Fact]
    public void TopBarsHeight_IsTheSumOfEveryPinnedTopBar()
    {
        var css = StripComments(File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath)));
        var declaration = Regex.Match(css, @"--topbars-h\s*:\s*([^;]+);");
        Assert.True(declaration.Success,
            "--topbars-h is no longer declared. Every pinned shell reads it for its `top` and its height; without it they all fall back to 0px and the top bars paint over their headers.");

        var value = declaration.Groups[1].Value;
        Assert.True(value.Contains("var(--voicemode-h", StringComparison.Ordinal),
            $"--topbars-h ({value.Trim()}) does not include the voice-mode banner's height. While that bar is up, every pinned screen loses its own header to it.");
        Assert.True(value.Contains("var(--connbanner-h", StringComparison.Ordinal),
            $"--topbars-h ({value.Trim()}) does not include the network banner's height. While that bar is up, every pinned screen loses its own header to it.");
    }

    /// <summary>
    /// ONE offset per screen. A header INSIDE a pinned shell must not take the banner offset a second time.
    ///
    /// The pinned shells set their own `top` to the banner height, so a header inside one is already below
    /// the banner by virtue of being in that box. The roster's header, which shares the page with the
    /// banner, does need the offset - and it is the SAME `.app-bar` class. Giving the class the offset
    /// gave it to both, and a sticky `top: var(--voicemode-h)` inside an already-offset box means "never
    /// come closer than the banner height to the top of THIS box", which pushed the session header down a
    /// second time. Shipped 2026-07-25 and reported from the phone: "there's a black space at the top...
    /// it's like it bumped it down too far". The strip was exactly one banner tall.
    /// </summary>
    [Fact]
    public void HeadersInsideAPinnedShell_DoNotTakeTheBannerOffsetASecondTime()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), StylesPath));
        var offenders = new List<string>();

        foreach (var shell in FullScreenShells)
        {
            var block = RuleBlock(css, $"{shell} .app-bar");
            if (block is null)
            {
                offenders.Add($"{shell} .app-bar: no rule resets this header's sticky `top`. It inherits the roster header's banner offset and is pushed down a SECOND time, leaving an empty strip one banner tall.");
                continue;
            }
            var tops = Regex.Matches(block, @"(?<!-)\btop\s*:\s*([^;]+);").Select(m => m.Groups[1].Value.Trim()).ToList();
            if (tops.Count == 0 || tops[^1] != "0")
                offenders.Add($"{shell} .app-bar: its effective `top` is `{(tops.Count == 0 ? "unset" : tops[^1])}`, not 0. Inside an already-offset shell the offset must not be applied again.");
        }

        Assert.True(offenders.Count == 0,
            "A header inside a pinned shell takes the banner offset twice:\n  " + string.Join("\n  ", offenders)
            + "\n\nWHY: the shell has ALREADY moved itself below the banner. The roster's header needs the offset "
            + "because it shares the page with the banner; a session header does not, and they are the same class. "
            + "One offset per screen, applied by whichever box owns the offsetting.");
    }

    /// <summary>
    /// The banner must MEASURE itself and publish --voicemode-h. A guard on the shells alone would pass
    /// happily while the variable was never set - every shell would silently fall back to 0px and the
    /// banner would be back on top of the back button.
    /// </summary>
    [Fact]
    public void TheVoiceModeBanner_PublishesItsMeasuredHeight()
    {
        var path = Path.Combine(GetRepoRoot(), "apps", "mobile", "src", "components", "VoiceModeBanner.tsx");
        Assert.True(File.Exists(path), "apps/mobile/src/components/VoiceModeBanner.tsx is missing. If the banner was renamed, update this test - do not delete the contract.");

        var src = File.ReadAllText(path);
        Assert.True(src.Contains("--voicemode-h", StringComparison.Ordinal),
            "VoiceModeBanner no longer publishes --voicemode-h. Without it every pinned shell falls back to 0px and the banner covers the session screen's back arrow again.");
        Assert.True(src.Contains("ResizeObserver", StringComparison.Ordinal),
            "VoiceModeBanner no longer MEASURES itself. The bar wraps to two lines on a narrow phone and grows again when an error line appears - a hard-coded height is right on exactly one device.");
        Assert.True(Regex.IsMatch(src, @"""--voicemode-h"",\s*""0px"""),
            "VoiceModeBanner no longer resets --voicemode-h to 0px when it is not shown. A stale height pushes every screen down by a bar that is no longer on the page.");
    }

    /// <summary>
    /// The network banner must MEASURE itself and publish --connbanner-h, for exactly the reasons the
    /// voice-mode banner does. It is position:fixed, so it is out of the flow and pushes nothing down by
    /// itself - and it is the app's ONLY word about the network now that the always-on status pill is gone,
    /// so it is deliberately big and wraps to two or three lines on a narrow phone. Unmeasured, it painted
    /// over the voice-mode banner's "Turn off" button - the one way out of voice mode.
    /// </summary>
    [Fact]
    public void TheConnectionBanner_PublishesItsMeasuredHeight()
    {
        var path = Path.Combine(GetRepoRoot(), "apps", "mobile", "src", "components", "ConnectionBanner.tsx");
        Assert.True(File.Exists(path), "apps/mobile/src/components/ConnectionBanner.tsx is missing. If the banner was renamed, update this test - do not delete the contract.");

        var src = File.ReadAllText(path);
        Assert.True(src.Contains("--connbanner-h", StringComparison.Ordinal),
            "ConnectionBanner no longer publishes --connbanner-h. Without it every pinned shell falls back to 0px and the banner covers the screen's own header.");
        Assert.True(src.Contains("ResizeObserver", StringComparison.Ordinal),
            "ConnectionBanner no longer MEASURES itself. Its message wraps to two or three lines on a narrow phone - a hard-coded height is right on exactly one device.");
        Assert.True(Regex.IsMatch(src, @"""--connbanner-h"",\s*""0px"""),
            "ConnectionBanner no longer resets --connbanner-h to 0px when it is not shown. A stale height pushes every screen down by a bar that is no longer on the page - and this banner is hidden nearly all the time.");
    }

    /// <summary>Returns the body of the FIRST rule whose selector list contains <paramref name="selector"/> exactly.</summary>
    private static string? RuleBlock(string css, string selector)
    {
        // Comments must go first: this file documents the rules heavily, and a comment sitting above a
        // rule would otherwise be swallowed into that rule's selector text (and its commas would split it).
        var stripped = StripComments(css);
        foreach (Match m in Regex.Matches(stripped, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selectors = m.Groups[1].Value.Split(',').Select(s => s.Trim());
            if (selectors.Any(s => s == selector)) return m.Groups[2].Value;
        }
        return null;
    }

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
