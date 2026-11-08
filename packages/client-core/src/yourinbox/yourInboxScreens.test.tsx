import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { GatewayError } from "../api/client";
import type { YourInboxCount, YourInboxLink, YourInboxList, YourInboxMessage, YourInboxReplyOutcome } from "./yourInboxClient";
import { oddFactoryMessage, oddYourCount, oddYourList, oddYourMessage } from "./yourInboxFixture.testkit";

// THE YOUR INBOX SCREENS (Factory Inbox mission, step C; mockup C and its Cockpit form) against Gateway answers whose
// words are deliberately odd: a screen that writes any sender, state, filter, count, empty sentence, button, link, reply
// destination or offer of its own fails here (rule 7). The Gateway client is stood in for; what is under test is what
// the screens print, send and report. Section 7 of the mission: list, read, archive and reply each print only Gateway
// words; the unread count on the Inbox entry is the Gateway's.

const gw = vi.hoisted(() => ({
  list: null as ((view: string) => Promise<YourInboxList>) | null,
  message: null as ((id: string) => Promise<YourInboxMessage>) | null,
  state: null as ((id: string, state: string) => Promise<YourInboxMessage>) | null,
  reply: null as ((id: string, text: string, to?: string) => Promise<YourInboxReplyOutcome>) | null,
  count: null as (() => Promise<YourInboxCount>) | null,
}));
vi.mock("./yourInboxClient", () => ({
  getYourInbox: vi.fn((view: string) => gw.list!(view)),
  getYourInboxMessage: vi.fn((id: string) => gw.message!(id)),
  setYourInboxState: vi.fn((id: string, state: string) => gw.state!(id, state)),
  replyYourInbox: vi.fn((id: string, text: string, to?: string) => gw.reply!(id, text, to)),
  getYourInboxCount: vi.fn(() => gw.count!()),
}));
vi.mock("../api/client", async (importActual) => {
  const actual = await importActual<typeof import("../api/client")>();
  return { ...actual, createSession: vi.fn(async () => ({ sessionId: "77777777-7777-4777-8777-777777777777" })) };
});
vi.mock("../errors/reportClientError", async (importActual) => {
  const actual = await importActual<typeof import("../errors/reportClientError")>();
  // The real sentence, watched: what is shown is what is reported.
  return { ...actual, describeAndReport: vi.fn(actual.describeAndReport) };
});

import { createSession } from "../api/client";
import { describeAndReport } from "../errors/reportClientError";
import { getYourInbox, getYourInboxCount, replyYourInbox, setYourInboxState } from "./yourInboxClient";
import { YourInboxListView } from "./YourInboxListView";
import { YourInboxMessageView } from "./YourInboxMessageView";
import { refreshYourInboxCount, resetYourInboxCount, useYourInboxCount } from "./useYourInboxCount";
import { INBOX_TABS, InboxTabs } from "../factoryinbox/InboxTabs";

const hrefFor = (link: YourInboxLink): string | null => `/somewhere/${link.kind}/${link.target}`;

function renderMessage(message: YourInboxMessage, href: (link: YourInboxLink) => string | null = hrefFor) {
  gw.message = () => Promise.resolve(message);
  return render(
    <MemoryRouter>
      <YourInboxMessageView id={message.id} surface="cockpit" hrefFor={href} />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  resetYourInboxCount();
  gw.list = () => Promise.resolve(oddYourList());
  gw.message = () => Promise.resolve(oddYourMessage());
  gw.state = (id, state) =>
    Promise.resolve(oddYourMessage({ id, state, stateLabel: `Now ${state} (gateway)`, unread: state === "unread", replies: null }));
  gw.reply = () => Promise.reject(new Error("no reply staged"));
  gw.count = () => Promise.resolve(oddYourCount());
  vi.mocked(describeAndReport).mockClear();
  vi.mocked(getYourInbox).mockClear();
  vi.mocked(setYourInboxState).mockClear();
  vi.mocked(replyYourInbox).mockClear();
  vi.mocked(getYourInboxCount).mockClear();
  vi.mocked(createSession).mockClear();
});

