// The REAL shared player, drawing the committed contract fixture (the very JSON the Gateway's fold test produces).
// #hub, #lines or #empty picks the sample.
import { createRoot } from "react-dom/client";
import { FactoryWatchPlayer } from "../../src/factory/watch/FactoryWatchPlayer";
import type { FactoryWatchView } from "../../src/factory/watch/factoryWatchTypes";
import fixture from "../../src/factory/watch/factoryWatchView.fixture.json";

const samples = fixture as unknown as Record<string, FactoryWatchView>;
const name = window.location.hash.replace(/^#/, "") || "hub";
const view = samples[name];
if (!view) throw new Error(`No sample '${name}' in the fixture; use #hub, #lines or #empty.`);
createRoot(document.getElementById("root")!).render(<FactoryWatchPlayer view={view} />);
