// Guard removals for the red runs (run-proof.mjs --mutation <name>).
//
// Each one edits the app's JavaScript IN THE BROWSER ONLY: run-proof.mjs answers the bundle request with a copy in
// which every `from` text is replaced by its `to` text. Nothing on disk changes, and nothing here is ever built
// into an app. Every `from` must be found, or the run fails - a removal that did not happen must never read as a
// red. The texts are taken from the BUILT bundle of the merged viewer (minified), so they are filled in when the
// viewer branch is merged into this one.

export const MUTATIONS = {};
