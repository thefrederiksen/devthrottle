/*
 * dev-report-notes.js - the note-taking script for DevThrottle dev reports.
 *
 * ONE plain JavaScript file with no dependencies and no build step. Every app that shows a dev report
 * (the Cockpit, the phone, the Director) injects this same file into the report page. The contract it
 * implements - the report markup, the question markup and the host messages - is CONTRACT.md beside it.
 *
 * What it does: the owner clicks a paragraph, a table cell, a labelled SVG part, or selects text, and
 * types a note; or answers a question with its Queue button. Notes and answers wait in a QUEUED list
 * until Send, then move to a SENT list whose status words come from the host. It talks to the host
 * only by postMessage.
 *
 * Constraints it is written to:
 *   - It runs inside <iframe sandbox="allow-scripts"> WITHOUT allow-same-origin. So: no storage APIs,
 *     no cookies. Everything that must survive a reload is handed to the host in state-changed and
 *     comes back in restore.
 *   - It makes no network calls.
 *   - ASCII only.
 *   - Opened as a plain file with no host, everything works except Send, which shows the exact
 *     message that would have been sent.
 *
 * Credit: the idea of in-page notes pinned to an element with human labels, questions answered with a
 * Queue button, and queued shown apart from sent comes from lavish-axi by Kun Chen (MIT licence,
 * https://github.com/kunchenguid/lavish-axi). The table row and column labelling follows its
 * table-cell.js rules: a label that cannot be proven is left empty rather than guessed.
 */
