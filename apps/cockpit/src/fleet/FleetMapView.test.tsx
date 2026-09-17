// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";
import type { SharedRoster } from "@devthrottle/client-core/fleet/rosterStore";

// Rendered proof for the "By machine" pivot's machine fold: a machine that has a Director but no sessions
// must still appear as a lane, an idle Director inside a machine must render as a "free slot" rather than
// vanishing, and an UNREACHABLE machine must render dimmed and dated rather than being dropped. Only the
// machine pivot does this - repository / working tree / agent still group real sessions only. The pure
// folding is unit tested in fleetMapFormat.test; this test proves the view actually paints the lane, the
// placeholder, and the unreachable sub-header.

// The Fleet Map reads the ONE shared roster store; mock it so the test drives an exact fleet without a
// Gateway. Canvas measures the DOM with ResizeObserver, which jsdom lacks - stub it so layout runs.
const rosterValue: { current: SharedRoster } = {
  current: { sessions: [], machineErrors: [], directors: [], unreachableBanner: null, error: null, refreshNow: () => {} },
};
vi.mock("@devthrottle/client-core/fleet/rosterStore", () => ({
  useSharedRoster: () => rosterValue.current,
}));
vi.mock("react-router-dom", () => ({ useNavigate: () => vi.fn() }));

// The Fleet Map "+ New session" button opens the SAME NewSessionDialog the Sessions tab uses, which
// loads its machine / repo / agent pickers from the Gateway. Mock those data calls so the dialog renders
// against a fixed fleet without a real Gateway. getDirectors is what proves the pre-selection: the dialog
// default-selects the NEWEST-started Director, so we make "soren-1" newest and assert the CLICKED
// "north-1" is selected instead - which can only happen if the Fleet Map passed it through.
const getDirectorsMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: (...args: unknown[]) => getDirectorsMock(...args),
  getRepos: () => Promise.resolve([]),
  getAgents: () => Promise.resolve([]),
  createSession: () => Promise.resolve({ sessionId: "new" }),
  gatewayErrorMessage: (e: unknown) => String(e),
  // The colour legend is read from the Gateway (GET /gateway/session-colours) through the shared transport.
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
  gatewayFetch: () =>
    Promise.resolve(new Response(JSON.stringify(LEGEND), { status: 200, headers: { "Content-Type": "application/json" } })),
}));

const LEGEND = {
  entries: [
    { colour: "red", hex: "#EF4444", title: "Needs you", means: "The session has stopped and is waiting for you.", asksForYou: "Yes" },
    { colour: "blue", hex: "#3B82F6", title: "Working", means: "The agent is running a turn right now.", asksForYou: "No" },
    { colour: "purple", hex: "#A855F7", title: "Carrying on", means: "The Wingman judged it will continue on its own.", asksForYou: "No" },
  ],
  broken: { hex: "#FF00FF", title: "Magenta", means: "Not a state." },
  verdictNote: "Some colours need the verdicts switched on.",
};

import { FleetMapView } from "./FleetMapView";

// A fully Gateway-stamped session (effectiveColor / stateLabel / effectiveColorHex are required fields the
// dumb client renders verbatim, so a fixture missing them would throw or paint the protocol sentinel).
function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: "s1",
    machineName: "SOREN_NORTH",
    directorId: "north-1",
    repoName: "thefrederiksen/devthrottle",
    repoPath: "D:/ReposFred/devthrottle",
    agent: "ClaudeCode",
    activityState: "Working",
    createdAt: "2026-07-23T10:00:00Z",
    number: 104,
    name: "release",
    effectiveColor: "blue",
    stateLabel: "Working",
    effectiveColorHex: "#3b82f6",
    ...overrides,
  } as SessionDto;
}

function director(overrides: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId: "d", machineName: "SOREN", state: "online", ...overrides };
}

beforeEach(() => {
  window.localStorage.clear();
  // jsdom has no ResizeObserver; a no-op stub is enough for the Canvas layout effect.
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});

afterEach(() => cleanup());

