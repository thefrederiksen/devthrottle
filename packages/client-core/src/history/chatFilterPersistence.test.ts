// @vitest-environment jsdom
// The saved "Show:" choices, and the upgrade that must not silently reset them.
import { describe, it, expect, beforeEach } from "vitest";
import { loadChatFilter, persistChatFilter, CHAT_FILTER_STORAGE_KEY } from "./chatView";

describe("the saved Show: filter across the my-prompts-only upgrade", () => {
  beforeEach(() => window.localStorage.clear());

  it("still reads the THREE values every existing browser has saved", () => {
    // The trap this pins: the old reader checked parts.length === 3 exactly. Adding a fourth value
    // without widening that check fails every previously saved filter and resets three real choices.
    window.localStorage.setItem(CHAT_FILTER_STORAGE_KEY, "true,false,true");

    expect(loadChatFilter()).toEqual({
      showToolCalls: true,
      showToolResults: false,
      showThinking: true,
      myPromptsOnly: false,
    });
  });

  it("reads four values too", () => {
    window.localStorage.setItem(CHAT_FILTER_STORAGE_KEY, "false,true,false,false");

    expect(loadChatFilter().showToolResults).toBe(true);
  });

  it("never comes back with my-prompts-only ON, however it was saved", () => {
    // It is a way of finding something, not a way of reading a conversation. Restoring it a day later
    // would show a conversation with every answer missing and no memory of having asked for that.
    window.localStorage.setItem(CHAT_FILTER_STORAGE_KEY, "false,false,false,true");

    expect(loadChatFilter().myPromptsOnly).toBe(false);
  });

  it("writes it off even when it is on right now", () => {
    persistChatFilter({ showToolCalls: true, showToolResults: false, showThinking: false, myPromptsOnly: true });

    expect(window.localStorage.getItem(CHAT_FILTER_STORAGE_KEY)).toBe("true,false,false,false");
    expect(loadChatFilter().myPromptsOnly).toBe(false);
  });

  it("falls back cleanly on junk", () => {
    window.localStorage.setItem(CHAT_FILTER_STORAGE_KEY, "nonsense");

    expect(loadChatFilter()).toEqual({
      showToolCalls: false,
      showToolResults: false,
      showThinking: false,
      myPromptsOnly: false,
    });
  });
});