afterEach(() => {
  cleanup();
  resetYourInboxCount();
});

describe("YourInboxListView (mockup C)", () => {
  it("YourInboxListView_Rows_PrintTheGatewaysSenderSubjectBodyStateAndToneInItsOrder", async () => {
    render(<YourInboxListView onOpen={() => {}} />);

    const rows = await screen.findAllByTestId("yi-row");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText("#412 Wombat - Scribe (gateway)")).toBeTruthy();
    expect(within(rows[0]).getByText("Wombat ledger is ready (gateway)")).toBeTruthy();
    expect(within(rows[0]).getByText(/The quokka notes are filed\./)).toBeTruthy();
    expect(within(rows[0]).getByText("Fresh as dew (gateway)").getAttribute("data-tone")).toBe("red");
    expect(rows[0].getAttribute("data-unread")).toBe("true");
    expect(within(rows[1]).getByText("Zeta Works - Big Desk (gateway)")).toBeTruthy();
    expect(within(rows[1]).getByText("Already glanced (gateway)").getAttribute("data-tone")).toBe("grey");
    expect(rows[1].getAttribute("data-unread")).toBeNull();
  });

  it("YourInboxListView_AgentLine_IsPrintedVerbatim_OnlyOnTheRowThatHasOne", async () => {
    gw.list = () =>
      Promise.resolve(
        oddYourList({
          messages: [oddYourMessage({ agentLine: "Shelved by a robot: wombat 9, at teatime (gateway)" }), oddFactoryMessage()],
        }),
      );
    render(<YourInboxListView onOpen={() => {}} />);

    const rows = await screen.findAllByTestId("yi-row");
    expect(within(rows[0]).getByTestId("yi-agent-line").textContent).toBe("Shelved by a robot: wombat 9, at teatime (gateway)");
    expect(within(rows[1]).queryByTestId("yi-agent-line")).toBeNull();
  });

  it("YourInboxListView_Filters_PrintTheGatewaysLabelsAndCountsInItsOrder", async () => {
    render(<YourInboxListView onOpen={() => {}} />);

    const filters = await screen.findAllByTestId("yi-filter");
    expect(filters.map((f) => f.textContent)).toEqual(["Only fresh (gateway)1", "Everything here (gateway)2", "The shelf (gateway)7"]);
    expect(filters[1].getAttribute("aria-pressed")).toBe("true");
  });

  it("YourInboxListView_FilterPressed_ReadsThatViewFromTheGateway", async () => {
    gw.list = (view) =>
      Promise.resolve(view === "archived" ? oddYourList({ view: "archived", messages: [oddFactoryMessage({ id: "y-9" })] }) : oddYourList());
    render(<YourInboxListView onOpen={() => {}} />);

    fireEvent.click((await screen.findAllByTestId("yi-filter"))[2]);

    await waitFor(() => expect(screen.getAllByTestId("yi-row")).toHaveLength(1));
    expect(getYourInbox).toHaveBeenLastCalledWith("archived", expect.anything());
  });

  it("YourInboxListView_EmptyView_PrintsTheGatewaysSentenceVerbatim", async () => {
    gw.list = () => Promise.resolve(oddYourList({ count: 0, messages: [] }));
    render(<YourInboxListView onOpen={() => {}} />);

    expect((await screen.findByTestId("yi-empty")).textContent).toBe("Not a squeak (gateway).");
    expect(screen.queryAllByTestId("yi-row")).toHaveLength(0);
  });

  it("YourInboxListView_RowPressed_OpensThatMessage", async () => {
    const onOpen = vi.fn();
    render(<YourInboxListView onOpen={onOpen} />);

    fireEvent.click((await screen.findAllByTestId("yi-row"))[1]);

    expect(onOpen).toHaveBeenCalledWith("y-2");
  });

  it("YourInboxListView_GatewayRefuses_ShowsItsSentenceAndReportsIt", async () => {
    gw.list = () => Promise.reject(new GatewayError(403, "Only the tall owner reads this (gateway).", { reason: "Only the tall owner reads this (gateway)." }));
    render(<YourInboxListView onOpen={() => {}} />);

    expect((await screen.findByRole("alert")).textContent).toContain("Only the tall owner reads this (gateway).");
    expect(describeAndReport).toHaveBeenCalledWith("your-inbox", "read your inbox", expect.anything(), undefined);
  });
});

