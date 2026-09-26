// THE NOW VIEW, RENDERED FROM THE GATEWAY'S OWN ANSWER - the guard that stops the screen and the route drifting apart.
//
// WHY THIS FILE EXISTS. Every other test of this view hands it an object written in this package. That is how the Now
// screen shipped with all 1,317 client tests green and the one thing it exists for broken: the verdict identifier was
// read from inside `needs` when the route sends it at the root, so every option tap was refused; the agent's decisive
// sentence was read as `sentence` when the route sends `text`, so it rendered blank; and `elapsedOnly` was ignored,
// so a working session said "Working for 7:14 a.m.". A test that invents its own input cannot notice any of that.
//
// SO THE INPUT HERE IS NOT WRITTEN HERE. wingmanNow.gatewaySample.json is produced by the Gateway's own test
// (src/CcDirector.Gateway.UnitTests/Wingman/WingmanNowWireSampleTests.cs), which folds five real states with
// WingmanNowFold and serializes them exactly as GET /sessions/{sid}/wingman-now serializes its body. Rename a field
// on either side and one of the two tests goes red.
//
// NOT PROVEN HERE: that a running Gateway serves this over HTTP. The sample is the fold's answer written down, and
// that the route hands the fold its inputs is proven in the Gateway's own WingmanNowRouteTests.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { formatClockTime, WingmanNow } from "./WingmanNow";
import type { WingmanNow as WingmanNowDto } from "./wingmanNowRead";
import gatewaySample from "./wingmanNow.gatewaySample.json";

afterEach(() => cleanup());

/**
 * The Gateway's answers, exactly as its own test wrote them. The cast goes through unknown on purpose: the point of
 * this file is that the WIRE decides the shape, so nothing here may quietly correct it into the client's type.
 */
const SAMPLE = gatewaySample as unknown as Record<string, WingmanNowDto>;

/** A reading moment a fixed distance after the instant the Gateway sent, so an elapsed sentence is exact. */
function minutesAfter(utc: string, minutes: number): Date {
  return new Date(new Date(utc).getTime() + minutes * 60_000);
}

