/*
 * Voice Mode - offline-first storage (issue #425, spec 3.3/8).
 *
 * A recording must never be lost on a flaky connection. The moment a recording stops,
 * the raw audio blob plus the turn's metadata are written here (IndexedDB) BEFORE any
 * upload is attempted - so "local save = safe". The upload then reads the blob back from
 * here, and a pending/failed turn survives a page reload because it lives in IndexedDB,
 * not in page memory.
 *
 * Schema (one object store "outbox", keyPath "localId"):
 *   {
 *     localId:     string  // our local turn id (also the upload Idempotency-Key)
 *     sessionId:   string
 *     status:      "pending" | "uploading" | "uploaded" | "failed"
 *     createdAt:   string  // ISO timestamp
 *     mime:        string  // recording MIME (e.g. "audio/webm")
 *     blob:        Blob    // the recorded audio
 *     uploadId:    string  // server upload id, set once registered (resume-safe reuse)
 *     turnId:      string  // server turn id, set once the upload completes
 *     error:       string  // last failure message when status === "failed"
 *   }
 *
 * Service Worker / Background Sync / auto-sync-on-reconnect / 30-min stale are OUT - that
 * is issue #426. This file is storage + read-back only.
 */
(function () {
  "use strict";

  var DB_NAME = "ccd-voice";
  var DB_VERSION = 1;
  var STORE = "outbox";

  // Open (and, on first use / version bump, create) the database. Reused promise so
  // concurrent callers share one connection. Rejects with a clear error if IndexedDB
  // is unavailable or the open fails - no silent fallback (the page MUST be able to
  // persist a recording before it is allowed to claim "saved locally").
  var _dbPromise = null;
  function openDb() {
    if (_dbPromise) return _dbPromise;
    _dbPromise = new Promise(function (resolve, reject) {
      if (typeof indexedDB === "undefined" || !indexedDB) {
        reject(new Error("IndexedDB is not available in this browser."));
        return;
      }
      var req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = function () {
        var db = req.result;
        if (!db.objectStoreNames.contains(STORE)) {
          db.createObjectStore(STORE, { keyPath: "localId" });
        }
      };
      req.onsuccess = function () { resolve(req.result); };
      req.onerror = function () {
        reject(new Error("Could not open the voice database: "
          + (req.error && req.error.message || "unknown")));
      };
    });
    return _dbPromise;
  }

  // Run one transaction and resolve with the request's result. mode is "readonly" or
  // "readwrite". The work callback receives the object store and returns the IDBRequest
  // whose result we want (or null when the result is not needed).
  function tx(mode, work) {
    return openDb().then(function (db) {
      return new Promise(function (resolve, reject) {
        var t = db.transaction(STORE, mode);
        var store = t.objectStore(STORE);
        var req = work(store);
        t.oncomplete = function () { resolve(req ? req.result : undefined); };
        t.onabort = function () {
          reject(new Error("voice db transaction aborted: "
            + (t.error && t.error.message || "unknown")));
        };
        t.onerror = function () {
          reject(new Error("voice db transaction error: "
            + (t.error && t.error.message || "unknown")));
        };
      });
    });
  }

  // Insert or replace a whole record.
  function put(record) {
    return tx("readwrite", function (store) { return store.put(record); });
  }

  // Read one record by localId (resolves undefined if absent).
  function get(localId) {
    return tx("readonly", function (store) { return store.get(localId); });
  }

  // Read every record. Returned newest-first (most recent createdAt first) so the
  // outbox renders in the same order as the turn history above it.
  function getAll() {
    return tx("readonly", function (store) { return store.getAll(); }).then(function (rows) {
      rows = rows || [];
      rows.sort(function (a, b) {
        return String(b.createdAt || "").localeCompare(String(a.createdAt || ""));
      });
      return rows;
    });
  }

  // Delete one record by localId. Called once a turn is durably uploaded - its bytes are
  // now safe on the server, so we stop holding the blob locally.
  function remove(localId) {
    return tx("readwrite", function (store) { return store.delete(localId); });
  }

  // Merge a partial update into an existing record and write it back. Resolves with the
  // updated record. Throws if the record is gone (a caller updating a deleted turn is a
  // bug, not a recoverable state - surface it rather than silently re-create).
  function update(localId, patch) {
    return get(localId).then(function (rec) {
      if (!rec) throw new Error("voice db: record not found: " + localId);
      Object.assign(rec, patch);
      return put(rec).then(function () { return rec; });
    });
  }

  window.VoiceDb = {
    put: put,
    get: get,
    getAll: getAll,
    remove: remove,
    update: update,
  };
})();
