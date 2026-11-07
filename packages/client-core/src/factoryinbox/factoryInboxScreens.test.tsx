import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { GatewayError } from "../api/client";
import type { FactoryInboxList, FactoryInboxMessage, FactoryInboxSummary, FactoryInboxThread } from "./factoryInboxClient";
import { oddList, oddMessage, oddSummary, oddThread } from "./factoryInboxFixture.testkit";

// THE FACTORY INBOX SCREENS (Factory Inbox mission, phase 4; mockups A, B and D) against Gateway answers whose words are
// deliberately odd: a screen that writes any state, badge, trail line or summary of its own fails here (rule 7). The
// Gateway client is stood in for; what is under test is what the screens print, send and report.

const gw = vi.hoisted(() => ({
  list: null as ((signal?: AbortSignal) => Promise<FactoryInboxList>) | null,
  thread: null as ((factory?: string, signal?: AbortSignal) => Promise<FactoryInboxThread>) | null,
  summary: null as (() => Promise<FactoryInboxSummary>) | null,
  send: null as ((factory: string, text: string, note: string) => Promise<FactoryInboxMessage>) | null,
}));
vi.mock("./factoryInboxClient", () => ({
  getFactoryInboxList: vi.fn((signal?: AbortSignal) => gw.list!(signal)),
  getFactoryInboxThread: vi.fn((factory: string, signal?: AbortSignal) => gw.thread!(factory, signal)),
  getFactoryInboxSummary: vi.fn(() => gw.summary!()),
  sendFactoryInboxMessage: vi.fn((factory: string, text: string, note: string) => gw.send!(factory, text, note)),
}));
// The saved recordings: none, unless a test stages some. The durable send itself is tested in factoryInboxVoice.test.ts.
vi.mock("./factoryInboxVoice", () => ({
  listFactoryRecordings: vi.fn(async () => []),
  saveFactoryRecording: vi.fn(async () => {}),
  deleteFactoryRecording: vi.fn(async () => {}),
  discardFactoryRecording: vi.fn(async () => {}),
  sendFactoryRecording: vi.fn(),
}));
// The Your inbox tab reads the unread count; its own tests are in yourinbox/yourInboxScreens.test.tsx.
vi.mock("../yourinbox/yourInboxClient", () => ({
  getYourInboxCount: vi.fn(async () => ({ unread: 0, label: "" })),
}));
vi.mock("../errors/reportClientError", async (importActual) => {
  const actual = await importActual<typeof import("../errors/reportClientError")>();
  // The real sentence, watched: what is shown is what is reported.
  return { ...actual, describeAndReport: vi.fn(actual.describeAndReport) };
});

import { describeAndReport } from "../errors/reportClientError";
import { getFactoryInboxList, getFactoryInboxThread, sendFactoryInboxMessage } from "./factoryInboxClient";
import { FactoryInboxListView } from "./FactoryInboxListView";
import { FactoryInboxThreadView } from "./FactoryInboxThreadView";
import { FactoryComposer } from "./FactoryComposer";
import { INBOX_TABS, InboxTabs } from "./InboxTabs";

beforeEach(() => {
  gw.list = () => Promise.resolve(oddList());
  gw.thread = () => Promise.resolve(oddThread());
  gw.summary = () => Promise.resolve(oddSummary());
  gw.send = () => Promise.resolve(oddMessage());
  vi.mocked(describeAndReport).mockClear();
  vi.mocked(sendFactoryInboxMessage).mockClear();
  vi.mocked(getFactoryInboxThread).mockClear();
  vi.mocked(getFactoryInboxList).mockClear();
});

afterEach(() => {
  cleanup();
});

