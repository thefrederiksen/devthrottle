// Wingman Voice service worker (issue #531): let the page load with no connection, but NEVER serve
// a stale version when there IS a connection. Strategy = network-first for the /m/ shell: try the
// network (and refresh the cache), fall back to the cached copy only when offline or the network
// stalls. API calls (/sessions, /wingman/*) are never touched - they pass straight through.
var CACHE = "wingman-voice-v17";
var SHELL = ["/m/index.html", "/m/m.css?v=17", "/m/m.js?v=17"];
var NET_TIMEOUT_MS = 4000;

self.addEventListener("install", function (e) {
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(SHELL); }));
  self.skipWaiting();                 // take over as soon as possible
});

self.addEventListener("activate", function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k !== CACHE; }).map(function (k) { return caches.delete(k); }));
  }));
  self.clients.claim();
});

self.addEventListener("fetch", function (e) {
  if (e.request.method !== "GET") return;
  var url = new URL(e.request.url);
  if (!url.pathname.startsWith("/m/")) return;   // only the mobile shell; API passes through

  e.respondWith(
    new Promise(function (resolve) {
      var settled = false;
      var timer = setTimeout(function () { if (!settled) { settled = true; fromCache(); } }, NET_TIMEOUT_MS);
      function fromCache() {
        caches.match(e.request).then(function (hit) {
          resolve(hit || caches.match("/m/index.html"));
        });
      }
      fetch(e.request).then(function (resp) {
        if (settled) { // network was slow; still refresh the cache for next time
          var late = resp.clone(); caches.open(CACHE).then(function (c) { c.put(e.request, late); }); return;
        }
        settled = true; clearTimeout(timer);
        var copy = resp.clone(); caches.open(CACHE).then(function (c) { c.put(e.request, copy); });
        resolve(resp);
      }).catch(function () {
        if (settled) return;
        settled = true; clearTimeout(timer); fromCache();
      });
    })
  );
});
