// The REAL phone contributor Factories page (apps/mobile), with the phone's own stylesheet and the providers its gated
// layout mounts around every screen.
import "./fake-gateway";
import { createRoot } from "react-dom/client";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { AlertsProvider } from "../../src/alerts/AlertsProvider";
import { LicenseAlarmsProvider } from "../../src/licenses/LicenseAlarmIndicator";
import { ContributorFactories } from "../../../../apps/mobile/src/pages/ContributorFactories";
import "../../../../apps/mobile/src/styles.css";

const start = window.location.hash.slice(1) || "/contributor/factories";
createRoot(document.getElementById("root")!).render(
  <MemoryRouter initialEntries={[start]}>
    <AlertsProvider enabled={false}>
      <LicenseAlarmsProvider enabled={false}>
        <Routes>
          <Route path="/contributor/factories" element={<ContributorFactories />} />
        </Routes>
      </LicenseAlarmsProvider>
    </AlertsProvider>
  </MemoryRouter>,
);
