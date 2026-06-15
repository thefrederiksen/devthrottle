// cockpit-mobile.js - mobile detection for the "Open Voice Mode" offer on the Cockpit home (issue #427).
//
// The Cockpit home is a full desktop dashboard. On a phone we OFFER (never force) the mobile-first
// Voice Mode page at /voice via a dismissible banner. "Mobile" = a mobile user-agent OR a narrow
// viewport (<= 720px, matching app.css's nav-rail collapse breakpoint). Dismissal is remembered in
// localStorage so the banner does not reappear in the same browser.
(function () {
    "use strict";

    var DISMISS_KEY = "cockpit.voiceOffer.dismissed";
    var VIEWPORT_MAX = 720;
    // Common mobile-device tokens; covers phones (the target) and small tablets.
    var MOBILE_UA = /Android|webOS|iPhone|iPod|BlackBerry|IEMobile|Opera Mini|Mobile/i;

    function isMobileBrowser() {
        var ua = navigator.userAgent || "";
        if (MOBILE_UA.test(ua)) {
            return true;
        }
        // Fall through to viewport width so phone-sized windows (and emulated viewports) also count.
        var width = window.innerWidth || document.documentElement.clientWidth || 0;
        return width > 0 && width <= VIEWPORT_MAX;
    }

    function isDismissed() {
        try {
            return localStorage.getItem(DISMISS_KEY) === "1";
        } catch (e) {
            // Storage unavailable (private mode / disabled): treat as not dismissed so the offer shows.
            return false;
        }
    }

    window.cockpitMobile = {
        // Returns the state the Home page needs to decide whether to render the offer.
        evaluate: function () {
            return { isMobile: isMobileBrowser(), dismissed: isDismissed() };
        },
        // Remembers that the user dismissed the offer so it stays hidden on later visits.
        dismiss: function () {
            try {
                localStorage.setItem(DISMISS_KEY, "1");
            } catch (e) {
                // Nothing we can do if storage is unavailable; the banner is already hidden in the DOM.
            }
        }
    };
})();