describe("Now, rendered from the answer the Gateway actually sends", () => {
  it("has all five sampled states, each about the session the Gateway named", () => {
    expect(Object.keys(SAMPLE).sort()).toEqual(["carryingOn", "done", "justAnswered", "needsYou", "snoozed", "working"]);
    for (const answer of Object.values(SAMPLE)) {
      expect(answer.sessionId).toBe("11111111-1111-1111-1111-111111111111");
    }
  });

  it("draws a needs-you answer with no option buttons, because the Gateway sends none", () => {
    const now = SAMPLE.needsYou;
    const { container } = render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 8)} />);

    expect(screen.getByText("What it needs from you")).toBeTruthy();
    expect(container.querySelector(".wnow-option")).toBeNull();
    // The retired fields are not on the wire at all - the fold stopped writing them.
    const wire = now as unknown as Record<string, unknown>;
    expect("canAnswerByOption" in wire).toBe(false);
    expect("verdictId" in wire).toBe(false);
    expect(Object.keys(now.needs as unknown as Record<string, unknown>).sort()).toEqual(["heading", "question"]);
  });

  it("shows the agent's own decisive sentence, which the Gateway sends as text", () => {
    const now = SAMPLE.needsYou;
    render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 8)} />);

    expect(screen.getByText("Claude Code said")).toBeTruthy();
    expect(screen.getByText("Either merge 3002 yourself, or allow that command and I will do it.")).toBeTruthy();
    expect(screen.getByText("What it needs from you")).toBeTruthy();
  });

  it("says how long a working session has been working, and names no clock time", () => {
    const now = SAMPLE.working;
    render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 6)} />);

    expect(now.when!.elapsedOnly).toBe(true);
    expect(screen.getByText("Working for 6 minutes")).toBeTruthy();
    expect(screen.queryByText(new RegExp(formatClockTime(now.when!.atUtc).replace(/\s/g, "\\s")))).toBeNull();
  });

  it("uses the Gateway's own heading and lead-in on what the session was last asked", () => {
    const now = SAMPLE.working;
    const asked = now.lastAsked!;
    render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 6)} />);

    expect(screen.getByText(asked.heading)).toBeTruthy();
    expect(screen.getByText(asked.text)).toBeTruthy();
    // "at", in the Gateway's own lower case. The view used to write "At " itself.
    expect(screen.getByText(`at ${formatClockTime(asked.atUtc)}`)).toBeTruthy();
  });

  it("reads his answer back in the Gateway's finished words, and points at the next session in its own words", () => {
    const now = SAMPLE.justAnswered;
    const answered = now.answered!;
    const next = now.nextNeedsYou!;
    const onGoToSession = vi.fn();
    render(
      <WingmanNow now={now} at={minutesAfter(answered.atUtc, 1)} actions={{ onGoToSession }} />,
    );

    expect(screen.getByText(answered.headline)).toBeTruthy();
    expect(
      screen.getByText(`${answered.sentLead} ${formatClockTime(answered.atUtc)} - ${answered.workingAgainAfterText}`),
    ).toBeTruthy();
    expect(screen.getByText(next.heading)).toBeTruthy();
    expect(screen.getByText(next.name)).toBeTruthy();

    fireEvent.click(screen.getByText(next.linkText));
    expect(onGoToSession).toHaveBeenCalledWith(next.sessionId);
  });

  it("draws the carrying-on deadline as one sentence with the local clock in the middle, and tints the card", () => {
    const now = SAMPLE.carryingOn;
    const deadline = now.carryingOnDeadline!;
    const { container } = render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 3)} />);

    expect(
      screen.getByText(`${deadline.before} ${formatClockTime(deadline.atUtc)}${deadline.after}`),
    ).toBeTruthy();
    expect(container.querySelector(".wnow-card-calm-purple")).toBeTruthy();
  });

  it("tints the finished work with the colour the Gateway named", () => {
    const now = SAMPLE.done;
    const { container } = render(<WingmanNow now={now} at={minutesAfter(now.when!.atUtc, 3)} />);

    expect(now.calmCard!.tone).toBe("cyan");
    expect(screen.getByText(now.calmCard!.heading)).toBeTruthy();
    expect(container.querySelector(".wnow-card-calm-cyan")).toBeTruthy();
  });

  it("sends every field the view reads under the name the view reads it by", () => {
    // THE NAMES, CHECKED DIRECTLY. Rendering is tolerant - a renamed field draws as nothing rather than throwing -
    // so the three that shipped broken were invisible. These are the paths the view dereferences, asserted present
    // on the Gateway's own answer, so a rename fails here as well as in whatever it silently stops drawing.
    const present = (answer: WingmanNowDto, path: string) => {
      let value: unknown = answer;
      for (const key of path.split(".")) {
        expect(value).not.toBeNull();
        value = (value as Record<string, unknown>)[key];
        expect(value).not.toBeUndefined();
      }
    };

    for (const key of ["sessionId", "state", "pillText", "voice.kind"]) {
      for (const answer of Object.values(SAMPLE)) present(answer, key);
    }
    for (const key of ["agentSaid.who", "agentSaid.text", "needs.heading"]) {
      present(SAMPLE.needsYou, key);
    }
    for (const key of ["when.lead", "when.atUtc", "when.showAgo", "when.elapsedOnly"]) {
      present(SAMPLE.working, key);
    }
    for (const key of ["lastAsked.heading", "lastAsked.text", "lastAsked.atUtc", "lastAsked.whenLead"]) {
      present(SAMPLE.working, key);
    }
    for (const key of [
      "answered.headline",
      "answered.sentLead",
      "answered.atUtc",
      "answered.workingAgainAfterText",
      "nextNeedsYou.heading",
      "nextNeedsYou.name",
      "nextNeedsYou.sessionId",
      "nextNeedsYou.linkText",
    ]) {
      present(SAMPLE.justAnswered, key);
    }
    for (const key of ["calmCard.heading", "calmCard.tone", "carryingOnDeadline.before", "carryingOnDeadline.atUtc", "carryingOnDeadline.after"]) {
      present(SAMPLE.carryingOn, key);
    }
  });
});
