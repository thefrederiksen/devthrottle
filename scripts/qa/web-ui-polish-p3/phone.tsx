import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { MOBILE_ROUTES } from "../../../apps/mobile/src/routes";
import { addAccount } from "../../../packages/client-core/src/auth/accountStore";
// The phone's own stylesheet, as its entry point loads it (apps/mobile/src/main.tsx).
import "../../../apps/mobile/src/styles.css";
import { installFakeGateway, reportId, sessionId } from "./fake-gateway";

// The phone: its real route table over the fake Gateway, signed in, opened on the page named in the address's hash.
installFakeGateway();
addAccount({ deviceKey: "capture-device-key", installId: "capture-install", email: "capture@example.com" });

const pages: Record<string, string> = {
  settings: "/settings",
  about: "/about",
  alerts: "/alerts",
  new: "/new",
  file: `/session/${sessionId}/file?path=${encodeURIComponent("D:/test/repository/README.md")}`,
  report: `/session/${sessionId}/reports/${reportId}`,
  session: `/session/${sessionId}`,
};
const page = pages[window.location.hash.slice(1)];
if (!page) throw new Error(`Unknown page ${window.location.hash}`);
const router = createMemoryRouter(MOBILE_ROUTES, { initialEntries: [page] });
const host = document.getElementById("root");
if (!host) throw new Error("No #root element");
createRoot(host).render(<RouterProvider router={router} />);
