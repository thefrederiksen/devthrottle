import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { AlertsBell } from "@devthrottle/client-core/alerts/AlertsBell";
import { useAlerts } from "@devthrottle/client-core/alerts/AlertsProvider";
import { setAlertCountInTitle } from "./documentTitle";

// ALERTS IN THE COCKPIT (the Alerts mission, section 5.5). The bell and its tray are the shared client-core components;
// this file is only the Cockpit's frame around them: where the bell sits (the left rail's head), where the tray opens
// (a popover beside the bell), the deep link a notification lands on, and the count in the browser tab's title.
//
// The rail scrolls, so a popover positioned inside it would be cut off at its edge. The tray is therefore placed on the
// page (position fixed) at the bell's own corner, measured when it opens. That is done only through the position
// variables alerts.css documents - the Cockpit sets no rule of the tray's own.

const TRAY_GAP_PX = 6;
/** The least room left between the tray and the window's edge. */
const EDGE_PX = 8;

/** The bell in the rail head. `?alert={id}` in the address opens the tray on that alert; closing it takes the
 *  parameter out again in the same render, so the tray never reopens on a link already used. */
export function RailAlertsBell() {
  const location = useLocation();
  const navigate = useNavigate();
  const wrap = useRef<HTMLDivElement>(null);
  const [anchor, setAnchor] = useState<{ top: number; left: number } | null>(null);
  const deepLinkAlert = new URLSearchParams(location.search).get("alert");

  const onOpenChange = useCallback(
    (open: boolean) => {
      if (open) {
        const bell = wrap.current?.querySelector<HTMLElement>(".alerts-bell");
        if (bell) {
          const r = bell.getBoundingClientRect();
          setAnchor({ top: r.bottom + TRAY_GAP_PX, left: r.left });
        }
        return;
      }
      if (deepLinkAlert === null) return;
      const params = new URLSearchParams(location.search);
      params.delete("alert");
      const search = params.toString();
      navigate({ pathname: location.pathname, search: search ? `?${search}` : "" }, { replace: true, state: location.state });
    },
    [deepLinkAlert, location.pathname, location.search, location.state, navigate],
  );

  // The tray opens at the bell's left edge. In a narrow window the bell sits further right than the tray is wide, so
  // once the tray is drawn its width is measured and it is moved left just enough to stay inside the window - found in
  // the Phase 4 QA run, where a 390px-wide Cockpit drew the tray 125px past the right edge.
  useLayoutEffect(() => {
    if (!anchor) return;
    const tray = wrap.current?.querySelector<HTMLElement>(".alerts-tray");
    if (!tray) return;
    const fits = Math.max(EDGE_PX, window.innerWidth - EDGE_PX - tray.getBoundingClientRect().width);
    if (anchor.left > fits) setAnchor({ top: anchor.top, left: fits });
  }, [anchor]);

  const style = anchor
    ? ({ "--alerts-tray-top": `${anchor.top}px`, "--alerts-tray-left": `${anchor.left}px` } as CSSProperties)
    : undefined;
  return (
    <div className="rail-alerts" ref={wrap} style={style} data-testid="rail-alerts">
      <AlertsBell where="cockpit" openAlertId={deepLinkAlert} onOpenChange={onOpenChange} />
    </div>
  );
}

/** Keeps the browser tab's title carrying the count of alerts that need an answer: "(2) DevThrottle Cockpit". */
export function AlertsTitleCount() {
  const { needsAnswerCount } = useAlerts();
  useEffect(() => {
    setAlertCountInTitle(needsAnswerCount);
  }, [needsAnswerCount]);
  useEffect(() => () => setAlertCountInTitle(0), []);
  return null;
}
