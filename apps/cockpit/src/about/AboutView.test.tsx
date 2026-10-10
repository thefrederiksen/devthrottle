// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import type { AboutInfo } from "@devthrottle/client-core/about/aboutClient";

// The About page renders whatever the Gateway ruled (CLAUDE.md rule 7). These tests pin the two things a
// regression would quietly get wrong: an unstamped bundle is REPORTED as unstamped rather than rendered as
// a blank build, and the internal listen port appears only when the Gateway hands one over - the hosted
// service omits it because it composes with nothing a caller can reach.
const getAboutMock = vi.fn();
vi.mock("@devthrottle/client-core/about/aboutClient", () => ({
  getAbout: (...args: unknown[]) => getAboutMock(...args),
}));
// describeAndReport is the one way an error reaches the screen (and the Gateway); here it only has to say the error.
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => String(err),
}));

import { AboutView, formatBundle, formatUptime } from "./AboutView";

function about(overrides: Partial<AboutInfo> = {}): AboutInfo {
  return {
    version: "0.6.15+abcdef1",
    buildDate: "2026-07-26 09:00:00",
    cockpit: { commit: "aaaaaaa", buildTime: "2026-07-26T09:38:49.000Z" },
    mobile: { commit: "bbbbbbb", buildTime: "2026-07-26T09:38:49.000Z" },
    deployment: "Self-hosted",
    address: "https://box.tailnet.ts.net",
    cockpitUrl: "https://box.tailnet.ts.net/cockpit",
    port: 7878,
    uptimeSeconds: 3 * 86400 + 4 * 3600 + 5 * 60,
    serverTime: "2026-07-26T10:00:00.000Z",
    ...overrides,
  };
}

afterEach(() => {
  cleanup();
  getAboutMock.mockReset();
});

describe("formatUptime", () => {
  it("drops the days and hours that are zero and always keeps minutes", () => {
    expect(formatUptime(0)).toBe("just started");
    expect(formatUptime(90)).toBe("1m");
    expect(formatUptime(3 * 3600 + 5 * 60)).toBe("3h 5m");
    expect(formatUptime(2 * 86400 + 60)).toBe("2d 1m");
  });
});

describe("formatBundle", () => {
  it("names the commit and the build time", () => {
    expect(formatBundle({ commit: "a1b2c3d", buildTime: "2026-07-26T09:38:49.000Z" })).toBe(
      "a1b2c3d (built 2026-07-26 09:38:49 UTC)",
    );
  });

  it("falls back to the commit alone when the stamp carries no time", () => {
    expect(formatBundle({ commit: "a1b2c3d", buildTime: null })).toBe("a1b2c3d");
  });

  it("says plainly that an unstamped bundle is not built in, never a blank build", () => {
    // The quiet failure: an empty string here renders an empty value cell that reads as a real build.
    expect(formatBundle(null)).toBe("(not built into this Gateway)");
    expect(formatBundle(undefined)).toBe("(not built into this Gateway)");
  });
});

describe("AboutView", () => {
  it("shows all three product versions", async () => {
    getAboutMock.mockResolvedValue(about());
    render(<AboutView />);

    await waitFor(() => expect(screen.getByText("0.6.15+abcdef1")).toBeTruthy());
    expect(screen.getByText("aaaaaaa (built 2026-07-26 09:38:49 UTC)")).toBeTruthy();
    expect(screen.getByText("bbbbbbb (built 2026-07-26 09:38:49 UTC)")).toBeTruthy();
    expect(screen.getByText("Self-hosted")).toBeTruthy();
  });

  it("reports an unstamped bundle instead of an empty value", async () => {
    getAboutMock.mockResolvedValue(about({ cockpit: null, mobile: null }));
    render(<AboutView />);

    await waitFor(() => expect(screen.getAllByText("(not built into this Gateway)").length).toBe(2));
  });

  it("shows the listen port when the Gateway gives one", async () => {
    getAboutMock.mockResolvedValue(about({ port: 7878 }));
    render(<AboutView />);

    await waitFor(() => expect(screen.getByText("Listening on port")).toBeTruthy());
    expect(screen.getByText("7878")).toBeTruthy();
  });

  it("renders NO port row when the Gateway omits it (the hosted service)", async () => {
    getAboutMock.mockResolvedValue(about({ port: null, deployment: "Hosted service" }));
    render(<AboutView />);

    await waitFor(() => expect(screen.getByText("Hosted service")).toBeTruthy());
    expect(screen.queryByText("Listening on port")).toBeNull();
  });

  it("never shows the install root, machine name, run mode or installed components", async () => {
    // Those left the payload entirely (the install root spelled out the Gateway box's operating-system user
    // name). This is the page-side half of the proof; the payload half is in the Gateway tests.
    getAboutMock.mockResolvedValue(about());
    render(<AboutView />);

    await waitFor(() => expect(screen.getByText("Self-hosted")).toBeTruthy());
    expect(screen.queryByText("Install root")).toBeNull();
    expect(screen.queryByText("Machine")).toBeNull();
    expect(screen.queryByText("Mode")).toBeNull();
    expect(screen.queryByText(/INSTALLED COMPONENTS/i)).toBeNull();
  });

  it("shows an explicit error banner when the Gateway call fails", async () => {
    getAboutMock.mockRejectedValue(new Error("boom"));
    render(<AboutView />);

    await waitFor(() => expect(screen.getByText(/Could not load About info/)).toBeTruthy());
  });
});
