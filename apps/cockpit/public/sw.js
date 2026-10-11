// Web Push service worker for the DevThrottle desktop Cockpit (issue #1257).
//
// The Cockpit reuses the SAME Gateway web-push plumbing the phone shipped with (#905): the Gateway
// pushes a { "count": N } message to every subscribed device whenever the number of sessions that
// "need you" is above zero and has changed, and pushes a single zero on the falling edge. There is NO
// new Gateway code - this worker is only the desktop-browser end of that existing pipe. The mobile app
// turns the push into an app-icon dot (public/push-sw.js); the Cockpit turns it into a real desktop
// notification, because the point on a desktop is to be CALLED by a backgrounded tab, not just badged.
//
// This is a hand-written, push-only service worker: the Cockpit is a plain same-origin single-page app
// with no offline/precache behavior, so this worker registers no fetch handler and owns nothing but the
// three push-related listeners. Registered at "/sw.js" so its scope is the whole Cockpit ("/").
//
// The subscribe/unsubscribe flow itself lives in the shared client (packages/client-core/src/push/
// register.ts); the shell-specific bits - the notification icon and the click-target URL - live here.

'use strict';

var NEEDS_YOU_TAG = 'devthrottle-needs-you';
// The DevThrottle logo the Gateway already serves for the mobile app, reused so the desktop
// notification carries the brand mark without shipping a second copy of the asset. Same-origin; if a
// build has no mobile app the browser simply shows its default icon (a notification needs no icon).
var NOTIFICATION_ICON = '/mobile/icon-192.png';

// A secret transfer waiting for the owner's answer (the Secret Handoff mission, issue #2943). The Gateway wrote the
// title and the body - the entry and the two machines, never a value - and this only shows them. It is its own
// notification, tagged per transfer, so it never replaces or clears the "needs you" one; clicking it opens the
// Cockpit's sessions screen, where the approval card waits.
var SECRET_TRANSFER_KIND = 'secret-transfer';

function showSecretTransfer(payload) {
  return self.registration.showNotification(String(payload.title), {
    tag: String(payload.tag),
    body: String(payload.body),
    renotify: true,
    requireInteraction: true,
    icon: NOTIFICATION_ICON,
    badge: NOTIFICATION_ICON,
    data: { url: '/sessions', fixedUrl: true }
  });
}

self.addEventListener('push', function (event) {
  var count = 0;
  var snoozeEnded = false;
  if (event.data) {
    try {
      var payload = event.data.json();
      if (payload && payload.kind === SECRET_TRANSFER_KIND) {
        event.waitUntil(showSecretTransfer(payload));
        return;
      }
      count = Number(payload && payload.count) || 0;
      snoozeEnded = !!(payload && payload.snoozeEnded);
    } catch (e) {
      count = 0;
      snoozeEnded = false;
    }
  }
  event.waitUntil(applyNeedsYou(count, snoozeEnded));
});

