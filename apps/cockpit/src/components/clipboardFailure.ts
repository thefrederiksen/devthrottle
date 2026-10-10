// A clipboard write the browser refused, worded as what it is.
//
// The browser refuses a clipboard write with a DOMException (permission) or, where the clipboard API does not exist
// at all (a page the browser does not trust), with a TypeError. describeAndReport reads a bare TypeError as "the
// request never reached the Gateway" - true of a failed fetch, false of a clipboard - so a copy failure is handed to
// it as this error instead: the browser's own name and words kept, and nothing about the Gateway.
export class ClipboardRefusedError extends Error {
  constructor(cause: unknown) {
    const why = cause instanceof Error ? `${cause.name}: ${cause.message}` : String(cause);
    super(`the browser did not allow copying to the clipboard (${why})`);
    this.name = "ClipboardRefusedError";
  }
}
