// @vitest-environment jsdom
import { describe, it, expect, afterEach } from "vitest";
import { act, cleanup, render, screen, within } from "@testing-library/react";
import { ApiModeLiveChat } from "@devthrottle/client-core/apimode/ApiModeLiveChat";
import {
  fakeLiveNetwork, receiptItem, settle, toolCard, workingDisplay,
} from "@devthrottle/client-core/apimode/liveStream.testkit";

// THE REAL LIVE CHAT, REACHED THROUGH CLIENT-CORE'S PACKAGE EXPORT (mission #4070, phase 3; review observation F).
// The Cockpit's mount test mocks the component, and the component's own tests import it by a relative path, so
// nothing else committed resolves `@devthrottle/client-core/apimode/ApiModeLiveChat` the way SessionDetail does.
// Here the component is NOT mocked: a broken export, or a component that no longer mounts, goes red. The stream is
// the client-core fake - no network and no clock.

afterEach(cleanup);

describe("ApiModeLiveChat through the package export", () => {
  it("ApiModeLiveChat_ImportedThroughThePackageExport_MountsAndShowsTheGatewaysRuledItems", async () => {
    // Arrange
    const net = fakeLiveNetwork();
    render(<ApiModeLiveChat sessionId="s1" display={workingDisplay(2)} streamDeps={net.deps} />);
    await act(settle);
    const conn = net.connections[net.connections.length - 1];

    // Act
    await act(async () => {
      conn.items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, "List the files")]);
      conn.items("g1:2", [toolCard("c1", "t1", "bash", "{\"command\":\"ls\"}", "running", "neutral", 2)]);
      await settle();
    });

    // Assert: the status line is the verdict, and the thread holds the ruled receipt and tool card.
    const status = screen.getByRole("status", { name: "Session status" });
    expect(within(status).getByText("Working - turn 2")).toBeTruthy();
    const log = screen.getByRole("log", { name: "Live conversation" });
    expect(within(log).getByText("List the files")).toBeTruthy();
    expect(within(log).getByText("Delivered").className).toContain("live-tone-green");
    expect(within(log).getByRole("article", { name: "bash: running" })).toBeTruthy();
  });
});
