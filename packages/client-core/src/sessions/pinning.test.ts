import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { buildSessionTree } from "./tree";
import { splitPinned } from "./pinning";

const pin = (rank: number, mark = "Fleet Manager") => ({
  rank,
  mark,
  title: "Your Fleet Manager. (fake)",
  othersHeading: "Not the Fleet Manager's - they ask you directly (fake)",
  handOverLinkLabel: "Hand sessions to the Fleet Manager... (fake)",
});

function s(id: string, extra: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: id, name: `session ${id}`, sortOrder: 0, ...extra } as SessionDto;
}

describe("splitPinned", () => {
  it("lifts the pinned row first and leaves every other row in the order it was given", () => {
    const roots = [s("a"), s("b"), s("fm", { pin: pin(0) }), s("c")];

    const split = splitPinned(roots);

    expect(split.pinned.map((x) => x.sessionId)).toEqual(["fm"]);
    expect(split.rest.map((x) => x.sessionId)).toEqual(["a", "b", "c"]);
    expect(split.pin?.othersHeading).toBe("Not the Fleet Manager's - they ask you directly (fake)");
  });

  it("orders several pinned rows by the Gateway's rank", () => {
    const split = splitPinned([s("second", { pin: pin(1, "Two") }), s("first", { pin: pin(0, "One") })]);

    expect(split.pinned.map((x) => x.sessionId)).toEqual(["first", "second"]);
    expect(split.pin?.mark).toBe("One");
  });

  it("pins nothing when the Gateway pinned nothing", () => {
    const split = splitPinned([s("a"), s("b")]);

    expect(split.pinned).toEqual([]);
    expect(split.rest.map((x) => x.sessionId)).toEqual(["a", "b"]);
    expect(split.pin).toBeNull();
  });

  it("keeps the pinned row's team under it: its sessions are not top-level rows", () => {
    const tree = buildSessionTree([
      s("fm", { pin: pin(0) }),
      s("w1", { controllerSessionId: "fm" }),
      s("w2", { controllerSessionId: "w1" }),
      s("plain"),
    ]);

    const split = splitPinned(tree.roots);

    expect(split.pinned.map((x) => x.sessionId)).toEqual(["fm"]);
    expect(split.rest.map((x) => x.sessionId)).toEqual(["plain"]);
    expect(tree.childrenOf.get("fm")?.map((x) => x.sessionId)).toEqual(["w1"]);
  });

  it("never pins a row the Gateway did not pin, whatever it is called", () => {
    const split = splitPinned([s("fleet-manager", { name: "Fleet Manager" })]);
    expect(split.pinned).toEqual([]);
  });
});
