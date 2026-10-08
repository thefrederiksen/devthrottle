// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DemoModeCard } from "./DemoModeCard";

// DEMO MODE (owner, 8 Oct 2026): the card reads the account's switch from the Gateway and the toggle writes it.
describe("DemoModeCard", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  function stubGateway(initial: boolean) {
    const calls: { method: string; body: unknown }[] = [];
    let state = initial;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        if (url !== "/gateway/demo-mode") return new Response("not stubbed", { status: 404 });
        const method = init?.method ?? "GET";
        const body = init?.body ? JSON.parse(String(init.body)) : null;
        calls.push({ method, body });
        if (method === "PUT") state = (body as { enabled: boolean }).enabled;
        return new Response(JSON.stringify({ enabled: state }), { status: 200 });
      }),
    );
    return calls;
  }

  it("shows the switch off when the account is not in demo mode, with the one-line explanation", async () => {
    stubGateway(false);
    render(<DemoModeCard />);
    const toggle = (await screen.findByTestId("demo-mode-toggle")) as HTMLInputElement;
    expect(toggle.checked).toBe(false);
    expect(screen.getByText(/Blurs what your factories and sessions do, for screen shares and demos\. Applies to everyone on this account\./)).toBeTruthy();
  });

  it("writes enabled true to the Gateway when switched on, and shows it on", async () => {
    const calls = stubGateway(false);
    render(<DemoModeCard />);
    const toggle = (await screen.findByTestId("demo-mode-toggle")) as HTMLInputElement;
    fireEvent.click(toggle);
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")!.body).toEqual({ enabled: true });
    await waitFor(() => expect((screen.getByTestId("demo-mode-toggle") as HTMLInputElement).checked).toBe(true));
  });

  it("writes enabled false when switched off", async () => {
    const calls = stubGateway(true);
    render(<DemoModeCard />);
    const toggle = (await screen.findByTestId("demo-mode-toggle")) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    fireEvent.click(toggle);
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    expect(calls.find((c) => c.method === "PUT")!.body).toEqual({ enabled: false });
  });
});
