import type { RouteObject } from "react-router-dom";
import { Reports } from "./Reports";
import { ReportView } from "./ReportView";
import { ReportLanding } from "./ReportLanding";

// The phone's dev report routes, in ONE place (dev reports mission, phase 3b).
//
// They live here rather than inline in the app's route table so a test can mount the exact array the app
// mounts. A printed link lands on `/session/{sid}/reports/{rid}` from cold - no list first, nothing chosen
// on the way - and "the report opens from that address" is only worth proving against the app's own routes.
//
// `/report/:reportId` is the LANDING the printed address `<gateway>/r/<report id>` sends a phone to. It is
// in this array for the same reason: it is the route the whole signed-out path depends on resolving, so the
// test that proves it resolves has to drive the array the app mounts.
export const DEV_REPORT_ROUTES: RouteObject[] = [
  { path: "/report/:reportId", element: <ReportLanding /> },
  { path: "/session/:sessionId/reports", element: <Reports /> },
  { path: "/session/:sessionId/reports/:reportId", element: <ReportView /> },
];