describe("YourInboxMessageView (reading a message)", () => {
  it("YourInboxMessageView_Read_PrintsTheGatewaysBodySenderLinksAndButtons", async () => {
    renderMessage(oddYourMessage({ unread: false, state: "read", stateLabel: "Already glanced (gateway)" }));

    expect((await screen.findByTestId("yi-body")).textContent).toBe("The quokka notes are filed.\nSecond line of the wombat body.");
    expect(screen.getByTestId("yi-from").textContent).toBe("#412 Wombat - Scribe (gateway)");
    expect(screen.getByTestId("yi-state").textContent).toBe("Already glanced (gateway)");
    const links = screen.getAllByTestId("yi-link");
    expect(links.map((l) => l.textContent)).toEqual(["Visit the scribe (gateway)", "Peek at the scroll (gateway)"]);
    expect(links[0].getAttribute("href")).toBe("/somewhere/session/11111111-1111-4111-8111-111111111111");
    expect(screen.getAllByTestId("yi-action").map((b) => b.textContent)).toEqual(["Tick it off (gateway)", "Shelve it (gateway)"]);
    expect(setYourInboxState).not.toHaveBeenCalled();
  });

  it("YourInboxMessageView_AgentActs_ArePrintedVerbatim_InTheGatewaysOrder", async () => {
    renderMessage(
      oddYourMessage({
        unread: false,
        state: "read",
        agentActions: [
          { act: "read", line: "Peeked at by a robot: wombat 9 (gateway)", atUtc: "2026-11-07T14:00:00Z" },
          { act: "archived", line: "Shelved by a robot: wombat 9 (gateway)", atUtc: "2026-11-07T14:02:00Z" },
        ],
        agentLine: "Shelved by a robot: wombat 9 (gateway)",
      }),
    );

    await screen.findByTestId("yi-body");
    expect(screen.getAllByTestId("yi-agent-line").map((l) => l.textContent)).toEqual([
      "Peeked at by a robot: wombat 9 (gateway)",
      "Shelved by a robot: wombat 9 (gateway)",
    ]);
  });

  it("YourInboxMessageView_NoAgentActs_PrintsNoAgentLines", async () => {
    renderMessage(oddYourMessage({ unread: false, state: "read" }));

    await screen.findByTestId("yi-body");
    expect(screen.queryByTestId("yi-agent-acts")).toBeNull();
  });

  it("YourInboxMessageView_AMessageTheGatewayGivesNoReplyTo_OffersNoReply_AndPrintsItsSender", async () => {
    renderMessage(
      oddYourMessage({
        unread: false,
        state: "read",
        from: { kind: "gateway", label: "Dawn tally - orphan timers (gateway)" },
        links: [],
        replyTo: null,
      }),
    );

    expect((await screen.findByTestId("yi-from")).textContent).toBe("Dawn tally - orphan timers (gateway)");
    expect(screen.queryByRole("button", { name: "Reply" })).toBeNull();
    expect(screen.getAllByTestId("yi-action").map((b) => b.textContent)).toEqual(["Tick it off (gateway)", "Shelve it (gateway)"]);
  });

  it("YourInboxMessageView_AMessageWithAReplyTarget_OffersReply", async () => {
    renderMessage(oddYourMessage({ unread: false, state: "read" }));

    expect(await screen.findByRole("button", { name: "Reply" })).toBeTruthy();
  });

  it("YourInboxMessageView_LinkTheShellHasNoAddressFor_IsNotShown", async () => {
    renderMessage(oddYourMessage({ unread: false }), (l) => (l.kind === "session" ? "/s" : null));

    await screen.findByTestId("yi-body");
    expect(screen.getAllByTestId("yi-link").map((l) => l.textContent)).toEqual(["Visit the scribe (gateway)"]);
  });

  it("YourInboxMessageView_OpeningAnUnreadMessage_MarksItReadAndShowsTheGatewaysNewState", async () => {
    renderMessage(oddYourMessage());

    await waitFor(() => expect(screen.getByTestId("yi-state").textContent).toBe("Now read (gateway)"));
    expect(setYourInboxState).toHaveBeenCalledWith("y-1", "read", expect.anything());
  });

  it("YourInboxMessageView_ArchivePressed_SendsTheStateTheGatewayNamedAndPrintsItsAnswer", async () => {
    renderMessage(oddYourMessage({ unread: false, state: "read" }));

    fireEvent.click(await screen.findByRole("button", { name: "Shelve it (gateway)" }));

    await waitFor(() => expect(screen.getByTestId("yi-state").textContent).toBe("Now archived (gateway)"));
    expect(setYourInboxState).toHaveBeenCalledWith("y-1", "archived");
  });

  it("YourInboxMessageView_ArchiveRefused_ShowsTheGatewaysSentence", async () => {
    gw.state = () => Promise.reject(new GatewayError(404, "That scroll blew away (gateway).", { reason: "That scroll blew away (gateway)." }));
    renderMessage(oddYourMessage({ unread: false, state: "read" }));

    fireEvent.click(await screen.findByRole("button", { name: "Shelve it (gateway)" }));

    expect((await screen.findByRole("alert")).textContent).toContain("That scroll blew away (gateway).");
  });

  it("YourInboxMessageView_RepliesSent_ArePrintedWithTheGatewaysWordsForWhereTheyWent", async () => {
    renderMessage(
      oddYourMessage({
        unread: false,
        replies: [{ id: "r-1", text: "Thanks, wombat.", routedTo: "session", label: "Whispered to the scribe (gateway)", sentAtUtc: "2026-11-05T09:30:00Z" }],
      }),
    );

    const replies = await screen.findByTestId("yi-replies");
    expect(within(replies).getByText("Thanks, wombat.")).toBeTruthy();
    expect(within(replies).getByText(/Whispered to the scribe \(gateway\)/)).toBeTruthy();
  });
});

