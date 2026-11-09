// The REAL shared player, drawing the committed contract fixture (the very JSON the Gateway's fold test produces).
// #hub, #lines, #empty or #contributor picks the sample (contributor: the hand-written piece 3 shape).
import { createRoot } from "react-dom/client";
import { FactoryWatchPlayer } from "../../src/factory/watch/FactoryWatchPlayer";
import type { FactoryWatchView } from "../../src/factory/watch/factoryWatchTypes";
import fixture from "../../src/factory/watch/factoryWatchView.fixture.json";
import contributor from "../../src/factory/watch/factoryWatchView.contributor.fixture.json";

const samples = { ...(fixture as unknown as Record<string, FactoryWatchView>), contributor: contributor as unknown as FactoryWatchView };
const name = window.location.hash.replace(/^#/, "") || "hub";
const view = samples[name];
if (!view) throw new Error(`No sample '${name}' in the fixture; use #hub, #lines, #empty or #contributor.`);
createRoot(document.getElementById("root")!).render(<FactoryWatchPlayer view={view} />);