describe("FactoryInboxListView (mockup A)", () => {
  it("FactoryInboxListView_Rows_PrintTheGatewaysLineWaitingLineBadgeAndToneInItsOrder", async () => {
    render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);

    const rows = await screen.findAllByTestId("fi-row");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText("Zeta Works (gateway)")).toBeTruthy();
    expect(within(rows[0]).getByText("Quokka ledger needs a gentler tone")).toBeTruthy();
    expect(within(rows[0]).getByText("Two still pondering (gateway)")).toBeTruthy();
    const badge = within(rows[0]).getByText("Mulling (gateway)");
    expect(badge.getAttribute("data-tone")).toBe("amber");
    expect(within(rows[1]).getByText("Silence so far (gateway)")).toBeTruthy();
    expect(within(rows[1]).getByText("Empty-ish (gateway)").getAttribute("data-tone")).toBe("grey");
  });

  it("FactoryInboxListView_RowWithAReason_PrintsTheGatewaysReasonVerbatim_AndARowWithoutOneNone", async () => {
    render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);

    const rows = await screen.findAllByTestId("fi-row");
    const reason = within(rows[0]).getByText("Held up because the burrow is unlit (gateway)");
    expect(reason.className).toBe("fi-row-reason");
    expect(rows[1].querySelector(".fi-row-reason")).toBeNull();
  });

  // The review of pull request 4703: the list had the thread's old loop, aborting its read every 15 seconds.
  describe("re-reading", () => {
    type Pending = { signal?: AbortSignal; resolve: (l: FactoryInboxList) => void };
    let pending: Pending[];

    beforeEach(() => {
      vi.useFakeTimers({ shouldAdvanceTime: false });
      pending = [];
      gw.list = (signal) => new Promise<FactoryInboxList>((resolve) => pending.push({ signal, resolve }));
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    const settle = async () => {
      for (let i = 0; i < 5; i++) await vi.advanceTimersByTimeAsync(0);
    };

    it("FactoryInboxListView_SlowRead_IsNeverAbortedOrJoinedByAnother_ThenReadsAgain15SecondsAfterItEnds", async () => {
      render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);
      await settle();

      await vi.advanceTimersByTimeAsync(90_000);
      expect(getFactoryInboxList).toHaveBeenCalledTimes(1);
      expect(pending[0].signal?.aborted).toBe(false);

      pending[0].resolve(oddList());
      await settle();
      expect(screen.getAllByTestId("fi-row")).toHaveLength(2);
      await vi.advanceTimersByTimeAsync(15_000);
      expect(getFactoryInboxList).toHaveBeenCalledTimes(2);
    });

    it("FactoryInboxListView_MessageLeftWhileAReadRuns_ReadsAgainWhenThatReadEnds_NotBefore", async () => {
      render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);
      await settle();
      pending[0].resolve(oddList());
      await settle();
      await vi.advanceTimersByTimeAsync(15_000);
      expect(getFactoryInboxList).toHaveBeenCalledTimes(2);

      fireEvent.change(screen.getByLabelText("Message"), { target: { value: "One more thing" } });
      fireEvent.click(screen.getByRole("button", { name: "Send" }));
      await settle();
      expect(getFactoryInboxList).toHaveBeenCalledTimes(2);
      expect(pending[1].signal?.aborted).toBe(false);

      pending[1].resolve(oddList());
      await settle();
      expect(getFactoryInboxList).toHaveBeenCalledTimes(3);
    });
  });

  it("FactoryInboxListView_RowPressed_OpensThatFactory", async () => {
    const onOpen = vi.fn();
    render(<FactoryInboxListView surface="mobile" onOpen={onOpen} />);

    fireEvent.click((await screen.findAllByTestId("fi-row"))[1]);

    expect(onOpen).toHaveBeenCalledWith("alpha-mill");
  });

  it("FactoryInboxListView_Composer_IsOnTheListWithEveryFactoryToPickFromAndTheFirstPicked", async () => {
    render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);

    const pick = (await screen.findByLabelText("Factory")) as HTMLSelectElement;
    expect([...pick.options].map((o) => o.textContent)).toEqual(["Zeta Works (gateway)", "Alpha Mill (gateway)"]);
    expect(pick.value).toBe("zeta-works");
  });

  it("FactoryInboxListView_PickAnotherFactoryAndSend_LeavesItThere", async () => {
    render(<FactoryInboxListView surface="mobile" onOpen={() => {}} />);
    fireEvent.change(await screen.findByLabelText("Factory"), { target: { value: "alpha-mill" } });
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "Please look at the mill" } });

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(sendFactoryInboxMessage).toHaveBeenCalledWith("alpha-mill", "Please look at the mill", "none"));
  });

  it("FactoryInboxListView_ReadFails_ShowsAndReportsTheSentence", async () => {
    gw.list = () => Promise.reject(new GatewayError(503, "x", { reason: "The inbox store is resting (gateway).", retryable: false }));

    render(<FactoryInboxListView surface="cockpit" onOpen={() => {}} />);

    expect((await screen.findByRole("alert")).textContent).toBe("The inbox store is resting (gateway).");
    expect(describeAndReport).toHaveBeenCalledTimes(1);
    expect(vi.mocked(describeAndReport).mock.calls[0][0]).toBe("factory-inbox");
  });
});

