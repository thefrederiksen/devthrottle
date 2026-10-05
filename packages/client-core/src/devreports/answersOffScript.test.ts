// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { DEV_REPORT_ANSWERS_OFF_SCRIPT } from "./answersOffScript";
import { frameScriptFor } from "./DevReportViewer";
import { DEV_REPORT_NOTES_SCRIPT } from "./notesScript";

// THE REPORT'S OWN ANSWER CONTROLS, WHEN THE GATEWAY SAYS ANSWERS ARE OFF (devthrottle_internal#2309, round-3 review
// R1). The proof is on the controls themselves: each is disabled, and operating one changes nothing.

const REPORT = `
  <section data-dev-report="questions">
    <h3>Keep the old signup page for a week?</h3>
    <label><input type="radio" name="old-page" value="keep"> Keep it for a week</label>
    <label><input type="radio" name="old-page" value="remove"> Remove it now</label>
    <label><input type="checkbox" name="tell"> Tell support</label>
    <textarea name="why"></textarea>
    <select name="when"><option>Today</option><option>Tomorrow</option></select>
    <button type="button">Answer</button>
  </section>`;

function runScript() {
  new Function(DEV_REPORT_ANSWERS_OFF_SCRIPT)();
}

afterEach(() => {
  document.body.innerHTML = "";
});

describe("the answers-off script", () => {
  it("DisablesEveryControlInTheReport_AndOperatingOneChangesNothing", () => {
    document.body.innerHTML = REPORT;
    runScript();

    const controls = Array.from(document.querySelectorAll<HTMLInputElement>("input, select, textarea, button"));
    expect(controls).toHaveLength(6);
    for (const control of controls) {
      expect(control.disabled).toBe(true);
      expect(control.getAttribute("aria-disabled")).toBe("true");
    }

    const keep = document.querySelector<HTMLInputElement>('input[value="keep"]')!;
    keep.click();
    document.querySelector<HTMLLabelElement>("label")!.click();
    expect(keep.checked).toBe(false);
    const tell = document.querySelector<HTMLInputElement>('input[name="tell"]')!;
    tell.click();
    expect(tell.checked).toBe(false);
  });

  it("WithoutTheScript_TheSameControlsAreLive_SoTheTestAboveCanFail", () => {
    // POSITIVE CONTROL: the same markup with nothing run - the radio answers a click.
    document.body.innerHTML = REPORT;

    const keep = document.querySelector<HTMLInputElement>('input[value="keep"]')!;
    keep.click();
    expect(keep.checked).toBe(true);
    expect(keep.disabled).toBe(false);
  });
});

describe("which script the frame gets", () => {
  it("FollowsTheGatewaysTwoFlags_AndNothingElse", () => {
    expect(frameScriptFor(true, true)).toBe(DEV_REPORT_NOTES_SCRIPT);
    expect(frameScriptFor(true, false)).toBe(DEV_REPORT_NOTES_SCRIPT);
    expect(frameScriptFor(false, false)).toBe(DEV_REPORT_ANSWERS_OFF_SCRIPT);
    expect(frameScriptFor(false, true)).toBe("");
  });
});
