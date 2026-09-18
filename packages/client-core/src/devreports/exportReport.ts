// SAVING A DEV REPORT AS ONE HTML FILE, shared by the Cockpit and the phone.
//
// A dev report IS a single self-contained HTML file by contract (CONTRACT.md section 1: no scripts, styles
// inline, images as data: URLs). So the export is the bytes the Gateway already serves, saved unchanged -
// not a rendering of them, and not the app's frame around them. What opens in a browser afterwards is the
// report the agent published, on a machine with no DevThrottle and no session.
//
// What it does NOT carry, and cannot: the note-taking interface (the host injects that script at load time,
// so an exported file has no Queue buttons) and the conversation (it lives on the Gateway). An exported
// report is a report to READ. The reader who has to answer one opens the report in the app.

/** The characters a file name may carry here: letters, digits and hyphens, and nothing that needs escaping. */
function slug(title: string): string {
  const cleaned = title
    .toLowerCase()
    // Everything that is not an ASCII letter or digit becomes a separator - including the punctuation
    // Windows refuses in a file name (\ / : * ? " < > |) and the spaces that make a name awkward to pass
    // around. Accented letters go too: this is a file name, not the title.
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  // A title made entirely of characters this drops - an emoji, a Chinese title - leaves nothing. Then the
  // file is named for what it is rather than given a name made of hyphens.
  if (cleaned.length === 0) return "dev-report";
  // Long enough to stay recognisable, short enough that the version suffix cannot push a path over the
  // limits Windows keeps on file names.
  return cleaned.slice(0, 80).replace(/-+$/g, "");
}

/**
 * The name the exported file is offered under: the report's own title, the version it is, and `.html`.
 * The version is in the name because a report is republished in place - two exports of "the queue audit"
 * taken a day apart are different documents, and a reader with both should be able to tell which is which.
 */
export function reportFileName(title: string, version: number): string {
  return `${slug(title)}-v${version}.html`;
}

/**
 * Hand the browser a file to save. A Blob and a synthetic click on a download link: the only way a web app
 * can put a file on the reader's disk, and the same one every download in this app uses.
 *
 * The object URL is revoked afterwards - it is a handle into this document's memory, and a page that
 * exports several reports without revoking holds every one of them until it is closed - but NOT in the same
 * turn as the click: the browser reads the URL while it starts the save, and revoking it underneath that
 * cancels the download in some browsers. One turn later the save has the bytes and the handle is dead.
 */
export function saveHtmlFile(doc: Document, fileName: string, html: string): void {
  const blob = new Blob([html], { type: "text/html;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = doc.createElement("a");
  link.href = url;
  link.download = fileName;
  // Firefox only follows a click on a link that is in the document.
  doc.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