describe("FleetMapView - By machine free slots", () => {
  it("shows an idle machine as a lane with a free slot, even though it has no sessions", () => {
    rosterValue.current = {
      sessions: [session()], // one busy session on SOREN_NORTH
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "soren-1", machineName: "SOREN", state: "online" }), // idle, no sessions
        director({ directorId: "mac-1", machineName: "Sorens-Mac-mini", state: "online" }), // idle
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The header (and the canvas root) count all three reachable machines, not just the one running work.
    expect(screen.getAllByText(/\b3 machines\b/).length).toBeGreaterThan(0);

    // The idle machines each get a lane header - they were completely absent before this fix.
    expect(screen.getByText("SOREN")).toBeTruthy();
    expect(screen.getByText("Sorens-Mac-mini")).toBeTruthy();

    // Idle Directors render as free slots (one per idle machine here).
    const freeSlots = screen.getAllByText(/free slot/i);
    expect(freeSlots.length).toBe(2);

    // The busy machine still shows its session and does NOT show a free slot for its only Director.
    expect(screen.getByText("release")).toBeTruthy();
  });

  it("does not fold idle Directors into the repository pivot - only machine shows capacity", () => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "repo");
    rosterValue.current = {
      sessions: [session()],
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "soren-1", machineName: "SOREN", state: "online" }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The repository pivot groups real sessions; an idle machine is not a repository, so no free slots.
    expect(screen.queryByText(/free slot/i)).toBeNull();
    expect(screen.getByText("thefrederiksen/devthrottle")).toBeTruthy();
  });

  // An offline machine used to be EXCLUDED here, on the argument that it is not available capacity. That
  // argument deleted the machine from the map altogether - which is the delete-instead-of-dim defect the
  // Gateway has stopped committing (it now serves an unreachable machine's sessions with their age). The
  // machine is shown, dimmed and dated; what it must NOT do is claim to be a free slot or offer an action
  // that cannot be honoured.
  it("shows an offline machine, dated and with no new-session action - dimmed, never dropped", () => {
    rosterValue.current = {
      // Director ids whose last segment differs, because the sub-header shortens a Director to that
      // segment ("Director alpha"), and two ids ending in the same segment would be indistinguishable.
      sessions: [session({ directorId: "north-alpha" })],
      machineErrors: [],
      directors: [
        director({ directorId: "north-alpha", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "dead-beta", machineName: "DEAD_MACHINE", state: "offline", lastSeenAgeSeconds: 420 }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The lane exists, and it says what state the machine is in and how old that answer is.
    expect(screen.getByText("DEAD_MACHINE")).toBeTruthy();
    expect(screen.getByText(/Offline - last seen 7m ago/)).toBeTruthy();
    // It is not offered as capacity: no "free slot" line, and no button that could not be honoured.
    expect(screen.queryByText(/free slot/i)).toBeNull();
    // NOTE THE NOUN. This placeholder sits under a DIRECTOR sub-header and used to read "machine
    // unreachable" - on a machine that may be running other Directors perfectly well. It names the
    // Director now, which is the only thing the Gateway actually reported.
    expect(screen.getByText(/this director cannot be reached/i)).toBeTruthy();
    expect(screen.queryByText(/machine unreachable/i)).toBeNull();
    expect(screen.queryByRole("button", { name: /Start a new session on Director beta/ })).toBeNull();
    // The reachable machine keeps its own action.
    expect(screen.getByRole("button", { name: /Start a new session on Director alpha/ })).toBeTruthy();
    // Both machines are counted, so the header agrees with the lanes below it.
    expect(screen.getAllByText(/\b2 machines\b/).length).toBeGreaterThan(0);
  });
});

describe("FleetMapView - By director pivot (devthrottle_internal#1177)", () => {
  it("gives every Director its own lane by NAME, keeps idle ones as free slots and offline ones dimmed", () => {
    // Selecting the pivot through storage also proves initialPivot() accepts "director" - without that
    // allow-list entry the saved choice would silently reset to "machine" and this whole test would fail.
    window.localStorage.setItem("cockpit.fleetMapPivot", "director");
    rosterValue.current = {
      sessions: [session({ directorId: "north-alpha", machineName: "SOREN_NORTH" })],
      machineErrors: [],
      directors: [
        // A renamed Director: the lane must read as its display name, not as 8 hex chars.
        director({ directorId: "north-alpha", machineName: "SOREN_NORTH", state: "online", displayName: "SOREN_NORTH_SLOT_2" }),
        // An idle, unnamed Director on the SAME machine: its own lane (the whole point of the pivot),
        // labelled with the historical short-id fallback, rendered as a free slot.
        director({ directorId: "north-idle", machineName: "SOREN_NORTH", state: "online" }),
        // An offline Director: shown dated with no action, never dropped (same rule as the machine pivot).
        director({ directorId: "dead-beta", machineName: "DEAD_MACHINE", state: "offline", lastSeenAgeSeconds: 420 }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // Three lanes: the named one, the idle one (short-id fallback), the offline one.
    expect(screen.getByText("SOREN_NORTH_SLOT_2")).toBeTruthy();
    expect(screen.getByText("Director idle")).toBeTruthy();
    expect(screen.getByText("Director beta")).toBeTruthy();

    // The busy lane shows its session; the machine rides in the subtitle.
    expect(screen.getByText("release")).toBeTruthy();
    expect(screen.getByText("SOREN_NORTH / 1 session")).toBeTruthy();

    // The idle Director is a free slot; the offline one is unreachable and dated.
    expect(screen.getAllByText(/free slot/i)).toHaveLength(1);
    expect(screen.getByText(/this director cannot be reached/i)).toBeTruthy();
    expect(screen.queryByText(/machine unreachable/i)).toBeNull();
    expect(screen.getByText(/Offline - last seen 7m ago/)).toBeTruthy();

    // "+ New session" is offered on the reachable lanes and withheld on the offline one.
    expect(screen.getByRole("button", { name: /Start a new session on SOREN_NORTH_SLOT_2/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Start a new session on Director idle/ })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Start a new session on Director beta/ })).toBeNull();
  });
});

describe("FleetMapView - Director sub-header new-session button", () => {
  it("opens the shared dialog pre-targeted to the clicked Director, not the newest one", async () => {
    // The dialog would otherwise default to the NEWEST-started Director. Make "soren-bbb" newest, so if
    // the Fleet Map failed to pass the clicked Director through, the dialog would select SOREN_SOUTH.
    getDirectorsMock.mockResolvedValue([
      {
        directorId: "north-aaa",
        machineName: "SOREN_NORTH",
        displayName: "",
        version: "1",
        startedAt: "2026-07-20T00:00:00Z",
        lastSeen: "",
        controlEndpoint: "http://127.0.0.1:7880",
      },
      {
        directorId: "soren-bbb",
        machineName: "SOREN_SOUTH",
        displayName: "",
        version: "1",
        startedAt: "2026-07-24T00:00:00Z", // newest -> the dialog's default pick
        lastSeen: "",
        controlEndpoint: "http://127.0.0.1:7990",
      },
    ]);

    rosterValue.current = {
      sessions: [session({ directorId: "north-aaa", machineName: "SOREN_NORTH" })],
      machineErrors: [],
      directors: [director({ directorId: "north-aaa", machineName: "SOREN_NORTH", state: "online" })],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The machine pivot renders a "+ New session" button on the Director sub-header (top-right).
    const newBtn = screen.getByRole("button", { name: /Start a new session on Director/ });
    fireEvent.click(newBtn);

    // The SAME dialog the Sessions tab opens appears, and it default-selects the clicked Director's
    // machine (SOREN_NORTH) rather than the newest-started one (SOREN_SOUTH).
    const dialog = await screen.findByRole("dialog", { name: /Start a new session/ });
    await waitFor(() => {
      const north = dialog.querySelector("button.newsess-machine.sel");
      expect(north?.textContent).toContain("SOREN_NORTH");
    });
  });
});

// THE WARNING LINE ABOVE THE MAP. It used to be built in this view by counting the envelope's
// machineErrors rows and printing each one with the word "machine". Those rows are PER DIRECTOR, so a
// machine running three Directors and fifteen live sessions announced "1 machine unreachable on the last
// sweep: SOREN_NORTH" the moment one of its slots was shut down - a healthy machine reported as dead, from
// a count of the wrong noun. The verdict is now folded on the Gateway and printed verbatim, so these prove
// the view prints what it is given and invents nothing.
describe("FleetMapView - the unreachable banner is the Gateway's sentence", () => {
  it("prints the Gateway's banner verbatim", () => {
    rosterValue.current = {
      sessions: [session({ directorId: "north-alpha" })],
      machineErrors: [],
      directors: [director({ directorId: "north-alpha", machineName: "SOREN_NORTH", state: "online" })],
      unreachableBanner:
        "1 director could not be reached on the last sweep: Slot 5 on SOREN_NORTH (last seen 32m ago) - the rest of that machine is answering normally",
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    expect(screen.getByText(/Slot 5 on SOREN_NORTH \(last seen 32m ago\)/)).toBeTruthy();
  });

  // THE REGRESSION, and the assertion that fails on the old view: machineErrors is populated exactly as it
  // is for one unreachable slot, and the Gateway has ruled that this is not a dead machine. The old code
  // counted that row and rendered "1 machine unreachable on the last sweep: SOREN_NORTH" regardless.
  it("does NOT call a machine unreachable just because one of its Directors is", () => {
    rosterValue.current = {
      sessions: [session({ directorId: "north-alpha" })],
      machineErrors: [{ directorId: "north-slot5", machineName: "SOREN_NORTH", error: "director not connected to the tunnel" }],
      directors: [
        director({ directorId: "north-alpha", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "north-slot5", machineName: "SOREN_NORTH", state: "offline", lastSeenAgeSeconds: 1896 }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    expect(screen.queryByText(/machine unreachable on the last sweep/i)).toBeNull();
    expect(screen.queryByText(/could not be reached/i)).toBeNull();
    // Positive control: the map really did render this fleet, so the absence above is not an empty page.
    expect(screen.getByText("SOREN_NORTH")).toBeTruthy();
  });

  // A shut-down Director is ordinary. It is labelled, it is not offered as capacity it cannot provide, and
  // it raises no warning at all.
  it("renders a shut-down Director as not running, with no warning and no action", () => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "director");
    rosterValue.current = {
      sessions: [],
      machineErrors: [],
      directors: [
        director({
          directorId: "north-slot5",
          machineName: "SOREN_NORTH",
          displayName: "Slot 5",
          state: "stopped",
          lastSeenAgeSeconds: 1896,
          // The Gateway's finished presentation, rendered verbatim - not re-derived from the state here.
          stateLabel: "Not running",
          dataIsStale: true,
          canStartSession: false,
          emptySlotText: "No sessions - this director is not running",
        }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    expect(screen.getByText(/Not running - last seen 31m ago/)).toBeTruthy();
    expect(screen.getByText("No sessions - this director is not running")).toBeTruthy();
    // Not free capacity: its tunnel went with the process, so a start could not be delivered.
    expect(screen.queryByText(/free slot/i)).toBeNull();
    expect(screen.queryByRole("button", { name: /Start a new session on Slot 5/ })).toBeNull();
    // And nothing on this screen calls it a failure.
    expect(screen.queryByText(/unreachable/i)).toBeNull();
  });
});

describe("FleetMapView - the Sessions list's tree, across machines", () => {
  // The owner's case, 16 September: Architect 102 on the Mac Mini started Worker 146 on the Mac Mini and
  // Worker 124 on SOREN_NORTH. The Sessions list showed both under 102; the map showed 146 only, with 124
  // loose at the top of SOREN_NORTH's column.
  function fleet(): SessionDto[] {
    const mac = { machineName: "devthrottle-mac-mini", directorId: "mac-1", triageBucket: "active" };
    return [
      session({ sessionId: "102", number: 102, name: "AXI Tools - Architect", ...mac, createdAt: "2026-09-16T10:26:00Z" }),
      session({ sessionId: "146", number: 146, name: "AXI Tools - Worker - fix", ...mac, isControlled: true, controllerSessionId: "102" }),
      session({ sessionId: "124", number: 124, name: "AXI Tools - Worker - benchmark", triageBucket: "active", isControlled: true, controllerSessionId: "102" }),
      session({ sessionId: "144", number: 144, name: "cc-worktrees - Architect", triageBucket: "active" }),
    ];
  }

  beforeEach(() => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "machine");
    rosterValue.current = {
      sessions: fleet(),
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "mac-1", machineName: "devthrottle-mac-mini", state: "online" }),
      ],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };
  });

  it("collapses 102's crew by default and says what is inside it", () => {
    render(<FleetMapView />);
    const chevron = screen.getByRole("button", { name: /Expand the 2 sessions under AXI Tools - Architect/ });
    expect(chevron.getAttribute("aria-expanded")).toBe("false");
    expect(screen.getByText(/2 under it:/)).toBeTruthy();
    // Collapsed: neither Worker is drawn anywhere on the map - including SOREN_NORTH's column.
    expect(screen.queryByText("AXI Tools - Worker - benchmark")).toBeNull();
    expect(screen.queryByText("AXI Tools - Worker - fix")).toBeNull();
    expect(screen.getByText("cc-worktrees - Architect")).toBeTruthy();
  });

  it("expands to BOTH Workers under 102, the one on SOREN_NORTH saying where it runs, each drawn once", () => {
    render(<FleetMapView />);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 2 sessions under AXI Tools - Architect/ }));
    const crew = screen.getByLabelText("Sessions under AXI Tools - Architect");
    expect(crew.textContent).toContain("AXI Tools - Worker - fix");
    expect(crew.textContent).toContain("AXI Tools - Worker - benchmark");
    expect(screen.getAllByText("AXI Tools - Worker - benchmark")).toHaveLength(1);
    const card124 = screen.getByText("AXI Tools - Worker - benchmark").closest("article");
    expect(card124?.textContent).toContain("SOREN_NORTH");
    const card146 = screen.getByText("AXI Tools - Worker - fix").closest("article");
    expect(card146?.textContent).not.toContain("SOREN_NORTH");
    // The open crew is the Sessions list's remembered setting, so the two screens agree.
    expect(window.localStorage.getItem("dt.sessions.crewExpanded")).toContain("102");
  });

  it("By agent does not hide a session inside another column's crew", () => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "agent");
    rosterValue.current = {
      ...rosterValue.current,
      sessions: fleet().map((s) => (s.sessionId === "124" ? { ...s, agent: "Codex" } : s)),
    };
    render(<FleetMapView />);
    // 124 runs Codex, its parent ClaudeCode: in the Codex column it stands on its own.
    expect(screen.getByText("AXI Tools - Worker - benchmark")).toBeTruthy();
  });
});

