// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";

// THE PHONE'S CONTROLS HOLD A TYPED PROMPT THE GATEWAY HAS NOT FINISHED (voice delivery phase 5, contract section
// 7, T6), through the REAL controls, the REAL status strip (mounted beside them, as the Terminal and Chat screens
// mount it) and the REAL typed-prompt module - the same proof the Cockpit composer carries, on the other surface.
// Only the Gateway calls and the device store are faked.

const DELIVERY_ID = "0f0e0d0c0b0a09080706050403020100";

const { sendPrompt, readPromptOutcome, disk } = vi.hoisted(() => ({
  sendPrompt: vi.fn(),
  readPromptOutcome: vi.fn(),
  disk: new Map<string, unknown>(),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  readPromptOutcome,
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
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

import { SessionControls } from "./SessionControls";
import { DictationStatusStrip } from "@devthrottle/client-core/dictation/DictationStatusStrip";
import { allDictationStatuses, clearDictationStatus } from "@devthrottle/client-core/dictation/status";
import { dismissTypedPrompt } from "@devthrottle/client-core/dictation/typedPromptDelivery";

const SID = "sess-42";
const TEXT = "please run the tests";

const resolved = (result: Record<string, unknown>) => ({
  kind: "resolved",
  result: { terminal: true, submitted: false, movedOn: false, transcript: TEXT, ...result },
});

const onFlash = vi.fn();
const onError = vi.fn();

function Harness() {
  return (
    <>
      <DictationStatusStrip sessionId={SID} />
      <SessionControls sessionId={SID} onFlash={onFlash} onError={onError} showKeyRows />
    </>
  );
}

function textarea(): HTMLTextAreaElement {
  return screen.getByPlaceholderText(/type a message/i) as HTMLTextAreaElement;
}

function typeAndSend(): void {
  fireEvent.change(textarea(), { target: { value: TEXT } });
  fireEvent.click(screen.getByRole("button", { name: "Send" }));
}

function strip(): HTMLElement {
  return document.querySelector(".dictate-strip") as HTMLElement;
}

async function sendHeld(): Promise<void> {
  sendPrompt.mockResolvedValueOnce({ delivering: true, directorState: "no-answer", deliveryId: DELIVERY_ID });
  render(<Harness />);
  typeAndSend();
  await screen.findByText(/Still delivering/);
}

beforeEach(() => {
  sendPrompt.mockReset();
  readPromptOutcome.mockReset();
  onFlash.mockReset();
  onError.mockReset();
  disk.clear();
});

afterEach(async () => {
  for (const id of [...disk.keys()]) await dismissTypedPrompt(id);
  cleanup();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
});

describe("phone: a typed send answered 202", () => {
  it("is held in the strip as Still delivering with Check now, and no Sent flash", async () => {
    await sendHeld();

    const s = strip();
    expect(s.className).toContain("dictate-strip-delivering");
    expect(within(s).getByRole("button", { name: "Check now" })).toBeTruthy();
    expect(within(s).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(onFlash).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
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
});

describe("phone: a 200 and a 502 behave exactly as before", () => {
  it("200 flashes Sent, with no strip", async () => {
    sendPrompt.mockResolvedValueOnce({ delivering: false, deliveryId: DELIVERY_ID });
    render(<Harness />);

    typeAndSend();

    await waitFor(() => expect(onFlash).toHaveBeenCalledWith("Sent"));
    expect(document.querySelector(".dictate-strip")).toBeNull();
    expect(disk.size).toBe(0);
  });

  it("502 reports the error, with no strip and no Sent", async () => {
    sendPrompt.mockRejectedValueOnce(Object.assign(new Error("The session did not take that."), { status: 502 }));
    render(<Harness />);

    typeAndSend();

    await waitFor(() => expect(onError).toHaveBeenCalledWith("The session did not take that."));
    expect(onFlash).not.toHaveBeenCalled();
    expect(document.querySelector(".dictate-strip")).toBeNull();
    expect(disk.size).toBe(0);
  });
});
