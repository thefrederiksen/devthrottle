import { createRoot } from "react-dom/client";
import { MemoryRouter } from "react-router-dom";
import { AlertsProvider } from "../../../packages/client-core/src/alerts/AlertsProvider";
import { Alerts } from "../../../apps/mobile/src/pages/Alerts";
import "../../../apps/mobile/src/styles.css";
import { installFakeGateway, rootElement } from "./fake-gateway";

// The phone: its real Alerts screen, under the same provider the app mounts above its routes.
installFakeGateway();

createRoot(rootElement()).render(
  <AlertsProvider>
    <MemoryRouter initialEntries={["/alerts"]}>
      <Alerts />
    </MemoryRouter>
  </AlertsProvider>,
);
