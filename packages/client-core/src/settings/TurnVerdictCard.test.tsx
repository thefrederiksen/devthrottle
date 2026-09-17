// The Turn verdicts card (the Wingman-on-every-turn mission), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the card shows the account the state it is ACTUALLY in and
// sends the change it clicked. The two switches are not interchangeable - one spends a model call at every
// stop, the other changes what colour a session is - so a card that showed the wrong one as on, or wrote to
// the wrong route, would leave somebody believing their fleet is judged when nothing is, or believing
// nothing has changed on their screens when the colours have already moved.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { TurnVerdictCard } from "./TurnVerdictCard";
import { SettingsTabPanel } from "./SettingsTabs";

const JUDGE = "Judge what each stop means";
const COLOUR = "Show the verdicts on my sessions";

// The per-account snapshot GET /gateway/settings answers with. The two fields under test are OMITTED when
// undefined - that is the older-Gateway shape, which has to read as OFF, because a Gateway that does not
// know the field is not judging anything.
function snapshot(judge?: boolean, colour?: boolean) {
  const body: Record<string, unknown> = {
    snoozeDefaultMinutes: 60,
    snoozePresets: [15, 60, 240, 480],
    snoozeMaxPresets: 5,
    timeZone: "America/Toronto",
    timeZoneMachineDefault: "America/Toronto",
    dailyReportCadence: "daily",
    mentorReportEnabled: true,
  };
  if (judge !== undefined) body.turnVerdictJudgeEnabled = judge;
  if (colour !== undefined) body.turnVerdictColourEnabled = colour;
  return body;
}

// Every request the card makes, recorded, so a test can assert what actually went over the wire.
let calls: { url: string; method: string; body: unknown }[] = [];

function fakeGateway(judge?: boolean, colour?: boolean) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const body = init?.body === undefined ? undefined : JSON.parse(String(init.body));
      calls.push({ url, method, body });
      if (url === "/gateway/settings") {
        return new Response(JSON.stringify(snapshot(judge, colour)), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url === "/gateway/turn-verdict-judge" || url === "/gateway/turn-verdict-colour") {
        // The Gateway echoes what it applied; the card must render THAT, not what it sent.
        return new Response(JSON.stringify({ enabled: (body as { enabled: boolean }).enabled }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      // The Fleet Manager tab's placement card shares the tab; a refusal is enough for it to render its error.
      if (url === "/gateway/fleet-manager/placement") {
        return new Response(JSON.stringify({ error: "not under test" }), {
          status: 503,
          headers: { "Content-Type": "application/json" },
        });
      }
      // The Assistant tab's model card; answer its snapshot so a test that mounts it can.
      if (url === "/gateway/ai-provider") {
        return new Response(
          JSON.stringify({ provider: "devthrottle", carModeModel: "m", catalogAvailable: false }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        );
      }
      throw new Error(`unexpected request: ${method} ${url}`);
    }),
  );
}

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Turn verdicts card", () => {
  it("shows an account that has never chosen as judging nothing", async () => {
    fakeGateway(undefined, undefined);
    render(<TurnVerdictCard />);

    expect(((await screen.findByLabelText(JUDGE)) as HTMLInputElement).checked).toBe(false);
    expect(((await screen.findByLabelText(COLOUR)) as HTMLInputElement).checked).toBe(false);
  });

  // The direction that matters. The two cards beside this one default a MISSING field to ON, because
  // showing "stopped" for something still running is the mistake there. Here the missing field means a
  // Gateway that judges nothing, so ON would be the lie: it would report a fleet as judged when no stop is
  // being read, and the switch somebody then turned off would do nothing at all.
  it("reads a snapshot with neither field as off rather than on", async () => {
    fakeGateway(undefined, undefined);
    render(<TurnVerdictCard />);

    expect(((await screen.findByLabelText(JUDGE)) as HTMLInputElement).checked).toBe(false);
  });

  it("shows the shadow state as judging on and colours off", async () => {
    fakeGateway(true, false);
    render(<TurnVerdictCard />);

    expect(((await screen.findByLabelText(JUDGE)) as HTMLInputElement).checked).toBe(true);
    expect(((await screen.findByLabelText(COLOUR)) as HTMLInputElement).checked).toBe(false);
    expect(await screen.findByText(/nothing on your screens changes until you turn it on/i)).toBeTruthy();
  });

  it("sends the judging opt-in to its own route", async () => {
    fakeGateway(false, false);
    render(<TurnVerdictCard />);

    fireEvent.click(await screen.findByLabelText(JUDGE));

    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/turn-verdict-judge")).toBe(true));
    const write = calls.find((c) => c.url === "/gateway/turn-verdict-judge")!;
    expect(write.method).toBe("PUT");
    expect(write.body).toEqual({ enabled: true });
    // A silent success reads as a checkbox that did nothing, so the card owes a sentence.
    expect(await screen.findByText(/each stop on this account is judged/i)).toBeTruthy();
    expect(((await screen.findByLabelText(JUDGE)) as HTMLInputElement).checked).toBe(true);
  });

  it("sends the colour opt-in to the OTHER route, and does not touch judging", async () => {
    fakeGateway(true, false);
    render(<TurnVerdictCard />);

    fireEvent.click(await screen.findByLabelText(COLOUR));

    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/turn-verdict-colour")).toBe(true));
    expect(calls.find((c) => c.url === "/gateway/turn-verdict-colour")!.body).toEqual({ enabled: true });
    // The two switches are separate decisions; clicking one must never write the other.
    expect(calls.some((c) => c.url === "/gateway/turn-verdict-judge")).toBe(false);
    expect(((await screen.findByLabelText(COLOUR)) as HTMLInputElement).checked).toBe(true);
  });

  // A colour switch with nothing judging it is a control that does nothing, and a control that does nothing
  // is how somebody concludes the feature is broken.
  it("cannot turn the colours on while nothing is being judged", async () => {
    fakeGateway(false, false);
    render(<TurnVerdictCard />);

    expect(((await screen.findByLabelText(COLOUR)) as HTMLInputElement).disabled).toBe(true);
    expect(await screen.findByText(/Turn judging on first/i)).toBeTruthy();
  });

  it("reports a refused write rather than pretending the change took", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (url === "/gateway/settings") {
          return new Response(JSON.stringify(snapshot(false, false)), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        }
        return new Response(JSON.stringify({ error: "a tenant could not be resolved for this request" }), {
          status: 403,
          headers: { "Content-Type": "application/json" },
        });
      }),
    );
    render(<TurnVerdictCard />);

    fireEvent.click(await screen.findByLabelText(JUDGE));

    const failure = await screen.findByText(/could not/i);
    expect(failure.textContent).toContain("403");
    expect(((await screen.findByLabelText(JUDGE)) as HTMLInputElement).checked).toBe(false);
  });

  // The standing rule is that Settings is one page on two surfaces. The card lives in the shared tab, so
  // this asserts it through the same panel BOTH shells mount - the phone cannot end up without it.
  it("is on the Fleet Manager tab that both shells mount", async () => {
    fakeGateway(false, false);
    render(
      <MemoryRouter>
        <SettingsTabPanel tab="fleetmanager" />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: /Turn verdicts/ })).toBeTruthy();
  });
});