describe("FleetMapView - what the colours mean", () => {
  beforeEach(() => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "machine");
    rosterValue.current = {
      sessions: [
        session({
          sessionId: "144",
          number: 144,
          name: "cc-worktrees - Architect",
          effectiveColor: "purple",
          effectiveColorHex: "#A855F7",
          stateLabel: "Monitor fix round 2 progress",
          // What the Director wrote before the Wingman judged the turn - the hover used to show this.
          lastStatusReason: "needs you",
        }),
      ],
      machineErrors: [],
      directors: [director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" })],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };
  });

  it("shows the Gateway's legend beside the map, every colour it sends", async () => {
    render(<FleetMapView />);
    const panel = screen.getByRole("complementary", { name: "What the colours mean" });
    for (const entry of LEGEND.entries) {
      expect(await screen.findByText(entry.means)).toBeTruthy();
      expect(panel.querySelector(`[data-colour="${entry.colour}"]`)).not.toBeNull();
    }
  });

  it("hovers a purple dot as Carrying on, not as the Director's 'needs you'", async () => {
    const { container } = render(<FleetMapView />);
    await screen.findByText(LEGEND.entries[2].means);
    const dot = container.querySelector(".fmap-card .fmap-dot") as HTMLElement;
    expect(dot.getAttribute("title")).toBe("Carrying on: Monitor fix round 2 progress");
  });
});
