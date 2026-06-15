/*
 * Voice Mode - Service Worker (issue #426, spec 3.4/8).
 *
 * Purpose: drain the IndexedDB upload outbox (built in #425) AUTOMATICALLY when the
 * connection returns - even if the /voice tab is backgrounded. The page itself owns the
 * upload contract (register -> chunk PUTs -> complete -> poll) because that code lives in
 * voice.js and reads the blob back from IndexedDB; the Service Worker's job is purely to
 * wake the page and tell it "drain now" at the right moments:
 *
 *   - Background Sync: when the page calls registration.sync.register("ccd-voice-drain")
 *     while offline, the browser fires the "sync" event here once connectivity returns
 *     (and retries it with backoff on its own if the drain reports it is not done yet).
 *   - On a browser WITHOUT Background Sync, voice.js falls back to an "online" event +
 *     a periodic probe and drains in-page; this worker is then just the scope controller
 *     that makes the page installable/controlled.
 *
 * The worker NEVER touches the network or IndexedDB itself. It messages the controlled
 * page(s) and waits for the page to report the drain outcome, so all auth/token logic and
 * the resumable-upload state machine stay in one place (voice.js). No fallback that hides a
 * failure: if no client is available to drain, the sync is reported NOT done so the browser
 * retries it later.
 *
 * Scope: this script is served at /voice/sw.js with "Service-Worker-Allowed: /" so it can
 * register with scope "/voice" and control the /voice page (which has no trailing slash and
 * would otherwise be above the script's own /voice/ directory scope).
 */
"use strict";

var DRAIN_TAG = "ccd-voice-drain";

// Take over immediately on install/activate so a freshly-registered worker controls the
// already-open /voice page without requiring a second navigation.
self.addEventListener("install", function (event) {
  self.skipWaiting();
});

self.addEventListener("activate", function (event) {
  event.waitUntil(self.clients.claim());
});

// Background Sync: the browser fires this once connectivity returns for a sync the page
// registered while offline. We ask a controlled client to run the drain and report whether
// the outbox is now empty. If it reports "not done" (or there is no client to ask), we
// reject so the browser keeps the sync registered and retries it later with backoff.
self.addEventListener("sync", function (event) {
  if (event.tag !== DRAIN_TAG) return;
  event.waitUntil(requestDrainFromClients());
});

// A controlled page can also poke the worker directly (e.g. on its own "online" event) to
// re-register the sync; this keeps the wake-up path working even if the original
// registration was lost.
self.addEventListener("message", function (event) {
  var data = event.data || {};
  if (data.type === "register-sync") {
    if (self.registration && self.registration.sync) {
      self.registration.sync.register(DRAIN_TAG).catch(function () {
        // Sync registration can be refused (permission/feature). The page's online-event
        // fallback still drains; nothing to hide here.
      });
    }
  }
});

// Message one controlled client and resolve only when it reports the drain finished with an
// empty outbox. Rejects (-> Background Sync retries) when no client answers or the client
// reports work remaining. A MessageChannel gives us a direct reply from the page.
function requestDrainFromClients() {
  return self.clients
    .matchAll({ type: "window", includeUncontrolled: true })
    .then(function (clients) {
      if (!clients || !clients.length) {
        // No /voice tab open to perform the drain. Report not-done so the browser retries
        // the sync later (the next time a tab is open or connectivity changes).
        return Promise.reject(new Error("ccd-voice: no client to drain"));
      }
      return askClientToDrain(clients[0]);
    });
}

function askClientToDrain(client) {
  return new Promise(function (resolve, reject) {
    var channel = new MessageChannel();
    var settled = false;
    var timer = setTimeout(function () {
      if (settled) return;
      settled = true;
      reject(new Error("ccd-voice: drain timed out"));
    }, 60 * 1000);

    channel.port1.onmessage = function (ev) {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      var done = ev.data && ev.data.done === true;
      if (done) resolve();
      else reject(new Error("ccd-voice: drain reported work remaining"));
    };

    client.postMessage({ type: "drain-outbox" }, [channel.port2]);
  });
}
