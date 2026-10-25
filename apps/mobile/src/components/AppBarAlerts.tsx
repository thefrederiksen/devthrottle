import { useCallback } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { AlertsBell } from "@devthrottle/client-core/alerts/AlertsBell";

// The bell in the phone's app bar (the Alerts mission, section 5.4: "present on every screen that has the app bar").
// ONE component, mounted in every app bar the phone draws - the roster's, the session screens' (SessionAppBar) and each
// page's own - so the control, its count and the deep link are the same on every screen and cannot drift apart.
//
// A push notification opens the app on ?alert={id}; the tray opens on that alert, and closing the tray takes the
// parameter out of the address in the same render, so the tray can never reopen on a link already used.
export function AppBarAlerts() {
  const location = useLocation();
  const navigate = useNavigate();
  const deepLinkAlert = new URLSearchParams(location.search).get("alert");
  const onOpenChange = useCallback(
    (open: boolean) => {
      if (open || deepLinkAlert === null) return;
      const params = new URLSearchParams(location.search);
      params.delete("alert");
      const search = params.toString();
      navigate({ pathname: location.pathname, search: search ? `?${search}` : "" }, { replace: true, state: location.state });
    },
    [deepLinkAlert, location.pathname, location.search, location.state, navigate],
  );
  return <AlertsBell where="phone" openAlertId={deepLinkAlert} onOpenChange={onOpenChange} />;
}
