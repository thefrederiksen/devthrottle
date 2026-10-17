// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { AgentChoice } from "../api/client";
import ConnectionChoiceRow from "./ConnectionChoiceRow";

// The shared New Session "Connection" row (API mode mission #4070, phase 5), as both shells mount it.

function agent(modes: Array<[string, string]>): AgentChoice {
  return { type: "Pi", displayName: "Pi", defaultModel: "", modelLabel: "", connections: modes.map(([mode, label]) => ({ mode, label })) };
}

const BOTH = agent([["terminal", "Terminal"], ["api", "API mode"]]);

const SENTENCE = "Prompts are handed to Pi as messages, and each one is confirmed or refused with a reason.";
const DESCRIBED: AgentChoice = {
  ...BOTH,
  connections: [
    { mode: "terminal", label: "Terminal", description: null },
    { mode: "api", label: "API mode", description: SENTENCE },
  ],
};

describe("ConnectionChoiceRow", () => {
  afterEach(() => cleanup());

  it("draws nothing for an agent offered the terminal alone, so the screen is as it was", () => {
    const { container } = render(
      <ConnectionChoiceRow agent={agent([["terminal", "Terminal"]])} chosen="terminal" onChoose={() => {}} idPrefix="t" />,
    );

    expect(container.innerHTML).toBe("");
  });

  it("draws nothing before an agent is chosen", () => {
    const { container } = render(<ConnectionChoiceRow agent={null} chosen="terminal" onChoose={() => {}} idPrefix="t" />);

    expect(container.innerHTML).toBe("");
  });

  it("offers the Gateway's choices in its words, under one Connection label, with the chosen one pressed", () => {
    render(<ConnectionChoiceRow agent={BOTH} chosen="terminal" onChoose={() => {}} idPrefix="t" />);

    const group = screen.getByRole("group", { name: "Connection" });
    const buttons = Array.from(group.querySelectorAll("button"));
    expect(buttons.map((b) => b.textContent)).toEqual(["Terminal", "API mode"]);
    expect(screen.getByRole("button", { name: "Terminal" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: "API mode" }).getAttribute("aria-pressed")).toBe("false");
  });

  it("hands back the mode word of the choice clicked", () => {
    const onChoose = vi.fn();
    render(<ConnectionChoiceRow agent={BOTH} chosen="terminal" onChoose={onChoose} idPrefix="t" />);

    fireEvent.click(screen.getByRole("button", { name: "API mode" }));

    expect(onChoose).toHaveBeenCalledWith("api");
  });

  it("shows terminal as chosen when the choice made is not one this agent offers", () => {
    render(<ConnectionChoiceRow agent={BOTH} chosen="smoke-signals" onChoose={() => {}} idPrefix="t" />);

    expect(screen.getByRole("button", { name: "Terminal" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("says what API mode means, in the Gateway's words, while API mode is the choice", () => {
    render(<ConnectionChoiceRow agent={DESCRIBED} chosen="api" onChoose={() => {}} idPrefix="t" />);

    expect(screen.getByTestId("connection-choice-description").textContent).toBe(SENTENCE);
  });

  it("says nothing under the row while the terminal is the choice", () => {
    render(<ConnectionChoiceRow agent={DESCRIBED} chosen="terminal" onChoose={() => {}} idPrefix="t" />);

    expect(screen.queryByTestId("connection-choice-description")).toBeNull();
  });

  it("says nothing for a choice an older Gateway sent no sentence for", () => {
    render(<ConnectionChoiceRow agent={BOTH} chosen="api" onChoose={() => {}} idPrefix="t" />);

    expect(screen.queryByTestId("connection-choice-description")).toBeNull();
  });

  it("cannot be changed while the session is being created", () => {
    render(<ConnectionChoiceRow agent={BOTH} chosen="terminal" onChoose={() => {}} idPrefix="t" disabled />);

    expect((screen.getByRole("button", { name: "API mode" }) as HTMLButtonElement).disabled).toBe(true);
  });
});
