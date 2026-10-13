// The one "Remote plan needed" notice, rendered (the Pricing Change mission). Two things are checked:
// the notice shows the Gateway's words and nothing of its own about plans, and the gate both shells wrap
// their app in swaps the app for the notice while the refusal is in effect.
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { RemotePlanGate, RemotePlanNotice } from "./RemotePlanNotice";
import { clearRemotePlanRequired, currentRemotePlanRequired, reportRemotePlanRequired } from "./remotePlan";

const SENTENCE = "The phone app and the Cockpit need the Remote plan. Your Director keeps working on the Free plan.";
const PRICING = "https://devthrottle.com/pricing";

afterEach(() => {
  cleanup();
  clearRemotePlanRequired();
});

describe("RemotePlanNotice", () => {
  it("shows the Gateway's sentence as it is, one link to the address it named, and a way to try again", () => {
    const tryAgain = vi.fn();
    render(<RemotePlanNotice info={{ message: SENTENCE, subscribeUrl: PRICING }} onTryAgain={tryAgain} />);

    expect(screen.getByTestId("remote-plan-message").textContent).toBe(SENTENCE);
    const link = screen.getByTestId("remote-plan-subscribe") as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe(PRICING);
    expect(screen.getAllByRole("link")).toHaveLength(1);

    fireEvent.click(screen.getByTestId("remote-plan-try-again"));
    expect(tryAgain).toHaveBeenCalledTimes(1);
  });

  it("offers no link when the Gateway named no address, rather than inventing one", () => {
    render(<RemotePlanNotice info={{ message: SENTENCE, subscribeUrl: null }} onTryAgain={() => {}} />);

    expect(screen.getByTestId("remote-plan-message").textContent).toBe(SENTENCE);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByTestId("remote-plan-try-again")).toBeTruthy();
  });
});

describe("RemotePlanGate", () => {
  it("shows the app until the refusal arrives, then the notice INSTEAD of the app", () => {
    render(<RemotePlanGate><div data-testid="the-app">sessions</div></RemotePlanGate>);
    expect(screen.getByTestId("the-app")).toBeTruthy();
    expect(screen.queryByTestId("remote-plan-notice")).toBeNull();

    act(() => reportRemotePlanRequired({ message: SENTENCE, subscribeUrl: PRICING }));

    // The app is unmounted, so its polls stop: nothing keeps asking and nothing shows a raw error.
    expect(screen.queryByTestId("the-app")).toBeNull();
    expect(screen.getByTestId("remote-plan-message").textContent).toBe(SENTENCE);
    expect(screen.getByTestId("remote-plan-subscribe").getAttribute("href")).toBe(PRICING);
  });

  it("try again forgets the refusal and loads the app afresh", () => {
    const reload = vi.fn();
    render(<RemotePlanGate reload={reload}><div data-testid="the-app">sessions</div></RemotePlanGate>);
    act(() => reportRemotePlanRequired({ message: SENTENCE, subscribeUrl: PRICING }));

    fireEvent.click(screen.getByTestId("remote-plan-try-again"));

    expect(reload).toHaveBeenCalledTimes(1);
    expect(currentRemotePlanRequired()).toBeNull();
    expect(screen.getByTestId("the-app")).toBeTruthy();
  });
});