(function (root) {
  "use strict";

  var CHANNEL = "devthrottle.dev-report";
  var VERSION = 1;
  var MAX_STRING = 20000;
  var QUOTE_LENGTH = 240;
  var TOKEN_ATTR = "data-dev-report-token";
  var STARTED = typeof Symbol === "function" ? Symbol.for("devthrottle.dev-report-notes.started") : "__devReportNotesStarted";
  var ANCHOR_TYPES = ["element", "text", "table-cell", "svg-part"];

  // The host's per-load token, read from the script element the host injected, then removed from the DOM.
  // Every message to the host carries it, so the host can refuse a message from anything else in the frame
  // (see CONTRACT.md, "Trust"). A plain file opened with no host has no token.
  var hostToken = "";
  (function readToken() {
    var doc = root.document;
    var current = doc && doc.currentScript;
    if (current && current.hasAttribute(TOKEN_ATTR)) {
      hostToken = current.getAttribute(TOKEN_ATTR);
      current.removeAttribute(TOKEN_ATTR);
    }
  })();

  // The elements this script added to the page. Only these count as the notes interface: a report cannot
  // opt its own elements out of being noted by copying an attribute.
  var uiRoots = [];

  // ---------------------------------------------------------------------------------------------
  // Text and selectors
  // ---------------------------------------------------------------------------------------------

  function cleanText(value, limit) {
    var text = String(value == null ? "" : value).replace(/\s+/g, " ").trim();
    return limit ? text.slice(0, limit) : text;
  }

  function textOf(el) {
    if (!el) return "";
    return cleanText(el.innerText != null && el.innerText !== "" ? el.innerText : el.textContent, QUOTE_LENGTH);
  }

  // CSS identifier escaping (CSS Object Model, "serialize an identifier"). Written out rather than
  // calling CSS.escape so the selector a note carries is the same string in every browser and in tests.
  function escapeIdent(value) {
    var s = String(value);
    var out = "";
    for (var i = 0; i < s.length; i++) {
      var ch = s.charAt(i);
      var code = s.charCodeAt(i);
      if (code === 0) {
        out += String.fromCharCode(0xFFFD);
      } else if ((code >= 1 && code <= 31) || code === 127 ||
        (i === 0 && code >= 48 && code <= 57) ||
        (i === 1 && code >= 48 && code <= 57 && s.charCodeAt(0) === 45)) {
        out += "\\" + code.toString(16) + " ";
      } else if (i === 0 && s.length === 1 && code === 45) {
        out += "\\" + ch;
      } else if (code >= 128 || code === 45 || code === 95 ||
        (code >= 48 && code <= 57) || (code >= 65 && code <= 90) || (code >= 97 && code <= 122)) {
        out += ch;
      } else {
        out += "\\" + ch;
      }
    }
    return out;
  }

  function uniqueIdSelector(el) {
    if (!el.id) return "";
    // A plain id reads as #id. Anything else becomes an attribute selector, which every selector engine
    // reads the same way, where escaped identifiers are not reliably supported.
    var candidate = /^-?[A-Za-z_][A-Za-z0-9_-]*$/.test(el.id)
      ? "#" + el.id
      : "[id=" + JSON.stringify(el.id) + "]";
    var doc = el.ownerDocument;
    try {
      if (doc.querySelectorAll(candidate).length === 1) return candidate;
    } catch (e) {
      return "";
    }
    return "";
  }

  // A selector that document.querySelector resolves back to exactly this element: a child-combinator
  // path from the nearest uniquely-identified ancestor (or the root element), with :nth-of-type where
  // a parent has more than one child of the same type.
  function selectorFor(el) {
    if (!el || el.nodeType !== 1) return "";
    var parts = [];
    var node = el;
    while (node && node.nodeType === 1) {
      var byId = uniqueIdSelector(node);
      if (byId) {
        parts.unshift(byId);
        break;
      }
      var part = escapeIdent(node.localName);
      var parent = node.parentElement;
      if (parent) {
        var same = [];
        for (var i = 0; i < parent.children.length; i++) {
          if (parent.children[i].localName === node.localName) same.push(parent.children[i]);
        }
        if (same.length > 1) part += ":nth-of-type(" + (same.indexOf(node) + 1) + ")";
      }
      parts.unshift(part);
      node = parent;
    }
    return parts.join(" > ");
  }

  // ---------------------------------------------------------------------------------------------
  // Table cells - row and column labels, empty when they cannot be proven
  // ---------------------------------------------------------------------------------------------

  function tag(el) {
    return el ? String(el.localName || "").toLowerCase() : "";
  }

  // The rows of one table only - never a nested table's rows.
  function rowsIn(el) {
    var rows = [];
    var children = el ? el.children : [];
    for (var i = 0; i < children.length; i++) {
      var t = tag(children[i]);
      if (t === "tr") rows.push(children[i]);
      else if (t === "thead" || t === "tbody" || t === "tfoot") rows = rows.concat(rowsIn(children[i]));
    }
    return rows;
  }

  function cellsOf(row) {
    var cells = [];
    var children = row ? row.children : [];
    for (var i = 0; i < children.length; i++) {
      var t = tag(children[i]);
      if (t === "td" || t === "th") cells.push(children[i]);
    }
    return cells;
  }

  function spanValue(cell, name) {
    var match = /^[\t\n\f\r ]*(\d+)/.exec(String(cell.getAttribute(name) || ""));
    return match ? Number(match[1]) : null;
  }

  function colSpan(cell) {
    var span = spanValue(cell, "colspan");
    return span !== null && span >= 1 ? span : 1;
  }

  function rowGroup(table, row) {
    var node = row.parentElement;
    while (node && node !== table) {
      var t = tag(node);
      if (t === "thead" || t === "tbody" || t === "tfoot") return node;
      node = node.parentElement;
    }
    return table;
  }

  // True when a rowspan from an earlier row of the same group reaches this row, so its DOM order is
  // no longer its column order.
  function rowIsShifted(table, row) {
    var rows = rowsIn(rowGroup(table, row));
    var index = rows.indexOf(row);
    if (index < 0) return true;
    for (var i = 0; i < index; i++) {
      var cells = cellsOf(rows[i]);
      for (var j = 0; j < cells.length; j++) {
        var span = spanValue(cells[j], "rowspan");
        if (span === 0 || (span !== null && span > index - i)) return true;
      }
    }
    return false;
  }

  function headerRowOf(table) {
    var children = table.children;
    for (var i = 0; i < children.length; i++) {
      if (tag(children[i]) === "thead") {
        var headRows = rowsIn(children[i]);
        return headRows.length ? headRows[headRows.length - 1] : null;
      }
    }
    var first = rowsIn(table)[0];
    var cells = cellsOf(first);
    if (!cells.length) return null;
    for (var k = 0; k < cells.length; k++) if (tag(cells[k]) !== "th") return null;
    return first;
  }

  function columnLabel(headerRow, cells, index) {
    if (!headerRow) return "";
    var headers = cellsOf(headerRow);
    var headerWidth = 0;
    var rowWidth = 0;
    var i;
    for (i = 0; i < headers.length; i++) headerWidth += colSpan(headers[i]);
    for (i = 0; i < cells.length; i++) rowWidth += colSpan(cells[i]);
    if (headerWidth === 0 || headerWidth !== rowWidth) return "";
    var start = 0;
    for (i = 0; i < index; i++) start += colSpan(cells[i]);
    var end = start + colSpan(cells[index]);
    var cursor = 0;
    for (i = 0; i < headers.length; i++) {
      var next = cursor + colSpan(headers[i]);
      if (cursor === start && next === end) return textOf(headers[i]);
      if (start < next) return "";
      cursor = next;
    }
    return "";
  }

  function tableCellAnchor(el) {
    var cell = el && el.closest ? el.closest("td,th") : null;
    var row = cell ? cell.closest("tr") : null;
    var table = row ? row.closest("table") : null;
    if (!cell || !row || !table) return null;
    var cells = cellsOf(row);
    var index = cells.indexOf(cell);
    if (index < 0) return null;

    var headerRow = headerRowOf(table);
    var shifted = rowIsShifted(table, row);
    var gridShifted = shifted || (headerRow ? rowIsShifted(table, headerRow) : false);
    var inHeader = headerRow === row || Boolean(cell.closest("thead"));

    var declared = null;
    var allHeaders = true;
    for (var i = 0; i < cells.length; i++) {
      if (tag(cells[i]) !== "th") allHeaders = false;
      if (!declared && tag(cells[i]) === "th" && String(cells[i].getAttribute("scope") || "").toLowerCase() === "row") {
        declared = cells[i];
      }
    }
    var rowHeading = inHeader ? null : (declared || (allHeaders || shifted ? null : cells[0]));

    return {
      type: "table-cell",
      selector: selectorFor(cell),
      quote: textOf(cell),
      rowLabel: rowHeading ? textOf(rowHeading) : "",
      columnLabel: gridShifted ? "" : columnLabel(headerRow, cells, index)
    };
  }

  // ---------------------------------------------------------------------------------------------
  // SVG parts, text selections, everything else
  // ---------------------------------------------------------------------------------------------

  function svgLabel(el) {
    var node = el;
    while (node && node.nodeType === 1) {
      var aria = node.getAttribute("aria-label");
      if (aria && cleanText(aria)) return { el: node, label: cleanText(aria, QUOTE_LENGTH) };
      var data = node.getAttribute("data-label");
      if (data && cleanText(data)) return { el: node, label: cleanText(data, QUOTE_LENGTH) };
      for (var i = 0; i < node.children.length; i++) {
        if (node.children[i].localName === "title" && cleanText(node.children[i].textContent)) {
          return { el: node, label: cleanText(node.children[i].textContent, QUOTE_LENGTH) };
        }
      }
      if (node.localName === "svg") return null;
      node = node.parentElement;
    }
    return null;
  }

  function svgPartAnchor(el) {
    var svg = el && el.closest ? el.closest("svg") : null;
    if (!svg) return null;
    var found = svgLabel(el);
    var target = found ? found.el : el;
    return {
      type: "svg-part",
      selector: selectorFor(target),
      quote: cleanText(target.textContent, QUOTE_LENGTH),
      label: found ? found.label : ""
    };
  }

  function isUi(el) {
    if (!el) return false;
    for (var i = 0; i < uiRoots.length; i++) {
      if (uiRoots[i] === el || uiRoots[i].contains(el)) return true;
    }
    return false;
  }

  function anchorFor(el) {
    if (!el || el.nodeType !== 1 || isUi(el)) return null;
    return tableCellAnchor(el) || svgPartAnchor(el) || {
      type: "element",
      selector: selectorFor(el),
      quote: textOf(el)
    };
  }

  function textSelectionAnchor(selection) {
    if (!selection || selection.rangeCount === 0) return null;
    var range = selection.getRangeAt(0);
    if (range.collapsed) return null;
    var quote = cleanText(selection.toString(), MAX_STRING);
    if (!quote) return null;
    var node = range.commonAncestorContainer;
    var el = node && node.nodeType === 1 ? node : (node ? node.parentElement : null);
    if (!el || isUi(el)) return null;
    return { type: "text", selector: selectorFor(el), quote: quote };
  }

  // ---------------------------------------------------------------------------------------------
  // Message validation - every inbound message is checked whole before anything is applied
  // ---------------------------------------------------------------------------------------------

  function isPlainObject(v) {
    return v !== null && typeof v === "object" && !Array.isArray(v);
  }

  function isStr(v) {
    return typeof v === "string" && v.length <= MAX_STRING;
  }

  function isOptStr(v) {
    return v === undefined || isStr(v);
  }

  function validAnchor(a) {
    return isPlainObject(a) && ANCHOR_TYPES.indexOf(a.type) >= 0 && isStr(a.selector) && isStr(a.quote) &&
      isOptStr(a.rowLabel) && isOptStr(a.columnLabel) && isOptStr(a.label);
  }

  function validItem(item) {
    if (!isPlainObject(item) || !isStr(item.id) || item.id === "") return false;
    if (item.kind === "note") return isStr(item.text) && validAnchor(item.anchor);
    if (item.kind === "answer") {
      return isStr(item.questionId) && item.questionId !== "" && isStr(item.question) &&
        isStr(item.optionValue) && isStr(item.optionLabel) && isStr(item.comment);
    }
    return false;
  }

  // A queued item may be waiting for the host to confirm it (pending), or carry the host's words for why it
  // was refused and is still queued (statusLabel).
  function validQueuedItem(item) {
    return validItem(item) && (item.pending === undefined || typeof item.pending === "boolean") &&
      isOptStr(item.statusLabel);
  }

  function validSentItem(item) {
    return validItem(item) && isStr(item.status) && isStr(item.statusLabel);
  }

  function validReply(r) {
    return isPlainObject(r) && isStr(r.id) && r.id !== "" && isStr(r.text) && isStr(r.at);
  }

  function validAnswerDraft(d) {
    return isPlainObject(d) && isStr(d.questionId) && d.questionId !== "" && isStr(d.optionValue) && isStr(d.comment);
  }

  function everyValid(list, check) {
    if (!Array.isArray(list)) return false;
    for (var i = 0; i < list.length; i++) if (!check(list[i])) return false;
    return true;
  }

  function validState(s) {
    if (!isPlainObject(s)) return false;
    if (!everyValid(s.queued, validQueuedItem) || !everyValid(s.sent, validSentItem) ||
      !everyValid(s.replies, validReply) || !everyValid(s.answerDrafts, validAnswerDraft)) {
      return false;
    }
    if (s.draft !== null && !(isPlainObject(s.draft) && validAnchor(s.draft.anchor) && isStr(s.draft.text))) {
      return false;
    }
    return isPlainObject(s.scroll) && isFiniteNumber(s.scroll.x) && isFiniteNumber(s.scroll.y);
  }

  function isFiniteNumber(v) {
    return typeof v === "number" && isFinite(v);
  }

  function validStatusUpdate(u) {
    return isPlainObject(u) && isStr(u.id) && isStr(u.status) && isStr(u.statusLabel);
  }

  // Returns { type, payload } for a well-formed host message, or null for anything else.
  function parseInbound(data) {
    if (!isPlainObject(data) || data.channel !== CHANNEL || data.version !== VERSION) return null;
    var p = data.payload;
    if (!isPlainObject(p)) return null;
    if (data.type === "restore") return validState(p.state) ? { type: "restore", payload: p } : null;
    if (data.type === "status") return everyValid(p.updates, validStatusUpdate) ? { type: "status", payload: p } : null;
    if (data.type === "reply") return validReply(p.reply) ? { type: "reply", payload: p } : null;
    return null;
  }

  function envelope(type, payload) {
    var message = { channel: CHANNEL, version: VERSION, type: type, payload: payload };
    if (hostToken) message.token = hostToken;
    return message;
  }

  // ---------------------------------------------------------------------------------------------
  // The model - queued, sent, replies, drafts. No DOM, so it is testable on its own.
  // ---------------------------------------------------------------------------------------------

  function copy(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function emptyState() {
    return { queued: [], sent: [], replies: [], draft: null, answerDrafts: [], scroll: { x: 0, y: 0 } };
  }

  function createModel() {
    var state = emptyState();

    function nextId(prefix) {
      var max = 0;
      var all = state.queued.concat(state.sent);
      for (var i = 0; i < all.length; i++) {
        var m = /^[a-z]+(\d+)$/.exec(all[i].id);
        if (m && Number(m[1]) > max) max = Number(m[1]);
      }
      return prefix + (max + 1);
    }

    function indexOfId(list, id) {
      for (var i = 0; i < list.length; i++) if (list[i].id === id) return i;
      return -1;
    }

    return {
      snapshot: function () {
        return copy(state);
      },
      // Returns the queued item, or throws when the note cannot be queued. The page checks lengths first
      // and shows the reason, so a throw here is a defect in the page, not a user mistake.
      queueNote: function (anchor, text) {
        if (!validAnchor(anchor)) throw new Error("queueNote: the anchor is not a valid anchor");
        var body = String(text == null ? "" : text).trim();
        if (!body) throw new Error("queueNote: a note needs some text");
        var item = { id: nextId("n"), kind: "note", text: body, anchor: copy(anchor) };
        if (!validItem(item)) throw new Error("queueNote: the note is longer than " + MAX_STRING + " characters");
        state.queued.push(item);
        return copy(item);
      },
      // One unsent answer per question: queueing again replaces the LATEST queued answer to that question in
      // place - unless that one is already with the host (pending), in which case the new answer is added
      // with a new id so the host cannot confuse the two. So a pending answer plus any number of revisions is
      // at most two queued items: the pending one, and the newest revision.
      queueAnswer: function (answer) {
        var item = {
          id: "",
          kind: "answer",
          questionId: String(answer.questionId || ""),
          question: String(answer.question || ""),
          optionValue: String(answer.optionValue || ""),
          optionLabel: String(answer.optionLabel || ""),
          comment: String(answer.comment || "").trim()
        };
        var latest = -1;
        for (var i = 0; i < state.queued.length; i++) {
          if (state.queued[i].kind === "answer" && state.queued[i].questionId === item.questionId) latest = i;
        }
        if (latest >= 0 && !state.queued[latest].pending) {
          item.id = state.queued[latest].id;
          if (!validItem(item)) throw new Error("queueAnswer: the answer is not valid");
          state.queued[latest] = item;
          return copy(item);
        }
        item.id = nextId("a");
        if (!validItem(item)) throw new Error("queueAnswer: the answer is not valid");
        state.queued.push(item);
        return copy(item);
      },
      // A pending item is with the host already, so it cannot be taken back.
      remove: function (id) {
        state.queued = state.queued.filter(function (it) { return it.id !== id || it.pending === true; });
      },
      queuedAnswerFor: function (questionId) {
        var found = null;
        for (var i = 0; i < state.queued.length; i++) {
          if (state.queued[i].kind === "answer" && state.queued[i].questionId === questionId) found = state.queued[i];
        }
        return found ? copy(found) : null;
      },
      // The message Send posts: the whole queue, in order, including items still waiting for the host to
      // confirm them (the host treats a repeated id as the same item). Does not change state.
      sendMessage: function () {
        var items = copy(state.queued);
        for (var i = 0; i < items.length; i++) {
          delete items[i].pending;
          delete items[i].statusLabel;
        }
        return envelope("send", { items: items });
      },
      // Marks the whole queue as waiting for the host. Nothing leaves the queue until the host says so.
      markPending: function () {
        for (var i = 0; i < state.queued.length; i++) {
          state.queued[i].pending = true;
          delete state.queued[i].statusLabel;
        }
      },
      // The host's word on each item. "refused" leaves a queued item queued, with the host's reason;
      // anything else moves it to sent. A sent item just takes the new words. Unknown ids are skipped.
      applyStatus: function (updates) {
        for (var i = 0; i < updates.length; i++) {
          var u = updates[i];
          var q = indexOfId(state.queued, u.id);
          if (q >= 0) {
            var item = state.queued[q];
            if (u.status === "refused") {
              item.pending = false;
              item.statusLabel = u.statusLabel;
            } else {
              state.queued.splice(q, 1);
              delete item.pending;
              item.status = u.status;
              item.statusLabel = u.statusLabel;
              state.sent.push(item);
            }
            continue;
          }
          var s = indexOfId(state.sent, u.id);
          if (s >= 0) {
            state.sent[s].status = u.status;
            state.sent[s].statusLabel = u.statusLabel;
          }
        }
      },
      addReply: function (reply) {
        var i = indexOfId(state.replies, reply.id);
        if (i >= 0) state.replies[i] = copy(reply);
        else state.replies.push(copy(reply));
      },
      setDraft: function (draft) {
        state.draft = draft ? copy(draft) : null;
      },
      clearAnswerDraft: function (questionId) {
        state.answerDrafts = state.answerDrafts.filter(function (d) { return d.questionId !== questionId; });
      },
      setAnswerDraft: function (questionId, optionValue, comment) {
        var draft = { questionId: String(questionId), optionValue: String(optionValue), comment: String(comment) };
        if (!validAnswerDraft(draft)) throw new Error("setAnswerDraft: the draft is not valid");
        state.answerDrafts = state.answerDrafts.filter(function (d) { return d.questionId !== draft.questionId; });
        state.answerDrafts.push(draft);
      },
      setScroll: function (x, y) {
        state.scroll = { x: x, y: y };
      },
      restore: function (next) {
        if (!validState(next)) throw new Error("restore: the state is not valid");
        state = copy(next);
      }
    };
  }

  // ---------------------------------------------------------------------------------------------
  // The page UI
  // ---------------------------------------------------------------------------------------------

  // The only rules this script puts on the report itself: the picking cursor and the hover outline.
  var PAGE_CSS = [
    ".drn-picking, .drn-picking * { cursor: crosshair !important; }",
    ".drn-hover { outline: 2px solid #0066B8 !important; outline-offset: 2px; }"
  ].join("\n");

  // The tray's own rules. They live inside the tray's shadow root, where the report's CSS cannot reach.
  var CSS_TEXT = [
    ":host { font-family: 'Segoe UI', system-ui, sans-serif; font-size: 14px; line-height: 1.4; color: #16181D; }",
    "* { box-sizing: border-box; }",
    ".drn-tray { position: fixed; right: 16px; bottom: 16px; z-index: 2147483000; width: 360px; max-width: calc(100vw - 32px); max-height: calc(100vh - 32px); overflow: auto; background: #FFFFFF; border: 1px solid #D5D8DE; border-radius: 12px; box-shadow: 0 6px 24px rgba(0,0,0,.14); }",
    ".drn-tray.drn-collapsed .drn-body { display: none; }",
    ".drn-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 10px 12px; border-bottom: 1px solid #E6E8EC; }",
    ".drn-body { padding: 10px 12px; }",
    ".drn-h { font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: .08em; color: #5A616B; margin: 12px 0 6px; }",
    ".drn-list { list-style: none; margin: 0; padding: 0; }",
    ".drn-list li { border: 1px solid #E6E8EC; border-radius: 8px; padding: 8px; margin: 0 0 6px; overflow-wrap: anywhere; }",
    ".drn-where { font-size: 12px; color: #5A616B; }",
    ".drn-status { font-size: 12px; font-weight: 600; color: #0066B8; }",
    ".drn-empty { font-size: 13px; color: #8A909A; }",
    "button { font: inherit; font-weight: 600; border-radius: 8px; padding: 6px 12px; cursor: pointer; border: 1px solid #D5D8DE; background: #F5F6F8; color: #16181D; }",
    "button.drn-primary { background: #0066B8; border-color: #0066B8; color: #FFFFFF; }",
    "button:disabled { opacity: .5; cursor: default; }",
    "textarea { width: 100%; min-height: 64px; font: inherit; padding: 6px 8px; border: 1px solid #D5D8DE; border-radius: 8px; }",
    ".drn-row { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }",
    ".drn-payload { white-space: pre-wrap; overflow-wrap: anywhere; font-family: Consolas, monospace; font-size: 12px; background: #F5F6F8; border: 1px solid #E6E8EC; border-radius: 8px; padding: 8px; }",
    ".drn-q-state { font-size: 13px; color: #5A616B; margin-left: 8px; }",
    "@media (max-width: 600px) { .drn-tray { right: 0; left: 0; bottom: 0; width: auto; max-width: none; max-height: 70vh; border-radius: 12px 12px 0 0; } }"
  ].join("\n");

  function describeAnchor(anchor) {
    if (!anchor) return "";
    if (anchor.type === "table-cell") {
      var where = [];
      if (anchor.rowLabel) where.push("row \"" + anchor.rowLabel + "\"");
      if (anchor.columnLabel) where.push("column \"" + anchor.columnLabel + "\"");
      return "Table cell" + (where.length ? " - " + where.join(", ") : "") + ": \"" + anchor.quote.slice(0, 80) + "\"";
    }
    if (anchor.type === "svg-part") return "Diagram part" + (anchor.label ? " \"" + anchor.label + "\"" : "");
    if (anchor.type === "text") return "Selected text: \"" + anchor.quote.slice(0, 80) + "\"";
    return "\"" + anchor.quote.slice(0, 80) + "\"";
  }

  function describeItem(item) {
    if (item.kind === "answer") {
      return {
        head: "Answer: " + item.question,
        body: item.optionLabel + (item.comment ? " - " + item.comment : "")
      };
    }
    return { head: describeAnchor(item.anchor), body: item.text };
  }

  // The whole question text, never cut short: the Queue button checks its length and declines to queue a
  // question that is too long, so the answer never carries different words from the ones the owner read.
  function questionText(q) {
    var explicit = q.getAttribute("data-dev-report-question-text");
    if (explicit && cleanText(explicit)) return cleanText(explicit);
    var heading = q.querySelector("h1,h2,h3,h4,h5,h6");
    var headingText = heading ? cleanText(heading.innerText != null && heading.innerText !== "" ? heading.innerText : heading.textContent) : "";
    if (headingText) return headingText;
    return q.getAttribute("data-dev-report-question");
  }

  function optionLabel(input) {
    var doc = input.ownerDocument;
    var label = input.closest("label");
    if (!label && input.id) {
      var labels = doc.getElementsByTagName("label");
      for (var i = 0; i < labels.length; i++) {
        if (labels[i].htmlFor === input.id) {
          label = labels[i];
          break;
        }
      }
    }
    var text = label ? textOf(label) : "";
    return text || String(input.value || "");
  }

  // A question's options and comment are the ones inside it that do not belong to a question nested inside
  // it - the same ownership rule the Gateway's shape check applies (which refuses nested questions outright).
  function ownedBy(q, list) {
    return Array.prototype.slice.call(list).filter(function (node) {
      return node.parentElement && node.parentElement.closest("[data-dev-report-question]") === q;
    });
  }

  function radiosIn(q) {
    return ownedBy(q, q.querySelectorAll("input[type=radio]"));
  }

  function checkedIn(q) {
    var chosen = null;
    radiosIn(q).forEach(function (r) { if (r.checked) chosen = r; });
    return chosen;
  }

  function commentIn(q) {
    return ownedBy(q, q.querySelectorAll("textarea[data-dev-report-comment]"))[0] || null;
  }

  // An element in the report that holds part of the notes interface in its own shadow root. The report's CSS
  // cannot select anything inside the root, and the inline !important rules on the host element outrank any
  // stylesheet rule that targets the host itself.
  function shadowHost(doc) {
    var host = doc.createElement("div");
    host.setAttribute("data-dev-report-ui", "");
    host.style.cssText = "all: initial !important; display: block !important; visibility: visible !important; " +
      "opacity: 1 !important; position: static !important; transform: none !important; filter: none !important; " +
      "clip-path: none !important; pointer-events: auto !important;";
    var shadow = host.attachShadow({ mode: "open" });
    var style = doc.createElement("style");
    style.textContent = CSS_TEXT;
    shadow.appendChild(style);
    return { host: host, shadow: shadow };
  }

  function el(doc, name, attrs, text) {
    var node = doc.createElement(name);
    if (attrs) {
      for (var key in attrs) {
        if (Object.prototype.hasOwnProperty.call(attrs, key)) node.setAttribute(key, attrs[key]);
      }
    }
    if (text != null) node.textContent = text;
    return node;
  }

  function start(options) {
    var opts = options || {};
    var win = opts.window || root;
    var doc = win.document;
    // The flag lives on the window under a symbol, where report markup cannot set it.
    if (win[STARTED] === true) {
      throw new Error("dev-report-notes: already started on this page");
    }
    win[STARTED] = true;

    var model = createModel();
    var hosted = false;
    var framed = win.parent && win.parent !== win;
    var picking = false;
    var hovered = null;
    var lastSelection = null;
    var scrollTimer = null;
    var lastPayload = null;

    // The host channel. The page makes a MessageChannel, hands one end to its parent inside ready, and from
    // then on everything - both ways - goes over that private port. A page the frame is later navigated to
    // never holds the port, so it can neither receive what the host pushes nor pass for this page.
    var port = null;
    var channel = framed ? new win.MessageChannel() : null;
    if (channel) port = channel.port1;

    function post(type, payload) {
      if (!port) return;
      port.postMessage(envelope(type, payload));
    }

    function emitState() {
      post("state-changed", { state: model.snapshot() });
    }

    // --- tray -----------------------------------------------------------------------------------
    var pageStyle = el(doc, "style", { "data-dev-report-ui": "" });
    pageStyle.textContent = PAGE_CSS;
    (doc.head || doc.documentElement).appendChild(pageStyle);
    uiRoots.push(pageStyle);

    var trayRoot = shadowHost(doc);
    uiRoots.push(trayRoot.host);
    var tray = el(doc, "aside", { "class": "drn-tray drn-collapsed", "aria-label": "Notes for the agent" });
    var head = el(doc, "div", { "class": "drn-head" });
    var toggle = el(doc, "button", { type: "button", "data-drn": "toggle", "aria-expanded": "false" }, "Notes");
    var noteBtn = el(doc, "button", { type: "button", "data-drn": "pick" }, "Add a note");
    head.appendChild(toggle);
    head.appendChild(noteBtn);
    tray.appendChild(head);

    var body = el(doc, "div", { "class": "drn-body" });
    var selectionBtn = el(doc, "button", { type: "button", "data-drn": "note-selection", disabled: "" }, "Note on selected text");
    var pickHint = el(doc, "div", { "class": "drn-where", "data-drn": "pick-hint" }, "");
    var composer = el(doc, "div", { "data-drn": "composer", hidden: "" });
    var composerWhere = el(doc, "div", { "class": "drn-where", "data-drn": "composer-where" });
    var composerText = el(doc, "textarea", { "data-drn": "composer-text", placeholder: "Your note to the agent", maxlength: String(MAX_STRING) });
    var composerRow = el(doc, "div", { "class": "drn-row" });
    var composerQueue = el(doc, "button", { type: "button", "class": "drn-primary", "data-drn": "composer-queue" }, "Queue note");
    var composerCancel = el(doc, "button", { type: "button", "data-drn": "composer-cancel" }, "Cancel");
    composerRow.appendChild(composerQueue);
    composerRow.appendChild(composerCancel);
    composer.appendChild(composerWhere);
    composer.appendChild(composerText);
    composer.appendChild(composerRow);

    var queuedList = el(doc, "ul", { "class": "drn-list", "data-drn": "queued" });
    var sendRow = el(doc, "div", { "class": "drn-row" });
    var sendBtn = el(doc, "button", { type: "button", "class": "drn-primary", "data-drn": "send", disabled: "" }, "Send");
    sendRow.appendChild(sendBtn);
    var payloadBox = el(doc, "div", { "data-drn": "payload-box", hidden: "" });
    var payloadNote = el(doc, "div", { "class": "drn-where" },
      "No app is connected to this page, so nothing was sent. This is exactly what Send would send:");
    var payloadPre = el(doc, "pre", { "class": "drn-payload", "data-drn": "payload" });
    payloadBox.appendChild(payloadNote);
    payloadBox.appendChild(payloadPre);
    var sentList = el(doc, "ul", { "class": "drn-list", "data-drn": "sent" });
    var replyList = el(doc, "ul", { "class": "drn-list", "data-drn": "replies" });

    body.appendChild(selectionBtn);
    body.appendChild(pickHint);
    body.appendChild(composer);
    body.appendChild(el(doc, "div", { "class": "drn-h" }, "Queued - not sent yet"));
    body.appendChild(queuedList);
    body.appendChild(sendRow);
    body.appendChild(payloadBox);
    body.appendChild(el(doc, "div", { "class": "drn-h" }, "Sent"));
    body.appendChild(sentList);
    body.appendChild(el(doc, "div", { "class": "drn-h" }, "Replies from the agent"));
    body.appendChild(replyList);
    tray.appendChild(body);
    trayRoot.shadow.appendChild(tray);
    doc.body.appendChild(trayRoot.host);

    function setOpen(open) {
      if (open) tray.classList.remove("drn-collapsed");
      else tray.classList.add("drn-collapsed");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    }

    function renderList(list, items, withStatus) {
      while (list.firstChild) list.removeChild(list.firstChild);
      if (!items.length) {
        list.appendChild(el(doc, "li", { "class": "drn-empty" }, "Nothing here."));
        return;
      }
      for (var i = 0; i < items.length; i++) {
        var d = describeItem(items[i]);
        var li = el(doc, "li", { "data-item-id": items[i].id });
        li.appendChild(el(doc, "div", { "class": "drn-where" }, d.head));
        li.appendChild(el(doc, "div", null, d.body));
        if (withStatus) {
          li.appendChild(el(doc, "div", { "class": "drn-status", "data-drn": "status" }, items[i].statusLabel));
        } else if (items[i].pending) {
          li.appendChild(el(doc, "div", { "class": "drn-status", "data-drn": "status" }, "Waiting for the app to confirm it has this"));
        } else {
          if (items[i].statusLabel) {
            li.appendChild(el(doc, "div", { "class": "drn-status", "data-drn": "status" }, items[i].statusLabel));
          }
          var remove = el(doc, "button", { type: "button", "data-drn": "remove", "data-remove-id": items[i].id }, "Remove");
          li.appendChild(remove);
        }
        list.appendChild(li);
      }
    }

    function render() {
      var s = model.snapshot();
      renderList(queuedList, s.queued, false);
      renderList(sentList, s.sent, true);
      while (replyList.firstChild) replyList.removeChild(replyList.firstChild);
      if (!s.replies.length) replyList.appendChild(el(doc, "li", { "class": "drn-empty" }, "No replies yet."));
      for (var i = 0; i < s.replies.length; i++) {
        var li = el(doc, "li", { "data-reply-id": s.replies[i].id });
        li.appendChild(el(doc, "div", { "class": "drn-where" }, s.replies[i].at));
        li.appendChild(el(doc, "div", null, s.replies[i].text));
        replyList.appendChild(li);
      }
      if (s.queued.length) sendBtn.removeAttribute("disabled");
      else sendBtn.setAttribute("disabled", "");
      sendBtn.textContent = s.queued.length ? "Send " + s.queued.length : "Send";
      toggle.textContent = "Notes (" + s.queued.length + " queued)";
      if (s.draft) {
        composer.removeAttribute("hidden");
        composerWhere.textContent = describeAnchor(s.draft.anchor);
        if (composerText.value !== s.draft.text) composerText.value = s.draft.text;
      } else {
        composer.setAttribute("hidden", "");
        composerText.value = "";
      }
      renderQuestionStates(s);
    }

    function changed() {
      render();
      emitState();
    }

    // --- picking an element to note -------------------------------------------------------------
    function setPicking(on) {
      picking = on;
      if (on) {
        doc.documentElement.classList.add("drn-picking");
        pickHint.textContent = "Click the paragraph, table cell or diagram part your note is about.";
        noteBtn.textContent = "Cancel";
        setOpen(true);
      } else {
        doc.documentElement.classList.remove("drn-picking");
        pickHint.textContent = "";
        noteBtn.textContent = "Add a note";
        if (hovered) hovered.classList.remove("drn-hover");
        hovered = null;
      }
    }

    function openDraft(anchor) {
      model.setDraft({ anchor: anchor, text: "" });
      setOpen(true);
      changed();
      composerText.focus();
    }

    doc.addEventListener("mouseover", function (event) {
      if (!picking) return;
      var target = event.target;
      if (hovered) hovered.classList.remove("drn-hover");
      hovered = null;
      if (target && target.nodeType === 1 && !isUi(target) && target.classList) {
        hovered = target;
        hovered.classList.add("drn-hover");
      }
    }, true);

    doc.addEventListener("click", function (event) {
      if (!picking) return;
      var target = event.target;
      if (isUi(target)) return;
      event.preventDefault();
      event.stopPropagation();
      if (hovered) hovered.classList.remove("drn-hover");
      var anchor = anchorFor(target);
      setPicking(false);
      if (anchor) openDraft(anchor);
    }, true);

    doc.addEventListener("selectionchange", function () {
      var anchor = textSelectionAnchor(win.getSelection ? win.getSelection() : null);
      // Keep the last real selection: tapping the button on a phone clears the selection first.
      if (anchor) {
        lastSelection = anchor;
        selectionBtn.removeAttribute("disabled");
        selectionBtn.textContent = "Note on \"" + anchor.quote.slice(0, 30) + (anchor.quote.length > 30 ? "..." : "") + "\"";
      }
    });

    selectionBtn.addEventListener("click", function () {
      if (!lastSelection) return;
      var anchor = lastSelection;
      lastSelection = null;
      selectionBtn.setAttribute("disabled", "");
      selectionBtn.textContent = "Note on selected text";
      openDraft(anchor);
    });

    toggle.addEventListener("click", function () {
      setOpen(tray.classList.contains("drn-collapsed"));
    });

    noteBtn.addEventListener("click", function () {
      setPicking(!picking);
    });

    composerText.addEventListener("input", function () {
      var s = model.snapshot();
      if (!s.draft) return;
      model.setDraft({ anchor: s.draft.anchor, text: composerText.value });
      emitState();
    });

    composerQueue.addEventListener("click", function () {
      var s = model.snapshot();
      if (!s.draft) return;
      if (!composerText.value.trim()) {
        composerWhere.textContent = describeAnchor(s.draft.anchor) + " - type a note first.";
        return;
      }
      if (composerText.value.trim().length > MAX_STRING) {
        composerWhere.textContent = describeAnchor(s.draft.anchor) + " - the note is too long; the limit is " + MAX_STRING + " characters.";
        return;
      }
      model.queueNote(s.draft.anchor, composerText.value);
      model.setDraft(null);
      changed();
    });

    composerCancel.addEventListener("click", function () {
      model.setDraft(null);
      changed();
    });

    queuedList.addEventListener("click", function (event) {
      var button = event.target.closest ? event.target.closest("[data-remove-id]") : null;
      if (!button) return;
      model.remove(button.getAttribute("data-remove-id"));
      changed();
    });

    sendBtn.addEventListener("click", function () {
      var message = model.sendMessage();
      if (!message.payload.items.length) return;
      if (!hosted) {
        lastPayload = message;
        payloadPre.textContent = JSON.stringify(message, null, 2);
        payloadBox.removeAttribute("hidden");
        return;
      }
      payloadBox.setAttribute("hidden", "");
      // Nothing leaves the queue here: posting a message is not the host accepting it. Each item moves to
      // Sent only when the host answers with a status for its id.
      post("send", message.payload);
      model.markPending();
      changed();
    });

    // --- questions ------------------------------------------------------------------------------
    var questions = Array.prototype.slice.call(doc.querySelectorAll("[data-dev-report-question]"));
    var questionStates = new Map();

    questions.forEach(function (q) {
      var id = q.getAttribute("data-dev-report-question");
      var radios = radiosIn(q);
      // One answer per question, whatever the radio names say. The shape check requires one shared name, but
      // a page with two names lets the browser keep two options checked, and then the answer queued would be
      // whichever came last rather than the one the owner clicked. So the question keeps at most one checked
      // option itself: at start the recommendation (or the first checked), and after that the owner's choice.
      var checkedAtStart = radios.filter(function (r) { return r.checked; });
      var keep = checkedAtStart.filter(function (r) { return r.hasAttribute("data-recommended"); })[0] ||
        checkedAtStart[0] ||
        radios.filter(function (r) { return r.hasAttribute("data-recommended"); })[0] ||
        null;
      radios.forEach(function (r) { r.checked = r === keep; });
      radios.forEach(function (r) {
        r.addEventListener("change", function () {
          if (!r.checked) return;
          radios.forEach(function (other) { if (other !== r) other.checked = false; });
        });
      });
      var rowRoot = shadowHost(doc);
      uiRoots.push(rowRoot.host);
      var row = el(doc, "div", { "class": "drn-row" });
      var queueBtn = el(doc, "button", { type: "button", "class": "drn-primary", "data-drn": "queue-answer", "data-question-id": id }, "Queue answer");
      var stateText = el(doc, "span", { "class": "drn-q-state", "data-drn": "question-state" }, "");
      row.appendChild(queueBtn);
      row.appendChild(stateText);
      rowRoot.shadow.appendChild(row);
      q.appendChild(rowRoot.host);
      questionStates.set(id, stateText);
      var comment = commentIn(q);
      if (comment) comment.setAttribute("maxlength", String(MAX_STRING));

      function saveAnswerDraft() {
        var chosen = checkedIn(q);
        var text = comment ? comment.value : "";
        if (text.length > MAX_STRING || (chosen && String(chosen.value).length > MAX_STRING)) return;
        model.setAnswerDraft(id, chosen ? String(chosen.value) : "", text);
        emitState();
      }
      radiosIn(q).forEach(function (r) { r.addEventListener("change", saveAnswerDraft); });
      if (comment) comment.addEventListener("input", saveAnswerDraft);

      queueBtn.addEventListener("click", function () {
        var chosen = checkedIn(q);
        if (!chosen) {
          stateText.textContent = "Pick an option first.";
          return;
        }
        var answer = {
          questionId: id,
          question: questionText(q),
          optionValue: String(chosen.value),
          optionLabel: optionLabel(chosen),
          comment: comment ? comment.value.trim() : ""
        };
        var tooLong = [];
        if (answer.comment.length > MAX_STRING) tooLong.push("the comment");
        if (answer.question.length > MAX_STRING) tooLong.push("the question text");
        if (answer.optionValue.length > MAX_STRING) tooLong.push("the option's value");
        if (answer.questionId.length > MAX_STRING) tooLong.push("the question id");
        if (tooLong.length) {
          stateText.textContent = "Not queued: " + tooLong.join(" and ") + " is longer than " + MAX_STRING + " characters.";
          return;
        }
        model.queueAnswer(answer);
        model.clearAnswerDraft(id);
        changed();
      });
    });

    function renderQuestionStates(s) {
      questionStates.forEach(function (stateText, id) {
        var queued = null;
        var sent = null;
        for (var i = 0; i < s.queued.length; i++) if (s.queued[i].questionId === id) queued = s.queued[i];
        for (var j = 0; j < s.sent.length; j++) if (s.sent[j].questionId === id) sent = s.sent[j];
        if (queued && queued.pending) stateText.textContent = "Sent: " + queued.optionLabel + " - waiting for the app to confirm it has this.";
        else if (queued && queued.statusLabel) stateText.textContent = "Not sent: " + queued.optionLabel + " - " + queued.statusLabel;
        else if (queued) stateText.textContent = "Queued: " + queued.optionLabel + " - press Send in the notes tray.";
        else if (sent) stateText.textContent = "Sent: " + sent.optionLabel + " - " + sent.statusLabel;
        else stateText.textContent = "";
      });
    }

    // After a restore, put back what the inputs showed. Queueing an answer clears that question's draft, so a
    // draft that exists is newer than any queued answer and wins; otherwise the queued answer is shown.
    function applyAnswersToInputs() {
      var drafts = model.snapshot().answerDrafts;
      questions.forEach(function (q) {
        var id = q.getAttribute("data-dev-report-question");
        var comment = commentIn(q);
        var fill = null;
        for (var i = 0; i < drafts.length; i++) if (drafts[i].questionId === id) fill = drafts[i];
        if (!fill) fill = model.queuedAnswerFor(id);
        if (!fill) return;
        if (fill.optionValue !== "") radiosIn(q).forEach(function (r) { r.checked = String(r.value) === fill.optionValue; });
        if (comment) comment.value = fill.comment;
      });
    }

    // --- scroll ---------------------------------------------------------------------------------
    win.addEventListener("scroll", function () {
      if (scrollTimer !== null) return;
      scrollTimer = win.setTimeout(function () {
        scrollTimer = null;
        model.setScroll(win.scrollX || 0, win.scrollY || 0);
        emitState();
      }, 250);
    });

    // --- host messages --------------------------------------------------------------------------
    function onHostMessage(event) {
      var message = parseInbound(event.data);
      if (!message) return;
      if (message.type === "restore") {
        hosted = true;
        payloadBox.setAttribute("hidden", "");
        model.restore(message.payload.state);
        applyAnswersToInputs();
        render();
        var scroll = message.payload.state.scroll;
        win.scrollTo(scroll.x, scroll.y);
        if (message.payload.state.draft) setOpen(true);
      } else if (message.type === "status") {
        model.applyStatus(message.payload.updates);
        changed();
      } else if (message.type === "reply") {
        model.addReply(message.payload.reply);
        setOpen(true);
        changed();
      }
    }

    render();
    if (channel) {
      port.onmessage = onHostMessage;
      var ready = envelope("ready", { questionIds: questions.map(function (q) { return q.getAttribute("data-dev-report-question"); }) });
      win.parent.postMessage(ready, "*", [channel.port2]);
    }

    return {
      model: model,
      isHosted: function () { return hosted; },
      lastPayload: function () { return lastPayload; }
    };
  }

  var api = {
    channel: CHANNEL,
    version: VERSION,
    selectorFor: selectorFor,
    escapeIdent: escapeIdent,
    anchorFor: anchorFor,
    tableCellAnchor: tableCellAnchor,
    svgPartAnchor: svgPartAnchor,
    textSelectionAnchor: textSelectionAnchor,
    parseInbound: parseInbound,
    validState: validState,
    validItem: validItem,
    started: STARTED,
    createModel: createModel,
    start: start
  };
  root.DevReportNotes = api;

  // Starts itself when injected into a report. A test page sets DEV_REPORT_NOTES_MANUAL_START first.
  // Compared with true, because a report element with that id would otherwise be a truthy window property.
  if (root.document && root.DEV_REPORT_NOTES_MANUAL_START !== true) {
    if (root.document.readyState === "loading") {
      root.document.addEventListener("DOMContentLoaded", function () { start({ window: root }); });
    } else {
      start({ window: root });
    }
  }
})(typeof window !== "undefined" ? window : globalThis);
