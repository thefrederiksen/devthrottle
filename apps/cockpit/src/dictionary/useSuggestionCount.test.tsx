// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, screen, waitFor, act } from "@testing-library/react";

// The count of dictionary suggestions waiting rides on the card's dot, the Settings row and the Dictionary tab (owner,
// 8 Oct 2026). When the Dictionary tab applies or dismisses a suggestion, every count on screen reads the Gateway's
// number again at once - it never shows a number the person just cleared for the rest of a 45 second poll.

const gateway = vi.hoisted(() => ({ count: 3 }));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => gateway.count),
}));

import { useSuggestionCount, suggestionsChanged } from "./useSuggestionCount";

function Count() {
  return <div data-testid="count">{useSuggestionCount(true, "tab")}</div>;
}

beforeEach(() => {
  cleanup();
  gateway.count = 3;
});
afterEach(() => cleanup());

describe("useSuggestionCount", () => {
  it("reads the Gateway's count again the moment the suggestions change", async () => {
    render(<Count />);
    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("3"));

    gateway.count = 0;
    act(() => suggestionsChanged());

    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("0"));
  });

  it("stops listening once it is gone", async () => {
    const view = render(<Count />);
    await waitFor(() => expect(screen.getByTestId("count").textContent).toBe("3"));
    view.unmount();

    const { getSuggestionCount } = await import("@devthrottle/client-core/dictation/dictionaryClient");
    const before = vi.mocked(getSuggestionCount).mock.calls.length;
    suggestionsChanged();
    expect(vi.mocked(getSuggestionCount).mock.calls.length).toBe(before);
  });
});
