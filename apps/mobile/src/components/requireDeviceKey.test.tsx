// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";

// SIGNED OUT, THE PRINTED REPORT ADDRESS SURVIVES SIGN IN (dev reports mission, phase 3b).
//
// A phone with no device key that opens `/session/{sid}/reports/{rid}` used to be sent to `/signin` with the
// address thrown away, so the owner signed in and arrived at the roster. The gate now carries the requested
// route in `next=`, which is what the shared SignIn remembers and DeviceCallback lands on (issue #1088).

const hasDeviceKey = vi.fn<() => boolean>();
vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({ hasDeviceKey: () => hasDeviceKey() }));

import { RequireDeviceKey } from "./RequireDeviceKey";

const SID = "7d2f9c10-0000-4000-8000-000000000031";
const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";
const REPORT_ROUTE = `/session/${SID}/reports/${REPORT}`;

function Address() {
  const location = useLocation();
  return <span data-testid="address">{location.pathname + location.search}</span>;
}

function mountAt(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Address />
      <Routes>
        <Route path="/signin" element={<div data-testid="signin" />} />
        <Route
          path="*"
          element={
            <RequireDeviceKey>
              <div data-testid="gated" />
            </RequireDeviceKey>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  hasDeviceKey.mockReset();
});

describe("the phone's auth gate", () => {
  it("carries the requested report address into Sign in", () => {
    hasDeviceKey.mockReturnValue(false);
    mountAt(REPORT_ROUTE);

    expect(screen.getByTestId("signin")).toBeTruthy();
    expect(screen.getByTestId("address").textContent).toBe(`/signin?next=${encodeURIComponent(REPORT_ROUTE)}`);
  });

  it("shows the gated app and changes nothing once the phone is enrolled", () => {
    hasDeviceKey.mockReturnValue(true);
    mountAt(REPORT_ROUTE);

    expect(screen.getByTestId("gated")).toBeTruthy();
    expect(screen.getByTestId("address").textContent).toBe(REPORT_ROUTE);
  });
});
