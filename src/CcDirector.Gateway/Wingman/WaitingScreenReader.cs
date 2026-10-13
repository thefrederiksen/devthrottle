using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What <see cref="WaitingScreenReader.ConfirmedMenuOnThePlanAsync"/> concluded: whether a model-confirmed menu owns
/// the screen, or - when the screen was menu-shaped and the caller's plan did not allow the model confirmation - the
/// plan's answer, which the request route turns into its 402 or 503. <see cref="IsMenu"/> is false whenever
/// <see cref="PlanRefusal"/> is set, because no menu claim was made.
/// </summary>
internal readonly record struct MenuConfirmation(bool IsMenu, TenantAiDecision? PlanRefusal);

/// <summary>
/// Reads a session's LIVE screen over the tunnel and classifies it with the pure
/// <see cref="WaitingScreenClassifier"/> (issue #2193). This is the ONE place that read-and-classify
/// step lives, so every surface that has to know "is this session sitting on a menu right now?" asks
/// the same question and gets the same answer: the wingman voice endpoint, the prompt front door's
/// menu guard, and the narration generator.
///
/// It costs one tunnel read and NO model call - the classifier is pure. That is what makes it usable
/// on the send path (where a model call would add seconds to every spoken reply) and on the narration
/// path (where it must not add provider cost to a turn).
///
/// Fail-closed is inherited from the classifier: an unreadable, blank, or ambiguous screen resolves to
/// <see cref="WaitingScreenKind.Blocked"/>, never to a menu and never to a typeable composer.
/// </summary>
internal static class WaitingScreenReader
{
    // The two SPOKEN lines this reader used to hold as English constants moved to SpokenPhrases with
    // issue #1009 - the product speaks them, so they exist in every language it speaks. They are
    // SpokenPhrases.WaitingScreenMenu and SpokenPhrases.WaitingScreenMenuNarrationSuffix, and both are
    // reached through a language resolved from the account's tenant.

    /// <summary>The short line for a card or a toast - the same fact as
    /// <see cref="Speech.SpokenPhrases.WaitingScreenMenu"/>, sized for a screen rather than a speaker.
    /// It stays ENGLISH: the mission translates what the product SAYS, not what it DISPLAYS, and every
    /// other label in the product is English.</summary>
    public const string MenuMessage =
        "This session is waiting on a menu. Open it in the Cockpit or on your machine and pick an option - "
        + "voice can't answer a menu yet.";

    /// <summary>
    /// Read the live screen grid for <paramref name="sid"/> and classify it. A read that throws, or that
    /// the owning Director does not answer, is <see cref="WaitingScreenKind.Blocked"/> - unreadable is
    /// never mistaken for either a menu or a composer.
    /// </summary>
    public static async Task<WaitingScreenKind> ClassifyAsync(
        SessionVerbClient route, string sid, CancellationToken ct = default)
    {
        ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: screen-grid read threw ({ex.Message}) - treating as unreadable");
            return WaitingScreenKind.Blocked;
        }
        if (grid is null)
            return WaitingScreenKind.Blocked;

