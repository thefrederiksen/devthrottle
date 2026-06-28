import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { Home } from "./pages/Home";
import { Terminal } from "./pages/Terminal";
import "./styles.css";

// The app is served under /m, so the router is rooted there. A hard navigation to a deep link
// (e.g. /m/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side.
const router = createBrowserRouter(
  [
    { path: "/", element: <Home /> },
    { path: "/session/:sessionId", element: <Terminal /> },
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
