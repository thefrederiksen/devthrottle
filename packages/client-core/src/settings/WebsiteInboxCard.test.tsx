// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { WebsiteInboxCard } from "./WebsiteInboxCard";

// WEBSITE MESSAGES (brief E): the card prints the Gateway's words as they are, shows a new key once, and revokes.
describe("WebsiteInboxCard", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  const none = {
    title: "Website messages",
    explanation: "The DevThrottle website can tell you here.",
    state: "none",
    statusLine: "The website has no key, so it emails you.",
    canMint: true,
    mintLabel: "Make a key",
    mintNote: null,
    canRevoke: false,
    revokeLabel: "Revoke the key",
    key: null,
    keyLine: null,
  };
  const minted = {
    ...none,
    state: "active",
    statusLine: "The website has a key, made 8 November 2026, 21:00 UTC. It has not written anything yet.",
    mintLabel: "Make a new key",
    mintNote: "The old key stops working at once.",
    canRevoke: true,
    key: "dtwi_secret",
    keyLine: "Copy this key now; it is shown only once.",
  };
  const revoked = { ...minted, state: "revoked", statusLine: "The website's key was revoked.", canRevoke: false, key: null, keyLine: null, mintLabel: "Make a key", mintNote: null };

  function stubGateway(first: object, answers: Record<string, object | { status: number; error: string }>) {
    const calls: string[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        if (url !== "/gateway/website-inbox-key") return new Response("not stubbed", { status: 404 });
        const method = init?.method ?? "GET";
        calls.push(method);
        const answer = method === "GET" ? first : answers[method];
        if ("status" in answer && "error" in answer)
          return new Response(JSON.stringify({ error: (answer as { error: string }).error }), { status: (answer as { status: number }).status });
        return new Response(JSON.stringify(answer), { status: method === "POST" ? 201 : 200 });
      }),
    );
    return calls;
  }

  it("shows the Gateway's words for an account with no key, and no revoke", async () => {
    stubGateway(none, {});
    render(<WebsiteInboxCard />);
    expect((await screen.findByTestId("website-inbox-status")).textContent).toBe("The website has no key, so it emails you.");
    expect(screen.getByTestId("website-inbox-mint").textContent).toBe("Make a key");
    expect(screen.queryByTestId("website-inbox-revoke")).toBeNull();
  });

  it("makes a key and shows it once, with the Gateway's sentence", async () => {
    const calls = stubGateway(none, { POST: minted });
    render(<WebsiteInboxCard />);
    fireEvent.click(await screen.findByTestId("website-inbox-mint"));
    await waitFor(() => expect(screen.getByTestId("website-inbox-new-key")).toBeTruthy());
    expect((screen.getByLabelText("The new website key") as HTMLInputElement).value).toBe("dtwi_secret");
    expect(screen.getByText("Copy this key now; it is shown only once.")).toBeTruthy();
    expect(calls).toEqual(["GET", "POST"]);
  });

  it("revokes, and the key is no longer shown", async () => {
    const calls = stubGateway({ ...minted, key: null, keyLine: null }, { DELETE: revoked });
    render(<WebsiteInboxCard />);
    fireEvent.click(await screen.findByTestId("website-inbox-revoke"));
    await waitFor(() => expect(screen.getByTestId("website-inbox-status").textContent).toBe("The website's key was revoked."));
    expect(screen.queryByTestId("website-inbox-new-key")).toBeNull();
    expect(calls).toEqual(["GET", "DELETE"]);
  });

  it("shows the Gateway's refusal as it is", async () => {
    stubGateway(none, { POST: { status: 403, error: "The website's key is made only from the owner's phone or Cockpit." } });
    render(<WebsiteInboxCard />);
    fireEvent.click(await screen.findByTestId("website-inbox-mint"));
    await waitFor(() => expect(screen.getByText(/made only from the owner's phone or Cockpit/)).toBeTruthy());
  });

  it("shows a screen that cannot make the key the Gateway's own sentence, with no buttons", async () => {
    stubGateway({ ...none, state: "unavailable", statusLine: "Not available here. Open Settings on your phone to make it.", canMint: false }, {});
    render(<WebsiteInboxCard />);
    expect((await screen.findByTestId("website-inbox-status")).textContent).toBe("Not available here. Open Settings on your phone to make it.");
    expect(screen.queryByTestId("website-inbox-mint")).toBeNull();
    expect(screen.queryByTestId("website-inbox-revoke")).toBeNull();
    expect(screen.queryByText(/Could not load/)).toBeNull();
  });
});
