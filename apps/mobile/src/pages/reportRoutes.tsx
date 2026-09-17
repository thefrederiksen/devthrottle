import type { RouteObject } from "react-router-dom";
import { Reports } from "./Reports";
import { ReportView } from "./ReportView";

// The phone's two dev report routes, in ONE place (dev reports mission, phase 3b).
//
// They live here rather than inline in the app's route table so a test can mount the exact array the app
// mounts. A printed link lands on `/session/{sid}/reports/{rid}` from cold - no list first, nothing chosen
// on the way - and "the report opens from that address" is only worth proving against the app's own routes.
export const DEV_REPORT_ROUTES: RouteObject[] = [
  { path: "/session/:sessionId/reports", element: <Reports /> },
  { path: "/session/:sessionId/reports/:reportId", element: <ReportView /> },
];
