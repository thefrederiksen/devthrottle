// Guard removals for the red runs (run-proof.mjs --mutation <name>).
//
// Each one edits the app's JavaScript IN THE BROWSER ONLY: run-proof.mjs answers the bundle request with a copy in
// which every `from` is replaced by its `to`. Nothing on disk changes, and nothing here is ever built into an app.
// Every `from` must match in each bundle it is applied to, or the run fails - a removal that did not happen must
// never read as a red. A `from` is plain text, or a regular expression where the minifier's variable names differ
// between the Cockpit and phone bundles. The texts were read from the built bundles of the viewer (frameHost.ts).
//
// `expectRed` names the claims that must go red when the guard is gone. A mutation run passes its own verdict only
// when every one of them fails; see README.md.

export const MUTATIONS = {
  // CONTRACT section 4 rule 1: the frame gets allow-same-origin.
  "sandbox-same-origin": {
    urlPattern: /\/assets\/index-[^/]*\.js$/,
    replace: [['setAttribute("sandbox","allow-scripts")', 'setAttribute("sandbox","allow-scripts allow-same-origin")']],
    expectRed: ["F1", "F3"],
  },
  // Rule 3: the host's Content-Security-Policy is not written (the meta is renamed, so the head is otherwise identical).
  "no-policy": {
    urlPattern: /\/assets\/index-[^/]*\.js$/,
    replace: [['<meta http-equiv="Content-Security-Policy" content="', '<meta name="policy-removed-by-mutation" content="']],
    expectRed: ["F2", "F5"],
  },
  // Rule 4: any ready is accepted - no token, and no one-ready-per-load.
  "no-token-check": {
    urlPattern: /\/assets\/index-[^/]*\.js$/,
    replace: [[/if\(!this\.token\|\|\w+\.token!==this\.token\)return this\.refuse\(this\.token\?"wrong token":"no ready is expected"\);/g, ""]],
    expectRed: ["F7", "F8"],
  },
  // Rules 3 and 4 together: a report script runs AND its forged ready is accepted, so its send is posted.
  "no-policy-no-token-check": {
    urlPattern: /\/assets\/index-[^/]*\.js$/,
    replace: [
      ['<meta http-equiv="Content-Security-Policy" content="', '<meta name="policy-removed-by-mutation" content="'],
      [/if\(!this\.token\|\|\w+\.token!==this\.token\)return this\.refuse\(this\.token\?"wrong token":"no ready is expected"\);/g, ""],
    ],
    expectRed: ["F2", "F4a"],
  },
  // Rule 6: a load the host did not cause leaves the port open.
  "load-keeps-port": {
    urlPattern: /\/assets\/index-[^/]*\.js$/,
    replace: [[
      'this.token="",this.closePort(),this.refuse("the frame loaded something this host did not put there; the port is closed")',
      'this.refuse("mutation: a load the host did not cause was ignored")',
    ]],
    expectRed: ["F7a", "F7", "F8"],
  },
};
