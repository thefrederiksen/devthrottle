// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";
import { useState } from "react";

// THE COCKPIT COMPOSER HOLDS A TYPED PROMPT THE GATEWAY HAS NOT FINISHED (voice delivery phase 5, contract
// section 7, T6), through the REAL composer, the REAL status strip and the REAL typed-prompt module. Only the
// Gateway calls and the device store are faked.
//
// A typed send answered 202 "still delivering" is held in the strip the recordings use, "Still delivering" with
// "Check now", and from then on only READ. Each ruling renders: delivered, not delivered (the words back with
// "Send anyway", which is ONE fresh send without the old id), and could not confirm (the words back with Dismiss
// only). A 200 and a 502 behave exactly as before.

const DELIVERY_ID = "0f0e0d0c0b0a09080706050403020100";

const { sendPrompt, readPromptOutcome, disk } = vi.hoisted(() => ({
  sendPrompt: vi.fn(),
  readPromptOutcome: vi.fn(),
  disk: new Map<string, unknown>(),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  readPromptOutcome,
  enqueuePrompt: vi.fn(async () => []),
  uploadImage: vi.fn(async () => ""),
}));

vi.mock("@devthrottle/client-core/dictation/heldPromptStore", () => ({
  saveHeldPrompt: vi.fn(async (rec: { deliveryId: string }) => void disk.set(rec.deliveryId, rec)),
  getHeldPrompt: vi.fn(async (id: string) => disk.get(id) ?? null),
  listHeldPrompts: vi.fn(async () => [...disk.values()]),
  deleteHeldPrompt: vi.fn(async (id: string) => void disk.delete(id)),
}));

vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
}));

vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, action: string, err: unknown) =>
    `Could not ${action}: ${err instanceof Error ? err.message : String(err)}`,
}));

import { SessionComposer } from "./SessionComposer";
import { allDictationStatuses, clearDictationStatus } from "@devthrottle/client-core/dictation/status";
import { dismissTypedPrompt } from "@devthrottle/client-core/dictation/typedPromptDelivery";

const SID = "sess-42";
const TEXT = "please run the tests";

const resolved = (result: Record<string, unknown>) => ({
  kind: "resolved",
  result: { terminal: true, submitted: false, movedOn: false, transcript: TEXT, ...result },
});

function Harness() {
  const [value, setValue] = useState("");
  return <SessionComposer sessionId={SID} value={value} onChange={setValue} onQueued={() => {}} />;
}

function textarea(): HTMLTextAreaElement {
  return screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement;
}

