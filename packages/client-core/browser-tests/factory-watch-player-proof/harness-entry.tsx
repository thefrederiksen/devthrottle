// The REAL shared player, drawing the committed contract fixture (the very JSON the Gateway's fold test produces).
// #hub, #lines, #empty, #contributor-own or #contributor-shared picks the sample; both fixtures come from the Gateway.
import { createRoot } from "react-dom/client";
import { FactoryWatchPlayer } from "../../src/factory/watch/FactoryWatchPlayer";
import type { FactoryWatchView } from "../../src/factory/watch/factoryWatchTypes";
import fixture from "../../src/factory/watch/factoryWatchView.fixture.json";
import contributor from "../../src/factory/watch/factoryWatchView.contributor.fixture.json";

const contributorSamples = contributor as unknown as Record<string, FactoryWatchView>;
const samples: Record<string, FactoryWatchView> = {
  ...(fixture as unknown as Record<string, FactoryWatchView>),
  "contributor-own": contributorSamples.own,
  "contributor-shared": contributorSamples.shared,
};
const name = window.location.hash.replace(/^#/, "") || "hub";
const view = samples[name];
if (!view) throw new Error(`No sample '${name}' in the fixture; use #hub, #lines, #empty, #contributor-own or #contributor-shared.`);
createRoot(document.getElementById("root")!).render(<FactoryWatchPlayer view={view} />);
