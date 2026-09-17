import { describe, expect, it } from "vitest";
import { EMBED_REPORTS_PATH_PREFIX, isEmbeddedPanePath } from "./embedRoute";

// The startup question, on its own: is this address a pane a host embeds? The consequence - the Cockpit's
// push service worker is not registered there - is a startup side effect in main.tsx, so what is testable
// here is the decision itself, and that is what these check. What is NOT proven here: that main.tsx
// actually asks it. That is read in main.tsx and seen in the live pane.

describe("isEmbeddedPanePath", () => {
  it("IsEmbeddedPanePath_ReportsPaneAddress_True", () => {
    expect(isEmbeddedPanePath("/embed/reports/9f0d1a6e-1111-2222-3333-444455556666")).toBe(true);
  });

  it("IsEmbeddedPanePath_CockpitRoute_False", () => {
    expect(isEmbeddedPanePath("/sessions")).toBe(false);
    expect(isEmbeddedPanePath("/session/9f0d1a6e-1111-2222-3333-444455556666")).toBe(false);
    expect(isEmbeddedPanePath("/")).toBe(false);
  });

  it("IsEmbeddedPanePath_LooksLikeThePaneButIsNot_False", () => {
    expect(isEmbeddedPanePath("/embed/report/abc")).toBe(false);
    expect(isEmbeddedPanePath("/embedded/reports/abc")).toBe(false);
  });

  it("EmbedReportsPathPrefix_IsTheRoutersOwnPrefix_Matches", () => {
    // The router in main.tsx builds its path from this constant, so the address the startup tests and the
    // address the router registers cannot drift apart.
    expect(EMBED_REPORTS_PATH_PREFIX).toBe("/embed/reports/");
  });
});
