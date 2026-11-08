// The account badge on the phone's roster bar (the owner, 8 November 2026): a round initials badge, not the label cut
// to "sor...". The whole label is still the button's name and its tooltip, so nothing is lost to a screen reader.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { AccountSwitcher, accountInitials } from "./AccountSwitcher";
import { addAccount, removeAllAccounts } from "./accountStore";

afterEach(() => {
  cleanup();
  removeAllAccounts();
});

describe("accountInitials", () => {
  it("accountInitials_Labels_GiveUpToTwoCapitals", () => {
    expect(accountInitials("soren.frederiksen@example.com")).toBe("SF");
    expect(accountInitials("soren@example.com")).toBe("S");
    expect(accountInitials("Account 2")).toBe("A2");
    expect(accountInitials("dev_team-lead@example.com")).toBe("DT");
    expect(accountInitials("--")).toBe("?");
  });
});

describe("AccountSwitcher", () => {
  it("AccountSwitcher_OneAccount_RendersNothing", () => {
    addAccount({ label: "soren@example.com", email: "soren@example.com", deviceKey: "k1", installId: "i1" });
    const { container } = render(<AccountSwitcher onOpen={() => {}} />);
    expect(container.innerHTML).toBe("");
  });

  it("AccountSwitcher_TwoAccounts_DrawsInitials_NamedByTheWholeLabel", () => {
    addAccount({ label: "dev@example.com", email: "dev@example.com", deviceKey: "k2", installId: "i2" });
    addAccount({ label: "soren.frederiksen@example.com", email: "soren.frederiksen@example.com", deviceKey: "k1", installId: "i1" });
    const onOpen = vi.fn();
    render(<AccountSwitcher onOpen={onOpen} />);

    const badge = screen.getByTestId("account-badge");
    expect(badge.textContent).toBe("SF");
    expect(badge.getAttribute("aria-label")).toBe("Signed in as soren.frederiksen@example.com. Switch account.");
    expect(badge.getAttribute("title")).toBe("soren.frederiksen@example.com");
    fireEvent.click(badge);
    expect(onOpen).toHaveBeenCalledTimes(1);
  });
});