describe("FactoryInboxThreadView (mockup B)", () => {
  it("FactoryInboxThreadView_Messages_ReadOldestFirstWithTheGatewaysWordsAndTrail", async () => {
    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);

    const messages = await screen.findAllByTestId("fi-message");
    expect(messages).toHaveLength(2);
    expect(within(messages[0]).getByText("Quokka ledger needs a gentler tone")).toBeTruthy();
    expect(within(messages[0]).getByText("Bad-ish (gateway)")).toBeTruthy();
    expect(within(messages[0]).getByText("Keyed in (gateway)")).toBeTruthy();
    expect(within(messages[0]).getByText("Handed to a writer (gateway)").getAttribute("data-tone")).toBe("ok");
    const trail = within(messages[0]).getByTestId("fi-trail").textContent ?? "";
    expect(trail).toContain("Left from the pocket (gateway)");
    expect(trail).toContain(" - Filed by the clerk (gateway)");
    expect(trail).toContain(" - On the big desk (gateway)");
    expect(within(messages[1]).getByText("Turned down (gateway)").getAttribute("data-tone")).toBe("red");
    expect(within(messages[1]).getByTestId("fi-trail").textContent).toContain("Turned down: the contract says no (gateway)");
    expect(screen.getByRole("heading", { name: "Zeta Works (gateway)" })).toBeTruthy();
  });

  it("FactoryInboxThreadView_Summary_ShowsTheGatewaysSentence", async () => {
    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
    await screen.findAllByTestId("fi-message");

    fireEvent.click(screen.getByRole("button", { name: "Summary" }));

    expect(await screen.findByText("Two arrived; one waved through, one turned down (gateway).")).toBeTruthy();
  });

  it("FactoryInboxThreadView_HeldSpokenMessage_PrintsTheGatewaysWordsVerbatim", async () => {
    gw.thread = () =>
      Promise.resolve({
        ...oddThread(),
        held: [
          {
            uploadId: "44444444444444444444444444444444",
            line: "A recording is parked behind the quokka (gateway).",
            atLabel: "Last poked (gateway)",
            atUtc: "2026-11-02T09:07:00Z",
            tone: "amber",
          },
        ],
      });

    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);

    const held = await screen.findByTestId("fi-held-message");
    expect(within(held).getByText("A recording is parked behind the quokka (gateway).")).toBeTruthy();
    expect(held.textContent).toContain("Last poked (gateway)");
    expect(held.getAttribute("data-tone")).toBe("amber");
  });

  it("FactoryInboxThreadView_NothingHeld_ShowsNoHeldList", async () => {
    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
    await screen.findAllByTestId("fi-message");

    expect(screen.queryByTestId("fi-held")).toBeNull();
  });

  // The end-to-end proof of 6 November 2026, defect 1: the 15-second re-read aborted the read in flight and started
  // another, so a slow thread never showed and the aborted reads piled up on the Gateway.
  describe("re-reading", () => {
    type Pending = { signal?: AbortSignal; resolve: (t: FactoryInboxThread) => void };
    let pending: Pending[];

    beforeEach(() => {
      vi.useFakeTimers({ shouldAdvanceTime: false });
      pending = [];
      gw.thread = (_factory, signal) => new Promise<FactoryInboxThread>((resolve) => pending.push({ signal, resolve }));
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    const settle = async () => {
      await vi.advanceTimersByTimeAsync(0);
    };

    it("FactoryInboxThreadView_SlowRead_IsNeverAbortedOrJoinedByAnother_AndItsAnswerIsShown", async () => {
      render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
      await settle();
      expect(pending).toHaveLength(1);

      await vi.advanceTimersByTimeAsync(90_000);

      expect(getFactoryInboxThread).toHaveBeenCalledTimes(1);
      expect(pending[0].signal?.aborted).toBe(false);
      pending[0].resolve(oddThread());
      await settle();
      expect(screen.getAllByTestId("fi-message")).toHaveLength(2);
    });

    it("FactoryInboxThreadView_AfterARead_ReadsAgain15SecondsLater_AndShowsTheLatestAnswer", async () => {
      render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
      await settle();
      pending[0].resolve(oddThread());
      await settle();

      await vi.advanceTimersByTimeAsync(14_999);
      expect(getFactoryInboxThread).toHaveBeenCalledTimes(1);
      await vi.advanceTimersByTimeAsync(1);
      expect(getFactoryInboxThread).toHaveBeenCalledTimes(2);

      pending[1].resolve({ ...oddThread(), title: "Zeta Works renamed (gateway)" });
      await settle();
      expect(screen.getByRole("heading", { name: "Zeta Works renamed (gateway)" })).toBeTruthy();
    });

    it("FactoryInboxThreadView_MessageLeftWhileAReadRuns_ReadsAgainWhenThatReadEnds_NotBefore", async () => {
      gw.send = () => Promise.resolve(oddMessage());
      render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
      await settle();

      fireEvent.change(screen.getByLabelText("Message"), { target: { value: "One more thing" } });
      fireEvent.click(screen.getByRole("button", { name: "Send" }));
      await settle();
      expect(getFactoryInboxThread).toHaveBeenCalledTimes(1);
      expect(pending[0].signal?.aborted).toBe(false);

      pending[0].resolve(oddThread());
      await settle();
      expect(getFactoryInboxThread).toHaveBeenCalledTimes(2);
    });

    it("FactoryInboxThreadView_Closed_AbortsTheReadInFlight_AndReadsNoMore", async () => {
      const { unmount } = render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
      await settle();

      unmount();
      await vi.advanceTimersByTimeAsync(60_000);

      expect(pending[0].signal?.aborted).toBe(true);
      expect(getFactoryInboxThread).toHaveBeenCalledTimes(1);
    });
  });

  it("FactoryInboxThreadView_HasItsOwnComposerForThisFactory", async () => {
    render(<FactoryInboxThreadView factory="zeta-works" surface="mobile" />);
    await screen.findAllByTestId("fi-message");

    expect(screen.queryByLabelText("Factory")).toBeNull();
    expect((screen.getByLabelText("Message") as HTMLTextAreaElement).placeholder).toBe("Message Zeta Works (gateway)...");
  });
});

