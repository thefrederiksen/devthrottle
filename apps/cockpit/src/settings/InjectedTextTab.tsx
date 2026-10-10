import { useEffect, useMemo, useState } from "react";
import {
  getInjectedText,
  setInjectedText,
  type InjectedText,
} from "@devthrottle/client-core/settings/injectedText";
import { bannerFor, fleetCommandsWarning, validateTemplate } from "./injectedTextState";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-injected-text";

// The "Injected text" tab: the whole of what DevThrottle puts in front of an agent at the start of a
// session, and the controls to read it, replace it, or go back to ours. The setting is GATEWAY-OWNED, so
// this one tab governs every machine the user runs a Director on. Responsive (CodingStyle.md): renders
// immediately with a loading line, loads asynchronously, and shows an explicit error banner on failure
// (the no-fallback rule - it never fabricates a state).
//
// YOURS OR OURS, NEVER A MERGE, and the one thing the user must never be wrong about is WHICH is live -
// so it is a full-width banner that changes colour, not a checkbox someone can misread.
//
// COCKPIT ONLY (issue #550). It was a navigation-rail entry of its own, sitting directly beneath
// Settings, which is the wrong shape for a setting. It is a tab now - but a tab the phone does not
// mount, because this is one fleet-wide block of text configured once at a desk, and editing it means
// working in a wide monospace editor with no honest phone form. The tab set carries that as
// `surface: "cockpit"` rather than each shell keeping its own list; see client-core/settings/tabs.ts.
//
// The layout was rebuilt in the same move. What it used to do wrong, and what replaced it:
//
//   - The read-only text sat in a short frame with BOTH scrollbars and clipped lines mid-sentence, so
//     reading one sentence meant scrolling sideways. It wraps now and is given real height.
//   - The two mode buttons sat loose, and the ACTIVE one was rendered disabled - so the page read as if
//     a button were broken rather than as if that mode were already on. They are a pair of selectable
//     cards now, the same shape as the AI tab's provider card, and the live one is marked as chosen.
//   - The placeholders were a wrapped run-on line of bracket tokens. They are a list, which is what they
//     are, and the one thing someone writing their own version actually has to read.

