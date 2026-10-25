import { useCallback, useEffect, useRef } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { AlertsBell } from "@devthrottle/client-core/alerts/AlertsBell";

// The bell in the phone's app bar (the Alerts mission, section 5.4: "present on every screen that has the app bar").
// ONE component, mounted in every app bar the phone draws - the roster's, the session screens' (SessionAppBar) and each
// page's own - so the control, its count and the deep link are the same on every screen and cannot drift apart.
//
// Pressing it opens the Alerts screen (phase 5: a full screen of its own, never a sheet over the page). A push
// notification opens the app on ?alert={id} on whatever screen; that is turned into the Alerts screen on that alert,
// in place of the address it came in on, so the link is used once.

/** Where the Alerts screen's back arrow returns to, carried in the navigation's state. */
export interface AlertsScreenState {
  /** The address the owner was on. */
  back: string;
  /** True when the Alerts screen was pushed on top of it (back pops), false when it replaced a deep link. */
  pushed: boolean;
  /** That address's own router state (the roster's tab, say), put back with it when Back replaces a deep link. Its
   *  one-shot commands are already spent: the screens that carry them scrub them on arrival (VoiceMode). */
  backState?: unknown;
}

export const ALERTS_PATH = "/alerts";

// Set when the Alerts screen closes, so the bell on the screen the owner returns to takes focus back - the page form of
// a dialog returning focus to the control that opened it.
let focusBellOnReturn = false;

/** The Alerts screen calls this as it closes. */
export function returnFocusToBell(): void {
  focusBellOnReturn = true;
}

export function AppBarAlerts() {
  const location = useLocation();
  const navigate = useNavigate();
  const bell = useRef<HTMLButtonElement>(null);
  const deepLinkAlert = new URLSearchParams(location.search).get("alert");

  useEffect(() => {
    if (!focusBellOnReturn) return;
    focusBellOnReturn = false;
    bell.current?.focus();
  }, []);

  useEffect(() => {
    if (deepLinkAlert === null) return;
    const params = new URLSearchParams(location.search);
    params.delete("alert");
    const rest = params.toString();
    const state: AlertsScreenState = { back: location.pathname + (rest ? `?${rest}` : ""), pushed: false, backState: location.state };
    navigate({ pathname: ALERTS_PATH, search: `?alert=${encodeURIComponent(deepLinkAlert)}` }, { replace: true, state });
  }, [deepLinkAlert, location.pathname, location.search, location.state, navigate]);

  const open = useCallback(() => {
    const state: AlertsScreenState = { back: location.pathname + location.search, pushed: true };
    navigate(ALERTS_PATH, { state });
  }, [location.pathname, location.search, navigate]);

  return <AlertsBell where="phone" onOpenPage={open} buttonRef={bell} />;
}
