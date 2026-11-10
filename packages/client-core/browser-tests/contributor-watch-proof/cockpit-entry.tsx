// The REAL Cockpit contributor Factories page (apps/cockpit), as the Cockpit mounts it: inside the shell's current-team
// provider, with the Cockpit's own stylesheets. Only the left rail is left out.
import "./fake-gateway";
import { createRoot } from "react-dom/client";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { CurrentTeamProvider } from "../../src/teams/CurrentTeam";
import { ContributorFactoriesPage } from "../../../../apps/cockpit/src/teams/collaborator/ContributorFactoriesPage";
import "../../../../apps/cockpit/src/styles.css";
import "../../../../apps/cockpit/src/components/components.css";

const start = window.location.hash.slice(1) || "/contributor/factories";
createRoot(document.getElementById("root")!).render(
  <CurrentTeamProvider>
    <MemoryRouter initialEntries={[start]}>
      <main className="proof-cockpit-main">
        <Routes>
          <Route path="/contributor/factories" element={<ContributorFactoriesPage />} />
        </Routes>
      </main>
    </MemoryRouter>
  </CurrentTeamProvider>,
);