        return WaitingScreenClassifier.Classify(
            grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);
    }

    /// <summary>
    /// True when the live screen is CONFIDENTLY a menu. This - not "not a composer" - is the phase 1 gate:
    /// only a positively recognized menu blocks a voice reply, so a screen the classifier merely cannot
    /// read keeps behaving exactly as it did before the guard existed. Blocking on unreadable too would be
    /// the stricter rule, but it would silently break ordinary voice replies, which is a worse outcome than
    /// the gap it would close.
    /// </summary>
    public static async Task<bool> IsMenuAsync(SessionVerbClient route, string sid, CancellationToken ct = default)
        => await ClassifyAsync(route, sid, ct) == WaitingScreenKind.Menu;

    /// <summary>
    /// The MODEL-CONFIRMED menu verdict (issue devthrottle_internal#1195) - the one every surface that BLOCKS
    /// on a menu must use. The session-115 misfire proved the pure classifier can be satisfied by ordinary
    /// prose (a numbered summary of finished work) plus a stale marker glyph, so the regex is demoted to a
    /// tripwire: it decides when the model must be asked, and it alone never convicts.
    ///
    /// The flow: read the grid once; anything unreadable is not a menu (phase-1 behavior unchanged). If the
    /// pure classifier sees no confident menu, that is final - no model call, which keeps ordinary sends at
    /// regex cost. If it does, the verdict cache is consulted by grid fingerprint - the narration call judges
    /// every turn's screen, so an unchanged screen is answered instantly with the model's verdict. Only a
    /// changed, menu-shaped screen pays for one <see cref="WingmanTranslator.DetectMenuAsync"/> call. The two
    /// failure directions are deliberate: no translator, or a model that cannot be reached while menu
    /// structure is on screen, BLOCKS (typing into a real picker presses Enter on an option the person never
    /// chose - the original #2193 disaster); an unreadable screen or a quiet classifier never blocks.
    /// </summary>
    public static async Task<bool> ConfirmedMenuAsync(
        SessionVerbClient route, string sid, TenantId tenant, WingmanTranslator? translator, CancellationToken ct = default)
        => (await ConfirmMenuAsync(route, sid, tenant, translator, askPlan: null, ct)).IsMenu;

    /// <summary>
    /// <see cref="ConfirmedMenuAsync"/> for a request route that has NOT asked the plan yet (Pricing Change mission,
    /// B1, review finding 1). The screen read and the pure classifier are free and answer for every plan: a screen that
    /// is plainly not a menu is "not a menu" for a Free member too, so a prompt they are allowed to send is never
    /// refused. Only when the classifier trips - the one point where a model would be asked - is
    /// <paramref name="askPlan"/> called, and a plan that does not allow the Wingman (or could not be read) comes back
    /// as <see cref="MenuConfirmation.PlanRefusal"/> with NO menu claim and no model call. The caller answers it with
    /// <see cref="HostedAi.HostedAiHttp.TenantAiRefusal"/>, exactly as the waiting-screen route does.
    ///
    /// Without this the refusal reached the model factory's backstop, threw, and landed in the catch below that
    /// answers "a menu" for an unreachable model - a false statement about the member's screen.
    /// </summary>
    public static Task<MenuConfirmation> ConfirmedMenuOnThePlanAsync(
        SessionVerbClient route, string sid, TenantId tenant, WingmanTranslator? translator,
        Func<TenantAiDecision> askPlan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(askPlan);
        return ConfirmMenuAsync(route, sid, tenant, translator, askPlan, ct);
    }

    private static async Task<MenuConfirmation> ConfirmMenuAsync(
        SessionVerbClient route, string sid, TenantId tenant, WingmanTranslator? translator,
        Func<TenantAiDecision>? askPlan, CancellationToken ct)
    {
        var notAMenu = new MenuConfirmation(false, null);
        var aMenu = new MenuConfirmation(true, null);
        ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: screen-grid read threw ({ex.Message}) - not a menu");
            return notAMenu;
        }
        if (grid is null || grid.Rows is null || grid.Rows.Count == 0) return notAMenu;

        var kind = WaitingScreenClassifier.Classify(
            grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);
        if (kind != WaitingScreenKind.Menu) return notAMenu;

        // The classifier above is free; everything below is the model's verdict, so only it is on the plan.
        if (askPlan is not null && askPlan() is { IsAllowed: false } plan)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: menu-shaped screen, and the plan does not allow the model confirmation (verdict={plan.Verdict}) - no menu claim, no model call");
            return new MenuConfirmation(false, plan);
        }

        var key = $"{tenant}/{sid}";
        var hash = WingmanScreenVerdictCache.HashRows(grid.Rows);
        if (WingmanScreenVerdictCache.TryGet(key, hash, out var cached))
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: menu-shaped screen, cached model verdict '{cached}'");
            return cached == "menu" ? aMenu : notAMenu;
        }

        if (translator is null)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: menu-shaped screen and no judge available - fail closed");
            return aMenu;
        }

        try
        {
            var menu = await translator.DetectMenuAsync(tenant, string.Join("\n", grid.Rows), ct);
            WingmanScreenVerdictCache.Store(key, hash, menu.IsMenu ? "menu" : "not-menu");
            FileLog.Write($"[WaitingScreenReader] sid={sid}: menu-shaped screen, model says isMenu={menu.IsMenu}");
            return menu.IsMenu ? aMenu : notAMenu;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WaitingScreenReader] sid={sid}: menu-shaped screen, model unreachable ({ex.Message}) - fail closed");
            return aMenu;
        }
    }

    /// <summary>The wire word for a classified screen: what the phone and the Cockpit read.</summary>
    public static string KindWord(WaitingScreenKind kind) => kind switch
    {
        WaitingScreenKind.Menu => "menu",
        WaitingScreenKind.PlainText => "text",
        _ => "blocked",
    };
}
