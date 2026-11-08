import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { ReactNode } from "react";
import { GatewayError } from "../api/client";
import type { MailDeskAnswer } from "./mailDeskClient";
import { noChannels, oddChannels, wordsOf } from "./mailDeskFixture.testkit";

// The Mail Desk page (docs/qa/mail-desk/phase-7/CONTRACT.md section 3) against a fixture whose words are deliberately
// odd: a page that writes any word of its own instead of the Gateway's fails here. The shell's frame and "Page not
// found" are stood in for, because which of them the page chooses, and what it puts inside, is under test.

const read = vi.hoisted(() => ({ next: null as (() => Promise<MailDeskAnswer>) | null }));
vi.mock("./mailDeskClient", () => ({
  getMailDeskChannels: vi.fn(() => {
    if (read.next === null) throw new Error("no Mail Desk answer staged");
    return read.next();
  }),
}));

const SHOWN = "DevThrottle could not read the Mail Desk (a test sentence).";
vi.mock("../errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("../errors/reportClientError")>()),
  describeAndReport: vi.fn(() => SHOWN),
}));

import { describeAndReport } from "../errors/reportClientError";
import { MailDeskHowItWorksPage, MailDeskPage, type MailDeskFrameProps } from "./MailDeskPage";

const HOW_HREF = "/somewhere/the-owls-explain";

function Frame({ title, children }: MailDeskFrameProps) {
  return (
    <section data-testid="frame">
      {title !== null && <h1>{title}</h1>}
      {children}
    </section>
  );
}

