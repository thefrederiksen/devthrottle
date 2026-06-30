import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { VoiceMode } from "./pages/VoiceMode";
import { ensureGatewayCookie } from "./api/client";
import "./styles.css";

// Mirror the injected per-machine token into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. The cookie exposes nothing the page does not already hold (window.__GW_TOKEN__).
ensureGatewayCookie();

// The app is served under /m, so the router is rooted there. A hard navigation to a deep link
// (e.g. /m/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side.
const router = createBrowserRouter(
  [
    { path: "/", element: <Home /> },
    { path: "/new", element: <NewSession /> },
    { path: "/session/:sessionId", element: <Terminal /> },
    { path: "/session/:sessionId/chat", element: <Chat /> },
    { path: "/session/:sessionId/voice", element: <VoiceMode /> },
  ],
  { basename: "/m" }
);

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Root element #root not found in the document");
}

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>
);