describe("FactoryComposer", () => {
  it("FactoryComposer_GoodThenSend_SendsTheTypedWordsWithTheNote", async () => {
    const onLeft = vi.fn();
    render(<FactoryComposer factory="zeta-works" factoryTitle="Zeta" surface="cockpit" onLeft={onLeft} />);
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "  Love the thumbnails  " } });
    fireEvent.click(screen.getByRole("button", { name: "Good" }));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(onLeft).toHaveBeenCalledTimes(1));
    expect(sendFactoryInboxMessage).toHaveBeenCalledWith("zeta-works", "  Love the thumbnails  ", "good");
    expect((screen.getByLabelText("Message") as HTMLTextAreaElement).value).toBe("");
    expect(screen.getByRole("button", { name: "Good" }).getAttribute("aria-pressed")).toBe("false");
  });

  it("FactoryComposer_GoodThenBad_KeepsOnlyBad", () => {
    render(<FactoryComposer factory="zeta-works" surface="cockpit" onLeft={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Good" }));
    fireEvent.click(screen.getByRole("button", { name: "Bad" }));

    expect(screen.getByRole("button", { name: "Good" }).getAttribute("aria-pressed")).toBe("false");
    expect(screen.getByRole("button", { name: "Bad" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("FactoryComposer_EmptyBox_CannotSend", () => {
    render(<FactoryComposer factory="zeta-works" surface="cockpit" onLeft={() => {}} />);

    expect((screen.getByRole("button", { name: "Send" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("FactoryComposer_RefusedSend_ShowsTheGatewaysWordsReportsThemAndKeepsTheText", async () => {
    gw.send = () =>
      Promise.reject(
        new GatewayError(409, "x", {
          reason: "'old-shop' is archived, so it takes no messages. Restore it from the Factories screen first.",
          code: "factory_archived",
          retryable: false,
        }),
      );
    const onLeft = vi.fn();
    render(<FactoryComposer factory="old-shop" surface="mobile" onLeft={onLeft} />);
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "Anyone there?" } });

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect((await screen.findByRole("alert")).textContent).toBe(
      "'old-shop' is archived, so it takes no messages. Restore it from the Factories screen first.",
    );
    expect(describeAndReport).toHaveBeenCalledTimes(1);
    expect(vi.mocked(describeAndReport).mock.calls[0].slice(0, 2)).toEqual(["factory-inbox", "leave the message"]);
    expect(onLeft).not.toHaveBeenCalled();
    expect((screen.getByLabelText("Message") as HTMLTextAreaElement).value).toBe("Anyone there?");
  });
});

describe("InboxTabs", () => {
  it("InboxTabs_OneEntry_HoldsTheFactoryInboxAndYourInboxTabs", () => {
    render(<InboxTabs active="factories" onSelect={() => {}} />);

    expect(INBOX_TABS.map((t) => t.id)).toEqual(["factories", "yours"]);
    expect(screen.getByRole("tab", { name: "Factory inbox" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "Your inbox" }).getAttribute("aria-selected")).toBe("false");
  });
});
