// THE COCKPIT'S BROWSER TAB TITLE, written in one place (the Alerts mission, section 5.5). Two things put words in it:
// the page on screen names itself ("Mine - DevThrottle Cockpit" while a saved filter is on), and the alerts that need an
// answer put their count in front ("(2) DevThrottle Cockpit"). Each used to be able to overwrite the other; here each
// sets only its own part, and the title is always the two put together.

/** The title the Cockpit's index.html carries, and what a page with nothing of its own to say is called. */
export const COCKPIT_TITLE = "DevThrottle Cockpit";

let pageTitle = COCKPIT_TITLE;
let alertCount = 0;

/** The title with the count in front, or the bare title when nothing needs an answer. */
export function composeTitle(page: string, count: number): string {
  return count > 0 ? `(${count}) ${page}` : page;
}

function write(): void {
  document.title = composeTitle(pageTitle, alertCount);
}

/** The page on screen names itself. */
export function setPageTitle(title: string): void {
  pageTitle = title;
  write();
}

/** The alerts that need an answer put their count in front of the title. */
export function setAlertCountInTitle(count: number): void {
  alertCount = Math.max(0, count);
  write();
}