function applyNeedsYou(count, snoozeEnded) {
  var tasks = [];

  // App badge where the browser supports it (installed desktop PWA / some Chromium builds). Harmless
  // and feature-detected everywhere else - the notification below is what actually calls the user.
  if (self.navigator && 'setAppBadge' in self.navigator) {
    if (count > 0) {
      tasks.push(self.navigator.setAppBadge(count).catch(function () {}));
    } else {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
  }

  if (count <= 0) {
    // Falling edge: the Gateway pushes exactly one zero when the last waiting session is answered, so
    // close the standing notification and clear the badge.
    tasks.push(closeNeedsYou());
    return Promise.all(tasks);
  }

  // Show one tagged notification for the current count. The Gateway only pushes when the count CHANGES
  // (see WebPushNeedsYouNotifier.Decide), so renotify:true is correct here - each push is real news and
  // should re-alert - while the shared tag REPLACES the previous notification rather than stacking a new
  // one. Clicking it focuses the Cockpit and lands on the waiting session (see notificationclick).
  // Snooze Length mission: a returned-from-snooze announcement (sent once when a snooze first expires)
  // carries the distinct "Snooze ended" copy so the owner knows it is a "go investigate why it went
  // quiet" item rather than a fresh turn-end.
  var body = snoozeEnded
    ? 'Snooze ended - still waiting on you'
    : (count === 1 ? '1 session needs you' : count + ' sessions need you');
  tasks.push(
    self.registration.showNotification('DevThrottle', {
      tag: NEEDS_YOU_TAG,
      body: body,
      renotify: true,
      icon: NOTIFICATION_ICON,
      badge: NOTIFICATION_ICON,
      data: { url: '/' }
    })
  );

  return Promise.all(tasks);
}

function closeNeedsYou() {
  return self.registration.getNotifications({ tag: NEEDS_YOU_TAG }).then(function (list) {
    list.forEach(function (n) {
      n.close();
    });
  });
}

self.addEventListener('notificationclick', function (event) {
  event.notification.close();
  var data = event.notification.data || {};
  if (data.fixedUrl && data.url) {
    event.waitUntil(focusOrOpen(data.url));
    return;
  }
  event.waitUntil(
    resolveTargetUrl().then(function (url) {
      return focusOrOpen(url);
    })
  );
});

// Where the click should land. The push payload carries only the count (no session id - the Gateway
// contract is unchanged), so we read the live roster to decide: exactly one session waiting -> land on
// THAT session; several (or if the roster read fails / is ambiguous) -> land on the roster, which
// surfaces the waiting sessions at the top. The /sessions read authenticates via the cc-gateway-token
// cookie the shell sets at startup (credentials: 'include'); any failure falls back to the roster.
function resolveTargetUrl() {
  return fetch('/sessions', { credentials: 'include', headers: { Accept: 'application/json' } })
    .then(function (res) {
      return res.ok ? res.json() : null;
    })
    .then(function (body) {
      var sessions = Array.isArray(body) ? body : [];
      var needs = sessions.filter(function (s) {
        return s && s.triageBucket === 'needsYou';
      });
      if (needs.length === 1 && needs[0].sessionId) {
        return '/session/' + encodeURIComponent(needs[0].sessionId);
      }
      return '/';
    })
    .catch(function () {
      return '/';
    });
}

// Focus an existing Cockpit tab (and steer it to the target) or open a new one. A Cockpit window is any
// same-origin window that is NOT the mobile app (its path starts with /m). Navigating the existing tab
// is best-effort: if the browser will not let us navigate a controlled client we still focus it.
function focusOrOpen(url) {
  return self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
    for (var i = 0; i < clients.length; i++) {
      var c = clients[i];
      if (isCockpitWindow(c) && 'focus' in c) {
        if ('navigate' in c) {
          return c.navigate(url).then(function (nav) {
            return (nav || c).focus();
          }).catch(function () {
            return c.focus();
          });
        }
        return c.focus();
      }
    }
    if (self.clients.openWindow) {
      return self.clients.openWindow(url);
    }
    return undefined;
  });
}

function isCockpitWindow(client) {
  try {
    var path = new URL(client.url).pathname;
    // The mobile app serves at /mobile (re-based from /m); a Cockpit push must not hijack that window.
    // Both mounts are excluded: the legacy /m still 301s to /mobile, so an installed PWA can be on either
    // momentarily, and neither is a Cockpit tab.
    var underMobile = path.indexOf('/mobile/') === 0 || path === '/mobile'
                   || path.indexOf('/m/') === 0 || path === '/m';
    return !underMobile;
  } catch (e) {
    return true;
  }
}

// The page posts this when it is foregrounded and the live roster shows nothing waiting, so the
// notification clears even though the Gateway only ever pushes non-zero counts (except the one falling
// edge). Mirrors the mobile worker so reconcileBadge() clears both shells the same way.
self.addEventListener('message', function (event) {
  if (event.data && event.data.type === 'devthrottle-clear-needs-you') {
    var tasks = [closeNeedsYou()];
    if (self.navigator && 'clearAppBadge' in self.navigator) {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
    event.waitUntil(Promise.all(tasks));
  }
});