export function InjectedTextTab() {
  const [data, setData] = useState<InjectedText | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The editor surface. `editing` means the textarea is the active, editable "your text"; otherwise the
  // tab shows ours as a read-only preview. `draft` is what is in the textarea while editing.
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [showOurs, setShowOurs] = useState(false);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const loaded = await getInjectedText(controller.signal);
        setData(loaded);
        setEditing(loaded.useYours);
        setDraft(loaded.yours ?? "");
      } catch (e) {
        if (!controller.signal.aborted) setError(describeAndReport(SURFACE, "load the injected text", e));
      }
    })();
    return () => controller.abort();
  }, []);

  const dirty = data !== null && editing && draft !== (data.yours ?? "");
  const editingUnsaved = data !== null && editing && (dirty || !data.useYours);
  const templateProblem = editing ? validateTemplate(draft) : null;
  const fleetWarning = editing ? fleetCommandsWarning(draft) : null;
  const banner = useMemo(
    () => bannerFor(data?.useYours ?? false, editingUnsaved),
    [data?.useYours, editingUnsaved],
  );

  // A load failure has nothing to show, so the whole tab is the error. A SAVE failure is different: the
  // text the user just wrote is still on screen and must not be thrown away, so that error is rendered
  // beside the editor instead (below).
  if (error !== null && data === null) {
    return (
      <div className="settings-error" role="alert">
        {error}
      </div>
    );
  }

  if (data === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  async function apply(useYours: boolean, yours: string | null, note: string) {
    setBusy(true);
    setMsg(null);
    setError(null);
    try {
      const updated = await setInjectedText(useYours, yours);
      setData(updated);
      setEditing(updated.useYours);
      setDraft(updated.yours ?? "");
      setMsg(note);
    } catch (e) {
      setError(describeAndReport(SURFACE, "save the injected text", e));
    } finally {
      setBusy(false);
    }
  }

  function writeMyOwn() {
    // Seed a fresh custom text from the user's saved version if they have one, otherwise from ours as a
    // starting point they can edit. Nothing is saved until they click Save.
    setDraft(data!.yours ?? data!.ours);
    setEditing(true);
    setMsg(null);
  }

  function useOurs() {
    // Switching back while a draft is open would throw away text the user typed, with no undo and no
    // sign it happened. "Discard changes" is the button for deliberately dropping a draft; this one is
    // not, so it asks first. Only when there is something to lose.
    if (dirty && !window.confirm("Switch to the DevThrottle text and discard your unsaved changes?")) return;
    void apply(false, data!.yours, "Now running the DevThrottle text. Your version is kept.");
  }

  function discard() {
    setDraft(data!.yours ?? "");
    setEditing(data!.useYours);
    setMsg(null);
  }

  // Which card reads as chosen. While composing an unsaved version the CHOICE has not changed yet - the
  // agents are still receiving whatever was saved - so the live setting stays marked and the banner is
  // what says "not saved yet". Marking the draft as chosen would contradict the banner.
  const oursChosen = !data.useYours;

  return (
    <>
      <section className="settings-card">
        <h2 className="settings-h2">
          Injected text <span className="settings-pill">your account</span>
        </h2>
        <p className="settings-hint">
          When DevThrottle starts an agent, it gives the agent this text before your first message. It
          tells the agent which session it is and how to reach the other sessions in your fleet. It is
          handed over through each agent&apos;s own documented startup extension point - it is not typed
          into your terminal, and it does not touch anything you type. This is the whole of what
          DevThrottle adds at the start of a session, on every machine you run a Director on.
        </p>

        <div className={`itx-banner itx-banner-${banner.tone}`}>
          <div className="itx-banner-title">{banner.title}</div>
          <div className="itx-banner-detail">{banner.detail}</div>
        </div>

        {/* The mode choice, as a choice. Two cards, the live one marked - not two buttons with the
            active one greyed out, which reads as a broken control rather than as a current state. */}
        <div className="itx-modes" role="radiogroup" aria-label="Which text your agents receive">
          <button
            type="button"
            role="radio"
            aria-checked={oursChosen}
            className={oursChosen ? "itx-mode itx-mode-on" : "itx-mode"}
            disabled={busy}
            onClick={useOurs}
          >
            <span className="itx-mode-title">
              <span
                className={oursChosen ? "settings-provider-radio on" : "settings-provider-radio"}
                aria-hidden="true"
              />
              The DevThrottle text
            </span>
            <span className="itx-mode-desc">
              What we ship, kept up to date as DevThrottle changes. Your own version is kept, not deleted.
            </span>
          </button>

          <button
            type="button"
            role="radio"
            aria-checked={!oursChosen}
            className={!oursChosen ? "itx-mode itx-mode-on" : "itx-mode"}
            disabled={busy || editingUnsaved}
            onClick={writeMyOwn}
          >
            <span className="itx-mode-title">
              <span
                className={!oursChosen ? "settings-provider-radio on" : "settings-provider-radio"}
                aria-hidden="true"
              />
              My own version
            </span>
            <span className="itx-mode-desc">
              Start from a copy of ours and edit it. You stop receiving our updates to this text - that is
              the trade.
            </span>
            {/* This card is disabled while a draft is open, so that clicking it again cannot re-seed the
                editor over text the user has typed. A greyed control with no reason given is the exact
                thing that made the old page read as broken, so the reason is on the card. */}
            {editingUnsaved && (
              <span className="itx-mode-note">Draft open - save or discard it below.</span>
            )}
          </button>
        </div>
      </section>

      <section className="settings-card">
        <div className="itx-editor-head">
          <h2 className="settings-h2">{editing ? "Your text" : "The DevThrottle text"}</h2>
          {editing && (
            <button type="button" className="settings-btn" onClick={() => setShowOurs((v) => !v)}>
              {showOurs ? "Hide the DevThrottle text" : "Compare with the DevThrottle text"}
            </button>
          )}
        </div>
        {!editing && (
          <p className="settings-hint">Read-only. Choose &quot;My own version&quot; above to edit it.</p>
        )}

        <div className={`itx-panes ${showOurs ? "itx-panes-split" : ""}`}>
          <div className="itx-pane">
            {showOurs && <div className="itx-pane-label">Yours</div>}
            <textarea
              className="itx-editor"
              spellCheck={false}
              readOnly={!editing}
              aria-label={editing ? "Your text" : "The DevThrottle text, read-only"}
              value={editing ? draft : data.ours}
              onChange={(e) => setDraft(e.target.value)}
            />
          </div>

          {showOurs && (
            <div className="itx-pane">
              <div className="itx-pane-label">The DevThrottle text</div>
              <textarea
                className="itx-editor"
                spellCheck={false}
                readOnly
                aria-label="The DevThrottle text, read-only"
                value={data.ours}
              />
            </div>
          )}
        </div>

        {error !== null && (
          <p className="itx-problem" role="alert">
            {error}
          </p>
        )}
        {templateProblem !== null && (
          <p className="itx-problem" role="alert" /* error-report-exempt: input check, the template the user is typing is not valid yet */>
            {templateProblem}
          </p>
        )}
        {fleetWarning !== null && templateProblem === null && <p className="itx-warn">{fleetWarning}</p>}

        <div className="itx-footer">
          {msg !== null && <span className="itx-msg">{msg}</span>}
          {editing && dirty && (
            <button type="button" className="settings-btn" disabled={busy} onClick={discard}>
              Discard changes
            </button>
          )}
          {editing && (
            <button
              type="button"
              className="settings-btn primary"
              disabled={busy || templateProblem !== null || (!dirty && data.useYours)}
              onClick={() => void apply(true, draft, "Saved. Your agents now receive your text.")}
            >
              {busy ? "Saving..." : "Save my version"}
            </button>
          )}
        </div>
      </section>

      {/* The placeholders, as a list. They were a wrapped run-on line of bracket tokens, and they are the
          one piece a person writing their own version actually has to read. */}
      <section className="settings-card">
        <h2 className="settings-h2">Placeholders you can use</h2>
        <p className="settings-hint">
          These stay editable wherever you put them, and are filled in for each session as it starts.
        </p>
        {data.placeholders.length === 0 ? (
          <p className="settings-hint settings-hint-inline">This Gateway lists no placeholders.</p>
        ) : (
          <ul className="itx-placeholders">
            {data.placeholders.map((p) => (
              <li key={p}>
                <code>{p}</code>
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