function Page({ notFound }: { notFound?: ReactNode }) {
  return (
    <MemoryRouter>
      <MailDeskPage
        Frame={Frame}
        notFound={notFound ?? <div data-testid="not-found">the shell's Page not found</div>}
        errorClassName="test-error-banner"
        howItWorksHref={HOW_HREF}
      />
    </MemoryRouter>
  );
}

function renderPage() {
  return render(<Page />);
}

function renderHowItWorks() {
  return render(
    <MemoryRouter>
      <MailDeskHowItWorksPage
        Frame={Frame}
        notFound={<div data-testid="not-found">the shell's Page not found</div>}
        errorClassName="test-error-banner"
      />
    </MemoryRouter>,
  );
}

function answers(answer: MailDeskAnswer) {
  read.next = () => Promise.resolve(answer);
}

beforeEach(() => {
  read.next = null;
  vi.mocked(describeAndReport).mockClear();
});

afterEach(() => {
  cleanup();
});

describe("MailDeskPage", () => {
  it("MailDeskPage_Channels_RendersEveryFieldOfEveryCardVerbatimInTheGatewaysOrder", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    renderPage();

    const cards = await screen.findAllByTestId("mail-desk-channel");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(envelope.title);
    expect(screen.getByText(envelope.summary)).toBeTruthy();
    expect(cards).toHaveLength(envelope.channels.length);
    envelope.channels.forEach((channel, index) => {
      const card = cards[index];
      // Every word in order, so a missing, reordered or rewritten field fails.
      const text = card.textContent ?? "";
      let from = 0;
      for (const word of wordsOf(channel)) {
        const at = text.indexOf(word, from);
        expect(at, `"${word}" on the card for ${channel.address}`).toBeGreaterThanOrEqual(0);
        from = at + word.length;
      }
      // Each decision count sits beside its own label, zeros included.
      const rows = within(card).getAllByRole("listitem");
      expect(rows.map((row) => row.textContent)).toEqual(channel.today.counts.map((c) => `${c.label}${c.count}`));
    });
    expect(screen.queryByText(envelope.emptyLabel)).toBeNull();
  });

  it("MailDeskPage_Channels_ShowsTheAddressWhole", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    renderPage();

    const heading = await screen.findByRole("heading", { level: 2, name: envelope.channels[0].address });
    expect(heading.textContent).toBe(envelope.channels[0].address);
  });

  it("MailDeskPage_Channels_HandsEachToneToTheStylesheetAsTheGatewaySentIt", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    renderPage();

    const cards = await screen.findAllByTestId("mail-desk-channel");
    envelope.channels.forEach((channel, index) => {
      const toned = Array.from(cards[index].querySelectorAll("[data-tone]")).map((el) => [el.textContent, el.getAttribute("data-tone")]);
      expect(toned).toEqual([
        [channel.modeLabel, channel.modeTone],
        ...(channel.checkedBy.note === null ? [] : [[channel.checkedBy.note, channel.checkedBy.noteTone]]),
        [channel.signedIn.label, channel.signedIn.tone],
        [channel.lastCheck.label, channel.lastCheck.tone],
      ]);
    });
  });

  it("MailDeskPage_GmailChannel_ShowsTheGatewaysProviderLabelAndSignInSentenceVerbatim", async () => {
    const envelope = oddChannels();
    const gmail = envelope.channels.find((c) => c.provider === "gmail")!;
    answers({ kind: "channels", envelope });

    renderPage();

    const heading = await screen.findByRole("heading", { level: 2, name: gmail.address });
    const card = heading.closest("[data-testid=mail-desk-channel]") as HTMLElement;
    expect(card.querySelector(".mail-desk-provider")?.textContent).toBe("Not Gmail Either");
    expect(within(card).getByText(gmail.signedIn.label).textContent).toBe(gmail.signedIn.label);
  });

  it("MailDeskPage_NoChannels_ShowsTheGatewaysEmptySentence", async () => {
    const envelope = noChannels();
    answers({ kind: "channels", envelope });

    renderPage();

    expect(await screen.findByText(envelope.emptyLabel)).toBeTruthy();
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(envelope.title);
    expect(screen.queryAllByTestId("mail-desk-channel")).toHaveLength(0);
  });

  it("MailDeskPage_WhileLoading_ShowsALoadingLineAtOnce", () => {
    read.next = () => new Promise<MailDeskAnswer>(() => undefined);

    renderPage();

    // Synchronously, on the first render: no wait.
    expect(screen.getByRole("status").textContent).toBe("Loading...");
    expect(screen.getByTestId("frame")).toBeTruthy();
  });

  it("MailDeskPage_FailedRead_ShowsTheShellsBannerAndReportsOnce", async () => {
    const failure = new GatewayError(500, "the store is unavailable");
    read.next = () => Promise.reject(failure);

    const { rerender } = renderPage();

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toBe(SHOWN);
    expect(banner.className).toBe("test-error-banner");
    expect(screen.getByTestId("frame").contains(banner)).toBe(true);
    // A re-render does not report the same failure again.
    rerender(<Page notFound={<div data-testid="not-found" />} />);
    expect(screen.getByRole("alert").textContent).toBe(SHOWN);
    expect(describeAndReport).toHaveBeenCalledTimes(1);
    expect(vi.mocked(describeAndReport).mock.calls[0][2]).toBe(failure);
    expect(screen.queryByTestId("not-found")).toBeNull();
  });

  it("MailDeskPage_SwitchOff_RendersTheShellsPageNotFoundAndNothingElse", async () => {
    answers({ kind: "off" });

    const { container } = renderPage();

    expect(await screen.findByTestId("not-found")).toBeTruthy();
    expect(screen.queryByTestId("frame")).toBeNull();
    expect(container.textContent).toBe("the shell's Page not found");
    expect(describeAndReport).not.toHaveBeenCalled();
  });

  it("MailDeskPage_Channels_HasNoEditingControls_OnlyTheHowLinkAndTheHelpMarks", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    const { container } = renderPage();

    await screen.findAllByTestId("mail-desk-channel");
    const controls = Array.from(container.querySelectorAll("button, input, select, textarea, a, [role=button]"));
    expect(controls.map((c) => c.textContent)).toEqual(["Why do the owls fly?", ...envelope.channels.map(() => "?")]);
  });

  it("MailDeskPage_HowItWorksLink_IsTheGatewaysWordsToTheShellsAddress", async () => {
    answers({ kind: "channels", envelope: oddChannels() });

    renderPage();

    const link = await screen.findByRole("link", { name: "Why do the owls fly?" });
    expect(link.getAttribute("href")).toBe(HOW_HREF);
  });

  it("MailDeskPage_NoChannels_StillOffersTheHowItWorksLink", async () => {
    answers({ kind: "channels", envelope: noChannels() });

    renderPage();

    expect(await screen.findByRole("link", { name: "Why do the owls fly?" })).toBeTruthy();
  });

  it("MailDeskPage_HelpMark_TapOpensTheGatewaysWordsAndLeavingClosesThem", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    renderPage();

    const cards = await screen.findAllByTestId("mail-desk-channel");
    const mark = within(cards[0]).getByRole("button", { name: "?" });
    const tip = within(cards[0]).getByRole("tooltip", { hidden: true });
    expect(tip.textContent).toBe(envelope.channels[0].checkedBy.help);
    expect(mark.getAttribute("aria-describedby")).toBe(tip.id);
    expect(tip.getAttribute("data-open")).toBeNull();
    fireEvent.click(mark);
    expect(tip.getAttribute("data-open")).toBe("true");
    expect(mark.getAttribute("aria-expanded")).toBe("true");
    fireEvent.blur(mark);
    expect(tip.getAttribute("data-open")).toBeNull();
  });

  it("MailDeskPage_HelpMark_OpenTipClosesOnEscapeAndOnATapOutside", async () => {
    answers({ kind: "channels", envelope: oddChannels() });

    renderPage();

    const cards = await screen.findAllByTestId("mail-desk-channel");
    const mark = within(cards[0]).getByRole("button", { name: "?" });
    const tip = within(cards[0]).getByRole("tooltip", { hidden: true });
    fireEvent.click(mark);
    expect(tip.getAttribute("data-open")).toBe("true");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(tip.getAttribute("data-open")).toBeNull();

    fireEvent.click(mark);
    fireEvent.pointerDown(tip);
    expect(tip.getAttribute("data-open")).toBe("true");
    fireEvent.pointerDown(cards[1]);
    expect(tip.getAttribute("data-open")).toBeNull();
  });
});

describe("MailDeskHowItWorksPage", () => {
  it("MailDeskHowItWorksPage_Channels_ShowsTheGatewaysTitleAndEveryStepInOrder", async () => {
    const envelope = oddChannels();
    answers({ kind: "channels", envelope });

    renderHowItWorks();

    const page = await screen.findByTestId("mail-desk-how-it-works");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(envelope.howItWorks.title);
    expect(within(page).getAllByRole("listitem").map((li) => li.textContent)).toEqual(envelope.howItWorks.steps);
    expect(screen.queryAllByTestId("mail-desk-channel")).toHaveLength(0);
  });

  it("MailDeskHowItWorksPage_SwitchOff_RendersTheShellsPageNotFound", async () => {
    answers({ kind: "off" });

    const { container } = renderHowItWorks();

    expect(await screen.findByTestId("not-found")).toBeTruthy();
    expect(container.textContent).toBe("the shell's Page not found");
  });
});
