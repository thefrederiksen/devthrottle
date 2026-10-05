// THE FRAME'S SCRIPT WHEN NEITHER NOTES NOR ANSWERS ARE OFFERED (devthrottle_internal#2309, round-3 review R1).
//
// A report's own markup can carry answer controls of its own - a question drawn as radio buttons, a text box, a
// select. With notes off there is no notes script to read them and nothing to send them to, so a control left live
// would let a reader pick an answer that goes nowhere and is lost without a word. This script is injected instead:
// it disables every form control in the report as the document finishes parsing, so each one is drawn greyed and
// cannot be changed. It adds nothing else and sends nothing.
//
// It runs only because the GATEWAY said answers are off (`answersOpen: false`); the viewer never decides that. When a
// later piece lets a reader answer a question addressed to them (devthrottle_internal#2307), the Gateway says so and
// this script is not injected.
//
// The report's own scripts never run (the frame's policy admits only the host's nonce), so the markup cannot add a
// control after this has run.

export const DEV_REPORT_ANSWERS_OFF_SCRIPT = `(function () {
  "use strict";
  function lock() {
    var controls = document.querySelectorAll("input, select, textarea, button");
    for (var i = 0; i < controls.length; i++) {
      controls[i].disabled = true;
      controls[i].setAttribute("aria-disabled", "true");
      controls[i].setAttribute("data-dev-report-answers-off", "");
    }
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", lock);
  } else {
    lock();
  }
})();
`;
