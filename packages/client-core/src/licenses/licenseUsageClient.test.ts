import { describe, it, expect } from "vitest";
import { planQuery, usageQuery } from "./licenseUsageClient";

// The query strings the usage and plan reads send. A question is always one string - ids sorted - so two equal
// questions are one request, and nothing is sent that the person did not ask.
describe("usageQuery", () => {
  it("is empty for the account in use with no gone sessions", () => {
    expect(usageQuery(null, false)).toBe("");
  });

  it("names the account and asks for gone sessions", () => {
    expect(usageQuery("account-a@example.com", true)).toBe("?account=account-a%40example.com&closed=true");
  });
});

describe("planQuery", () => {
  it("is empty for the default question", () => {
    expect(planQuery({})).toBe("");
    expect(planQuery({ stop: [], stopGroups: [] })).toBe("");
  });

  it("sends the target and the what-if, ids sorted and comma separated", () => {
    expect(planQuery({ account: "account-a@example.com", until: "reset", stop: ["s2", "s1"], stopGroups: ["top"] }))
      .toBe("?account=account-a%40example.com&until=reset&stop=s1%2Cs2&stopGroup=top");
  });

  it("sends a time as given", () => {
    expect(planQuery({ until: "2026-10-21T08:00:00.000Z" })).toBe("?until=2026-10-21T08%3A00%3A00.000Z");
  });
});
