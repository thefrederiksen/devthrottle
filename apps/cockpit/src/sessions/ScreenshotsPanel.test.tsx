// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";

// A thumbnail whose bytes did not load shows "Image unavailable" in its place (issue #1254). That is a failure the
// user sees, so it is reported too (the Error Logging mission, issue #3675) - with the session, and the same words.

const reported = vi.hoisted(() => vi.fn((_s: string, _a: string, message: string) => message));
vi.mock("@devthrottle/client-core/errors/reportClientError", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/errors/reportClientError");
  return { ...actual, reportShownError: reported };
});
vi.mock("@devthrottle/client-core/api/client", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/api/client");
  return {
    ...actual,
    deleteScreenshot: vi.fn(),
    getScreenshots: vi.fn(async () => ({
      items: [{ fileName: "shot-1.png", path: "C:/shots/shot-1.png", timeLabel: "10:00", lastWriteUtc: "", sizeBytes: 1 }],
      total: 1,
    })),
  };
});

import { ScreenshotsPanel } from "./ScreenshotsPanel";

describe("ScreenshotsPanel", () => {
  afterEach(() => {
    cleanup();
    reported.mockClear();
  });

  it("onError_ThumbnailDidNotLoad_ShowsImageUnavailableAndReportsIt", async () => {
    render(<ScreenshotsPanel sessionId="sess-1" onInsert={vi.fn()} />);

    fireEvent.error(await screen.findByAltText("shot-1.png"));

    expect(await screen.findByText("Image unavailable")).toBeTruthy();
    expect(reported).toHaveBeenCalledWith("cockpit-screenshots", "show the screenshot", "Image unavailable", { sessionId: "sess-1" });
  });
});
