// THE SIX SENTENCES THE GATEWAY WORDS, AND THE VIEW MUST NOT.
//
// WHY A TEST THAT READS THE SOURCE. Four of these six arrive from the Gateway with exactly the words the view used to
// write for itself - "What it was last asked", "Next that needs you", "Go there", "Sent at". So a test that renders
// the view and reads the screen CANNOT tell the difference: hard-code the words back and the pixels are identical and
// every assertion still passes. The drift would only show up the day the Gateway's wording moves, on the owner's
// screen, which is exactly the failure this whole round was about.
//
// SO THE CHECK IS ON THE SOURCE, and it is a PRESENCE first: each of the six fields must be READ by name in
// WingmanNow.tsx. An absence check on its own would pass on an empty file, a moved file, or a typo in a phrase - it
// certifies a reading that never happened. The absence of the hard-coded phrase is the second half, and it only
// means anything because the first half proved the file was read and the field is used.
//
// Product rule 7: the Gateway words it, the screen lays it out.
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const VIEW = readFileSync(new URL("./WingmanNow.tsx", import.meta.url), "utf-8");

/** The source with its comments removed, so a phrase QUOTED in a comment is not read as a phrase the view writes. */
const CODE = VIEW.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");

/** Each field the Gateway now words, and the words it sends today - which the view must read, and must not write. */
const GATEWAY_WORDS: ReadonlyArray<[field: string, words: string]> = [
  ["now.lastAsked.heading", "What it was last asked"],
  ["now.lastAsked.whenLead", "You, at"],
  ["now.answered.headline", "What you answered"],
  ["now.answered.sentLead", "Sent at"],
  ["now.nextNeedsYou.heading", "Next that needs you"],
  ["now.nextNeedsYou.linkText", "Go there"],
];

describe("the Now view reads the Gateway's words rather than writing its own", () => {
  it("is reading the view this test is about, and not an empty or moved file", () => {
    // The instrument, checked before anything is concluded from it. Every assertion below is about this file.
    expect(VIEW.length).toBeGreaterThan(5000);
    expect(VIEW).toContain("export function WingmanNow(");
    expect(CODE).toContain("now.agentSaid.text");
  });

  it.each(GATEWAY_WORDS)("reads %s from the answer", (field) => {
    expect(CODE).toContain(field);
  });

  it.each(GATEWAY_WORDS)("does not write %s's words itself (%s)", (_field, words) => {
    expect(CODE).not.toContain(words);
  });
});
