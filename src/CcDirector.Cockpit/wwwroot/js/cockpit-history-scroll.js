// Cockpit History tab: sticky-bottom scroll. The desktop History tab shipped with three scroll
// bugs (GitHub #744): a one-shot deferred scroll that landed short as markdown/link bubbles grew
// their height, no scroll-to-bottom on tab activation, and a refresh that yanked a scrolled-up
// reader back down. This module is the web answer to all three.
//
// The model is "follow the bottom until the user scrolls away":
// - The container auto-follows new content while the user is at (or near) the bottom.
// - The moment the user scrolls up to read earlier history, following stops and new turns no
//   longer move the view.
// - Scrolling back to the bottom re-engages following.
//
// .NET drives rendering; this module only measures and sets scrollTop, and reports follow-state
// changes back so the component knows whether to stick to the bottom after the next render.
//
// Loaded as an ES module via Blazor JS interop (import "./js/cockpit-history-scroll.js").

// How close to the bottom (in CSS pixels) still counts as "at the bottom". Generous enough that
// a settling layout pass or a partial last line does not drop us out of follow mode (the desktop
// bug: a scroll that landed >60px short tripped its own guard and wedged the view cut off).
const BOTTOM_THRESHOLD = 80;

// One record per live container, keyed by the element, so repeated init calls (re-render,
// reconnect) replace cleanly without leaking scroll listeners.
const registry = new WeakMap();

function isAtBottom(el) {
    return el.scrollHeight - el.scrollTop - el.clientHeight <= BOTTOM_THRESHOLD;
}

// Attach the scroll listener and start in follow mode at the bottom. dotnetRef receives
// UpdateFollow(bool) whenever the user crosses the at-bottom boundary, so the component never
// auto-scrolls a reader who has deliberately scrolled up.
export function init(el, dotnetRef) {
    if (!el) return;
    dispose(el);

    const state = { following: true };
    const onScroll = () => {
        const atBottom = isAtBottom(el);
        if (atBottom !== state.following) {
            state.following = atBottom;
            // Fire-and-forget: a dropped notification self-heals on the next scroll event.
            dotnetRef.invokeMethodAsync("UpdateFollow", atBottom);
        }
    };

    el.addEventListener("scroll", onScroll, { passive: true });
    registry.set(el, { onScroll });

    // Tab activation lands at the bottom (desktop bug #744 item 2): the pane mounts fresh when
    // the History tab is shown, so this is the activation scroll.
    scrollToBottom(el);
}

// Jump to the true bottom. Called by the component after a render that added content, but only
// when it is still following - so a scrolled-up reader is never disturbed.
export function scrollToBottom(el) {
    if (!el) return;
    el.scrollTop = el.scrollHeight;
}

// Whether the container is currently at the bottom. Lets the component decide, just BEFORE it
// mutates the list, whether it should stick to the bottom after the upcoming render.
export function atBottom(el) {
    return !!el && isAtBottom(el);
}

export function dispose(el) {
    if (!el) return;
    const rec = registry.get(el);
    if (rec) {
        el.removeEventListener("scroll", rec.onScroll);
        registry.delete(el);
    }
}
