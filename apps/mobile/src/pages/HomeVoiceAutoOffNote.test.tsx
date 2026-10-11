// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { __resetVoiceModeAllForTests } from "@devthrottle/client-core/voice/useVoiceModeAll";

// Voice mode auto-off, step 5: the phone shows the Gateway's quiet line under the account voice switch. The line is
// proved in client-core; this proves the MOUNT - that the phone's switch actually carries it, word for word, and
// nothing when the Gateway sends none.

const LINE = "Voice mode switched off at 14:32 - you answered five sessions without listening";
const gateway = vi.hoisted(() => ({ note: null as string | null, posts: 0 }));

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/api/client")>()),
  getVoiceModeAllState: vi.fn(async () => ({ enabled: false, note: gateway.note })),
  setVoiceModeAllSessions: vi.fn(async (enabled: boolean) => {
    gateway.posts += 1;
    return { enabled, total: 1, changed: 1, skipped: 0, sessions: [] };
  }),
}));

vi.mock("@devthrottle/client-core/secrets/SecretTransfers", () => ({
  SecretTransfersProvider: ({ children }: { children: unknown }) => children,
  SecretTransfersPanel: () => null,
}));
vi.mock("@devthrottle/client-core/restart/RestartRequestsPanel", () => ({
  RestartRequestsPanel: () => null,
}));

import { VoiceAllControl } from "./Home";

afterEach(() => {
  cleanup();
  __resetVoiceModeAllForTests();
});

async function renderControl() {
  const view = render(
    <MemoryRouter>
      <VoiceAllControl sessions={[]} />
    </MemoryRouter>,
  );
  await act(async () => {});
  return view;
}

describe("the phone's voice switch", () => {
  it("shows the Gateway's line word for word beside the switch", async () => {
    gateway.note = LINE;
    const { container } = await renderControl();

    const notes = [...container.querySelectorAll(".voice-all .voice-all-note")].map((n) => n.textContent);
    expect(notes).toContain(LINE);
  });

  it("shows no line when the Gateway sends none", async () => {
    gateway.note = null;
    const { container } = await renderControl();

    expect(container.querySelector(".voice-all .voice-all-note")).toBeNull();
  });

  it("switching voice on posts ONCE and clears the line - one write through the shared switch", async () => {
    gateway.note = LINE;
    gateway.posts = 0;
    const { container, getByRole } = await renderControl();

    await act(async () => { fireEvent.click(getByRole("button")); });

    expect(gateway.posts).toBe(1);
    const notes = [...container.querySelectorAll(".voice-all .voice-all-note")].map((n) => n.textContent);
    expect(notes).not.toContain(LINE);
  });
});
