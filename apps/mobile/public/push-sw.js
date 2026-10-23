// Web Push handling for the DevThrottle mobile Progressive Web App.
//
// This file is imported into the Workbox-generated service worker via the plugin's
// workbox.importScripts option (see vite.config.ts), so it runs in the service worker global scope
// and only adds event listeners - it does not touch the precache/offline behavior Workbox owns.
//
// The Gateway pushes a { "count": N } message whenever the number of sessions that "need you" is
// above zero and has changed. We turn that into the app-icon dot two ways, because the platforms
// differ:
//   - iOS installed PWA + desktop Chrome/Edge: navigator.setAppBadge(N) sets a real badge.
//   - Android: the Badging API is not supported; the launcher draws a dot ONLY while a notification
//     is showing. So we show one silent, tagged notification, which IS the Android dot.
// The dot is cleared by the app itself when it comes to the foreground and finds nothing waiting
// (the Gateway never pushes a zero, because a push that shows no notification is penalized by
// browsers - the userVisibleOnly contract).

'use strict';

var NEEDS_YOU_TAG = 'devthrottle-needs-you';

// A secret transfer waiting for the owner's answer (the Secret Handoff mission, issue #2943). The Gateway wrote the
// title, the body (the entry and the two machines, never a value), the per-transfer tag and the page to open; this
// only shows them. It is a separate notification that BUZZES - the agent that asked is waiting - and it never touches
// the "needs you" dot. Tapping it opens the approval card (see notificationclick).
var SECRET_TRANSFER_KIND = 'secret-transfer';

function showSecretTransfer(payload) {
  return self.registration.showNotification(String(payload.title), {
    tag: String(payload.tag),
    body: String(payload.body),
    renotify: true,
    silent: false,
    icon: '/mobile/icon-192.png',
    badge: '/mobile/icon-192.png',
    data: { url: String(payload.url), navigate: true }
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

  // App badge: iOS installed PWA + desktop. Not present on Android (harmless - the notification
  // below is what makes the dot appear there).
  if (self.navigator && 'setAppBadge' in self.navigator) {
    if (count > 0) {
      tasks.push(self.navigator.setAppBadge(count).catch(function () {}));
    } else {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
  }

  if (count <= 0) {
    tasks.push(closeNeedsYou());
    return Promise.all(tasks);
  }

  // Snooze Length mission: a returned-from-snooze ANNOUNCEMENT. The Gateway sends this exactly once when
  // a session's snooze first expires (see WebPushNeedsYouNotifier), so it is allowed to BUZZ with its own
  // copy even though a dot may already be up - it is genuinely new "go investigate why it went quiet"
  // news. Uses the same tag so it replaces the quiet dot; the next silent heartbeat folds it back into
  // the plain dot, and it never buzzes again while the snooze lingers.
  if (snoozeEnded) {
    tasks.push(
      self.registration.showNotification('DevThrottle', {
        tag: NEEDS_YOU_TAG,
        body: 'Snooze ended - still waiting on you',
        renotify: true,
        silent: false,
        icon: '/mobile/icon-192.png',
        badge: '/mobile/icon-192.png',
        data: { url: '/mobile/' }
      })
    );
    return Promise.all(tasks);
  }

  // Always show one silent, tagged notification while a session needs you. On Android the launcher
  // draws the app-icon dot ONLY while a notification is present, so this is the dot - we must show it
  // even if the app happens to be open right now, otherwise there is no dot the moment the user
  // leaves the app. The shared tag replaces (never stacks) it, and silent + renotify:false keep it a
  // quiet dot that never buzzes on updates. The app clears it when it comes to the foreground with
  // nothing waiting (see reconcileBadge in push/register.ts).
  var body = count === 1 ? '1 session needs you' : count + ' sessions need you';
  tasks.push(
    self.registration.showNotification('DevThrottle', {
      tag: NEEDS_YOU_TAG,
      body: body,
      renotify: false,
      silent: true,
      icon: '/mobile/icon-192.png',
      badge: '/mobile/icon-192.png',
      data: { url: '/mobile/' }
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
  var url = data.url || '/mobile/';
  // A notification that names a page (a secret transfer's approval card) steers an open app window to it; the
  // "needs you" dot only brings the app forward, wherever it was.
  var steer = !!data.navigate;
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
      for (var i = 0; i < clients.length; i++) {
        var c = clients[i];
        if (c.url.indexOf('/mobile') !== -1 && 'focus' in c) {
          if (steer && 'navigate' in c) {
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
    })
  );
});

// The page posts this when it is foregrounded and the live roster shows nothing waiting, so the dot
// clears even though the Gateway only ever pushes non-zero counts.
self.addEventListener('message', function (event) {
  if (event.data && event.data.type === 'devthrottle-clear-needs-you') {
    var tasks = [closeNeedsYou()];
    if (self.navigator && 'clearAppBadge' in self.navigator) {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
    event.waitUntil(Promise.all(tasks));
  }
});