async function typeAndSend(): Promise<void> {
  fireEvent.change(textarea(), { target: { value: TEXT } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

function strip(): HTMLElement {
  return document.querySelector(".dictate-strip") as HTMLElement;
}

async function sendHeld(): Promise<void> {
  sendPrompt.mockResolvedValueOnce({ delivering: true, directorState: "no-answer", deliveryId: DELIVERY_ID });
  render(<Harness />);
  await typeAndSend();
  await screen.findByText(/Still delivering/);
}

beforeEach(() => {
  sendPrompt.mockReset();
  readPromptOutcome.mockReset();
  disk.clear();
});

afterEach(async () => {
  for (const id of [...disk.keys()]) await dismissTypedPrompt(id);
  cleanup();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
});

describe("Cockpit composer: a typed send answered 202", () => {
  it("is held in the strip as Still delivering with Check now, the box is not refilled, and nothing says Sent", async () => {
    await sendHeld();

    const s = strip();
    expect(s.className).toContain("dictate-strip-delivering");
    expect(within(s).getByRole("button", { name: "Check now" })).toBeTruthy();
    expect(within(s).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(textarea().value).toBe("");
    expect(screen.queryByText("Sent")).toBeNull();
    expect(disk.has(DELIVERY_ID)).toBe(true);
  });

  it("Check now reads the outcome for that delivery id and sends nothing", async () => {
    await sendHeld();
    readPromptOutcome.mockResolvedValue({ kind: "delivering", directorState: "no-answer" });

    fireEvent.click(within(strip()).getByRole("button", { name: "Check now" }));

    await waitFor(() => expect(readPromptOutcome).toHaveBeenCalledWith(SID, DELIVERY_ID));
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });

  it("delivered: the strip says Sent and the held copy is gone", async () => {
    await sendHeld();
    readPromptOutcome.mockResolvedValueOnce(resolved({ submitted: true }));

    fireEvent.click(within(strip()).getByRole("button", { name: "Check now" }));

    await waitFor(() => expect(strip().className).toContain("dictate-strip-done"));
    expect(disk.has(DELIVERY_ID)).toBe(false);
  });

  it("not delivered: the words come back with the label and Send anyway, which makes ONE fresh send without the old id", async () => {
    await sendHeld();
    readPromptOutcome.mockResolvedValueOnce(
      resolved({ movedOn: true, movedOnReason: "not-delivered", offerSendAnyway: true }),
    );

    fireEvent.click(within(strip()).getByRole("button", { name: "Check now" }));

    await screen.findByText("This message was not delivered. Here is what you wrote - send it?");
    const s = strip();
    expect(within(s).getByText(TEXT)).toBeTruthy();
    expect(within(s).getByRole("button", { name: "Dismiss" })).toBeTruthy();

    sendPrompt.mockResolvedValueOnce({ delivering: false, deliveryId: "ffff0000ffff0000ffff0000ffff0000" });
    fireEvent.click(within(s).getByRole("button", { name: "Send anyway" }));

    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(2));
    const fresh = sendPrompt.mock.calls[1] as unknown[];
    expect(fresh.slice(0, 3)).toEqual([SID, TEXT, true]);
    expect(fresh[6]).toBeUndefined();
    expect(JSON.stringify(fresh)).not.toContain(DELIVERY_ID);
    await waitFor(() => expect(strip().className).toContain("dictate-strip-done"));
  });

  it("could not confirm: the words come back with its label and Dismiss, and no Send anyway", async () => {
    await sendHeld();
    readPromptOutcome.mockResolvedValueOnce(
      resolved({ movedOn: true, movedOnReason: "unconfirmed", offerSendAnyway: false }),
    );

    fireEvent.click(within(strip()).getByRole("button", { name: "Check now" }));

    await screen.findByText("We could not confirm this message arrived. Here is what you wrote.");
    const s = strip();
    expect(within(s).getByText(TEXT)).toBeTruthy();
    expect(within(s).queryByRole("button", { name: "Send anyway" })).toBeNull();
    fireEvent.click(within(s).getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(document.querySelector(".dictate-strip")).toBeNull());
    expect(disk.has(DELIVERY_ID)).toBe(false);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });
  // QA finding F4, phase 5: a session that really ended is resolved - the words come back with the
  // ended-session label and Dismiss only. The Gateway offers no "Send anyway" (there is no session left to
  // send anything to), so the strip must not show one.
  it("the session ended: the words, the ended-session label and Dismiss, and no Send anyway", async () => {
    await sendHeld();
    readPromptOutcome.mockResolvedValueOnce(
      resolved({ movedOn: true, movedOnReason: "session-exited", offerSendAnyway: false }),
    );

    fireEvent.click(within(strip()).getByRole("button", { name: "Check now" }));

    await screen.findByText("The session has ended, so this message was not sent. Here is what you wrote.");
    const s = strip();
    expect(within(s).getByText(TEXT)).toBeTruthy();
    expect(within(s).getByRole("button", { name: "Dismiss" })).toBeTruthy();
    expect(within(s).queryByRole("button", { name: "Send anyway" })).toBeNull();

    fireEvent.click(within(s).getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(document.querySelector(".dictate-strip")).toBeNull());
    expect(disk.has(DELIVERY_ID)).toBe(false);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });
});

describe("Cockpit composer: a 200 and a 502 behave exactly as before", () => {
  it("200 says Sent in the composer, with no strip", async () => {
    sendPrompt.mockResolvedValueOnce({ delivering: false, deliveryId: DELIVERY_ID });
    render(<Harness />);

    await typeAndSend();

    await screen.findByText("Sent");
    expect(document.querySelector(".dictate-strip")).toBeNull();
    expect(textarea().value).toBe("");
    expect(disk.size).toBe(0);
  });

  it("502 puts the words back in the box and shows the error, with no strip", async () => {
    sendPrompt.mockRejectedValueOnce(Object.assign(new Error("The session did not take that."), { status: 502 }));
    render(<Harness />);

    await typeAndSend();

    await screen.findByText(/The session did not take that/);
    expect(textarea().value).toBe(TEXT);
    expect(document.querySelector(".dictate-strip")).toBeNull();
    expect(disk.size).toBe(0);
  });
});
