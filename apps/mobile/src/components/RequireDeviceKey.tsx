import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { hasDeviceKey } from "@devthrottle/client-core/auth/deviceKey";

// The phone's auth gate (issue #908): every real screen requires an enrolled device key. Without one the
// phone is sent to Sign in. /signin and /device-callback sit OUTSIDE the gate so an unenrolled phone can
// reach them. hasDeviceKey() is read at navigation time, so enrolling (or a 401-triggered clear) re-gates
// on the next route.
//
// THE REQUESTED ROUTE RIDES THROUGH THE SIGN-IN ROUND TRIP, in `next=`, exactly as the Cockpit's gate does
// (issue #1088; the shared SignIn and DeviceCallback already carry it from there to the landing). Without
// it a signed-out phone that opened a printed report address signed in and landed on the roster, having
// silently thrown away the one thing the owner had clicked (dev reports mission, phase 3b).
//
// It lives in its own file so that behaviour can be mounted in a test. In main.tsx it was unreachable:
// importing that module starts the whole app.
export function RequireDeviceKey({ children }: { children: ReactNode }) {
  const location = useLocation();
  if (hasDeviceKey()) return <>{children}</>;
  const next = encodeURIComponent(location.pathname + location.search);
  return <Navigate to={`/signin?next=${next}`} replace />;
}