describe("YourInboxReplyComposer (reply)", () => {
  async function openReply(message: YourInboxMessage) {
    renderMessage(message);
    fireEvent.click(await screen.findByRole("button", { name: "Reply" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Reply" }), { target: { value: "Move it to Sunday." } });
  }

  it("Reply_ToAFactory_PrintsWhereItGoesAndWhereItWentInTheGatewaysWords", async () => {
    gw.reply = () =>
      Promise.resolve({
        kind: "sent",
        reply: { id: "r-1", text: "Move it to Sunday.", routedTo: "factory", label: "Dropped in the Zeta pigeonhole (gateway)", sentAtUtc: "2026-11-05T09:30:00Z" },
        message: oddFactoryMessage({ replies: [] }),
      });
    await openReply(oddFactoryMessage());

    expect(screen.getByText("Drop a note in the Zeta pigeonhole (gateway)")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect((await screen.findByTestId("yi-reply-status")).textContent).toBe("Dropped in the Zeta pigeonhole (gateway)");
    expect(replyYourInbox).toHaveBeenCalledWith("y-2", "Move it to Sunday.", undefined);
  });

  it("Reply_ToARunningSession_PrintsTheGatewaysWordsForWhereItWent", async () => {
    gw.reply = () =>
      Promise.resolve({
        kind: "sent",
        reply: { id: "r-2", text: "Move it to Sunday.", routedTo: "session", label: "Slid under the scribe's door (gateway)", sentAtUtc: "2026-11-05T09:30:00Z" },
        message: oddYourMessage({ unread: false, replies: [] }),
      });
    await openReply(oddYourMessage({ unread: false }));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect((await screen.findByTestId("yi-reply-status")).textContent).toBe("Slid under the scribe's door (gateway)");
  });

  it("Reply_ToAnEndedSession_ShowsTheGatewaysSentenceAndExactlyItsTwoChoices", async () => {
    gw.reply = () =>
      Promise.resolve({
        kind: "offers",
        sentence: "The scribe has gone home (gateway).",
        offers: [
          { kind: "new-session", label: "Wake a fresh scribe (gateway)", directorId: "dir-a", repoPath: "/repos/zeta", prompt: "Carry this (gateway)", factory: null },
          { kind: "factory", label: "Leave it on the Zeta desk (gateway)", directorId: null, repoPath: null, prompt: null, factory: "zeta-works" },
        ],
      });
    await openReply(oddYourMessage({ unread: false }));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    const offers = await screen.findByTestId("yi-offers");
    expect(within(offers).getByText("The scribe has gone home (gateway).")).toBeTruthy();
    expect(within(offers).getAllByTestId("yi-offer").map((b) => b.textContent)).toEqual([
      "Wake a fresh scribe (gateway)",
      "Leave it on the Zeta desk (gateway)",
    ]);
    expect(screen.queryByTestId("yi-reply-status")).toBeNull();
  });

  it("Reply_EndedSession_TakingTheFactoryChoice_SendsTheSameReplyThere", async () => {
    let calls = 0;
    gw.reply = (_id, _text, to) => {
      calls++;
      if (to === undefined)
        return Promise.resolve({
          kind: "offers",
          sentence: "The scribe has gone home (gateway).",
          offers: [{ kind: "factory", label: "Leave it on the Zeta desk (gateway)", directorId: null, repoPath: null, prompt: null, factory: "zeta-works" }],
        });
      return Promise.resolve({
        kind: "sent",
        reply: { id: "r-3", text: "Move it to Sunday.", routedTo: "factory", label: "Left on the Zeta desk (gateway)", sentAtUtc: "2026-11-05T09:31:00Z" },
        message: oddYourMessage({ unread: false, replies: [] }),
      });
    };
    await openReply(oddYourMessage({ unread: false }));
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    fireEvent.click(await screen.findByRole("button", { name: "Leave it on the Zeta desk (gateway)" }));

    expect((await screen.findByTestId("yi-reply-status")).textContent).toBe("Left on the Zeta desk (gateway)");
    expect(calls).toBe(2);
    expect(replyYourInbox).toHaveBeenLastCalledWith("y-1", "Move it to Sunday.", "factory");
    expect(screen.queryByTestId("yi-offers")).toBeNull();
  });

  it("Reply_EndedSession_TakingTheNewSessionChoice_StartsItWhereTheGatewaySaidWithItsPrompt", async () => {
    gw.reply = () =>
      Promise.resolve({
        kind: "offers",
        sentence: "The scribe has gone home (gateway).",
        offers: [{ kind: "new-session", label: "Wake a fresh scribe (gateway)", directorId: "dir-a", repoPath: "/repos/zeta", prompt: "Carry this (gateway)", factory: null }],
      });
    await openReply(oddYourMessage({ unread: false }));
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    fireEvent.click(await screen.findByRole("button", { name: "Wake a fresh scribe (gateway)" }));

    const started = await screen.findByTestId("yi-reply-started");
    expect(createSession).toHaveBeenCalledWith("dir-a", "/repos/zeta", { prePrompt: "Carry this (gateway)" });
    expect(within(started).getByRole("link").getAttribute("href")).toBe("/somewhere/session/77777777-7777-4777-8777-777777777777");
  });

  it("Reply_Refused_ShowsTheGatewaysSentenceAndReportsIt", async () => {
    gw.reply = () => Promise.reject(new GatewayError(409, "Zeta Works is in mothballs (gateway). Nothing was sent.", { reason: "Zeta Works is in mothballs (gateway). Nothing was sent." }));
    await openReply(oddFactoryMessage());

    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Zeta Works is in mothballs (gateway). Nothing was sent.");
    expect(describeAndReport).toHaveBeenCalledWith("your-inbox", "send the reply", expect.anything());
  });
});

describe("the unread count", () => {
  function Count() {
    const c = useYourInboxCount();
    return <span data-testid="count">{c === null ? "none" : `${c.unread}|${c.label}`}</span>;
  }

  it("useYourInboxCount_IsTheGatewaysNumberAndWords_SharedByEveryCaller", async () => {
    render(
      <>
        <Count />
        <Count />
      </>,
    );

    await waitFor(() => expect(screen.getAllByTestId("count").map((c) => c.textContent)).toEqual([
      "3|three fresh ones (gateway)",
      "3|three fresh ones (gateway)",
    ]));
    expect(getYourInboxCount).toHaveBeenCalledTimes(1);
  });

  it("useYourInboxCount_ReadsAgainWhenAMessageIsMarked", async () => {
    // The mark is held until the first count is on screen, so what is proven is the read the mark causes.
    let release: () => void = () => {};
    const held = new Promise<void>((r) => (release = r));
    gw.state = async (id, state) => {
      await held;
      return oddYourMessage({ id, state, stateLabel: `Now ${state} (gateway)`, unread: false, replies: null });
    };
    render(
      <MemoryRouter>
        <Count />
        <YourInboxMessageView id="y-1" surface="mobile" hrefFor={hrefFor} />
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("3|three fresh ones (gateway)"));
    gw.count = () => Promise.resolve(oddYourCount({ unread: 2, label: "two left (gateway)" }));
    release();

    // Opening the unread message marked it read; the count is read again at once, not half a minute later.
    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("2|two left (gateway)"));
  });

  it("refreshYourInboxCount_AskedWhileAReadIsOnItsWay_ReadsAgainAfterIt", async () => {
    let release: () => void = () => {};
    const held = new Promise<void>((r) => (release = r));
    let reads = 0;
    gw.count = async () => {
      reads++;
      if (reads === 1) {
        await held;
        return oddYourCount();
      }
      return oddYourCount({ unread: 1, label: "one left (gateway)" });
    };
    render(<Count />);
    await waitFor(() => expect(reads).toBe(1));

    // A mark lands while the first read is still on its way: that read cannot have counted it.
    refreshYourInboxCount();
    release();

    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("1|one left (gateway)"));
    expect(reads).toBe(2);
  });

  it("useYourInboxCount_Disabled_ReadsNothing", async () => {
    function Off() {
      const c = useYourInboxCount(false);
      return <span data-testid="off">{c === null ? "none" : "some"}</span>;
    }
    render(<Off />);

    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(screen.getByTestId("off").textContent).toBe("none");
    expect(getYourInboxCount).not.toHaveBeenCalled();
  });
});

describe("InboxTabs", () => {
  it("InboxTabs_TwoTabs_YourInboxCarriesTheGatewaysCountWords", async () => {
    const onSelect = vi.fn();
    render(<InboxTabs active="yours" onSelect={onSelect} />);

    expect(INBOX_TABS.map((t) => t.id)).toEqual(["factories", "yours"]);
    expect((await screen.findByTestId("yi-tab-count")).textContent).toBe("three fresh ones (gateway)");
    expect(screen.getByRole("tab", { name: /Your inbox/ }).getAttribute("aria-selected")).toBe("true");
    fireEvent.click(screen.getByRole("tab", { name: "Factory inbox" }));
    expect(onSelect).toHaveBeenCalledWith("factories");
  });

  it("InboxTabs_NothingUnread_ShowsNoCount", async () => {
    gw.count = () => Promise.resolve(oddYourCount({ unread: 0, label: "" }));
    render(<InboxTabs active="factories" onSelect={() => {}} />);

    await waitFor(() => expect(getYourInboxCount).toHaveBeenCalled());
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(screen.queryByTestId("yi-tab-count")).toBeNull();
  });
});
