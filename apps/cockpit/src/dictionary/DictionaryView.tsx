import { useCallback, useEffect, useRef, useState } from "react";
import { useBlocker } from "react-router-dom";
import {
  getDictionary,
  saveDictionary,
  getSuggestions,
  scanSuggestions,
  applySuggestions,
  dismissSuggestion,
  getDismissed,
  restoreDismissed,
  type Dictionary,
  type DictionarySuggestion,
  type DismissedTerm,
  type SuggestionsResult,
} from "@devthrottle/client-core/dictation/dictionaryClient";
import {
  addMistranscriptionTerm,
  addMistranscriptionVariant,
  addVocabularyWord,
} from "@devthrottle/client-core/dictation/dictionaryEdits";
import { ConfirmDialog } from "../components";
import { suggestionsChanged } from "./useSuggestionCount";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-dictionary";

// The dictation Dictionary editor (issue #977, epic #967) - the React port of the Blazor Cockpit
// Dictionary.razor (#183). The human edits the vocabulary chips and the common-mistranscriptions
// term-to-variant map. BOTH are fed to the cleanup pass and NEITHER is ever sent to the
// speech-to-text provider: the transcriber gets audio only, and the listed words are substituted
// afterwards on the finished transcript (issue 2481). Edited with a dirty-state Save that
// PUTs the whole glossary and re-renders the returned dictionary. It reads and writes same-origin
// through the Gateway front door (client-core) - never a Director address.
//
// The Gateway is the single source of truth for this glossary: it is used by phone-recording
// transcription and by live dictation/Speak on every Director connected to this Gateway.
// The "Last scan: ..." label, in the viewer's local time. Null means no scan has ever run.
function scanLabel(scannedAtUtc: string | null): string {
  if (scannedAtUtc === null) return "Never scanned";
  const when = new Date(scannedAtUtc);
  if (Number.isNaN(when.getTime())) return "Never scanned";
  return `Last scan: ${when.toLocaleString()}`;
}

export function DictionaryView() {
  const [dict, setDict] = useState<Dictionary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveMsg, setSaveMsg] = useState("");

  const [newVocab, setNewVocab] = useState("");
  const [newTerm, setNewTerm] = useState("");
  // Per-term draft text for the "+ wrong spelling" inputs, keyed by term.
  const [variantDrafts, setVariantDrafts] = useState<Record<string, string>>({});

  // Rejection notices shown next to each add box (issue #1255): a duplicate no longer silently clears
  // the input - it says so here, and the notice clears the moment the person edits that box again.
  const [vocabNotice, setVocabNotice] = useState("");
  const [termNotice, setTermNotice] = useState("");
  const [variantNotices, setVariantNotices] = useState<Record<string, string>>({});

  // ---- suggestions (devthrottle #2075, redesigned in #2115) ----
  // The stored result of the latest scan (daily per tenant, or the Scan-now button). The client is dumb:
  // it renders the list, the evidence, the count, the scan time, and the screening state exactly as the
  // Gateway computed them.
  const [suggestions, setSuggestions] = useState<DictionarySuggestion[]>([]);
  // When the latest scan ran (null = no scan has ever run), and the Gateway-ruled screening state.
  const [scannedAtUtc, setScannedAtUtc] = useState<string | null>(null);
  const [screeningOk, setScreeningOk] = useState(true);
  const [screeningError, setScreeningError] = useState("");
  // Which suggested terms are ticked (pre-ticked, since the screened list is already high-confidence).
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [suggestBusy, setSuggestBusy] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [suggestMsg, setSuggestMsg] = useState("");
  // The dismissed terms and whether the (initially collapsed) Dismissed section is open.
  const [dismissed, setDismissed] = useState<DismissedTerm[]>([]);
  const [showDismissed, setShowDismissed] = useState(false);

  const clearMsgTimer = useRef<number | null>(null);

  // Guard against losing unsaved edits on navigation (issue #1255). useBlocker intercepts in-app route
  // changes (for example choosing another Settings tab) while there are unsaved edits, so the person is asked
  // before the page unmounts and the edits vanish. The browser's own beforeunload covers a tab close or
  // refresh, which React Router cannot intercept.
  const blocker = useBlocker(dirty);
  // ConfirmDialog calls onClose right after a successful (synchronous) onConfirm. For the navigation
  // guard that would fire blocker.reset() immediately after blocker.proceed() and cancel the very
  // navigation we just allowed. This flag lets that trailing onClose no-op when we are proceeding, so
  // only a real cancel/dismiss resets the blocker.
  const proceedingRef = useRef(false);
  useEffect(() => {
    if (!dirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // A non-empty returnValue is what triggers the browser's native "leave site?" prompt.
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [dirty]);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const d = await getDictionary();
        if (!cancelled) setDict(d);
      } catch (err) {
        if (!cancelled) setError(describeAndReport(SURFACE, "load the dictionary", err));
      }
    })();
    return () => {
      cancelled = true;
      if (clearMsgTimer.current !== null) window.clearTimeout(clearMsgTimer.current);
    };
  }, []);

  // Fold one scan result (a fresh read or a Scan-now response) into the panel state. Pre-tick every
  // suggested term - the screened list is already high-confidence, so the happy path is one press -
  // while preserving any explicit user un-ticks for terms still present.
  const applyScanResult = useCallback((result: SuggestionsResult) => {
    setSuggestions(result.suggestions);
    setScannedAtUtc(result.scannedAtUtc);
    setScreeningOk(result.screeningOk);
    setScreeningError(result.screeningError); // error-report-exempt: the Gateway's own verdict on its screening step, carried in its answer; nothing failed here
    setSelected((prev) => {
      const next: Record<string, boolean> = {};
      for (const s of result.suggestions) next[s.term] = prev[s.term] ?? true;
      return next;
    });
  }, []);

  // Load the stored suggestions and dismissed terms separately from the glossary (devthrottle #2075).
  // A failure here must not break the glossary editor - the panel just stays empty - so it is swallowed.
  const refreshSuggestions = useCallback(async () => {
    try {
      applyScanResult(await getSuggestions());
    } catch {
      /* leave the panel empty if suggestions cannot be loaded */
    }
  }, [applyScanResult]);

  const refreshDismissed = useCallback(async () => {
    try {
      setDismissed(await getDismissed());
    } catch {
      /* leave dismissed list empty on failure */
    }
  }, []);

  useEffect(() => {
    void refreshSuggestions();
    void refreshDismissed();
  }, [refreshSuggestions, refreshDismissed]);

  const markDirty = useCallback(() => {
    setDirty(true);
    setSaveMsg("");
  }, []);

  // ---- vocabulary ----
  const addVocab = () => {
    if (dict === null) return;
    const result = addVocabularyWord(dict, newVocab);
    if (result.status === "added") {
      setDict(result.dict);
      setNewVocab("");
      setVocabNotice("");
      markDirty();
    } else if (result.status === "duplicate") {
      // Keep the text so the person sees exactly what was rejected, and say why.
      setVocabNotice(`"${result.word}" is already in the vocabulary.`);
    } else {
      // Empty input - nothing to add and nothing to announce; just clear the box.
      setNewVocab("");
      setVocabNotice("");
    }
  };

  const removeVocab = (i: number) => {
    if (dict === null || i < 0 || i >= dict.vocabulary.length) return;
    setDict({ ...dict, vocabulary: dict.vocabulary.filter((_, idx) => idx !== i) });
    markDirty();
  };

  // ---- mistranscriptions ----
  const addTerm = () => {
    if (dict === null) return;
    const result = addMistranscriptionTerm(dict, newTerm);
    if (result.status === "added") {
      setDict(result.dict);
      setNewTerm("");
      setTermNotice("");
      markDirty();
    } else if (result.status === "duplicate") {
      setTermNotice(`"${result.word}" is already a term.`);
    } else {
      setNewTerm("");
      setTermNotice("");
    }
  };

  const removeTerm = (term: string) => {
    if (dict === null) return;
    const next = { ...dict.commonMistranscriptions };
    delete next[term];
    setDict({ ...dict, commonMistranscriptions: next });
    setVariantDrafts((d) => {
      const copy = { ...d };
      delete copy[term];
      return copy;
    });
    setVariantNotices((n) => {
      const copy = { ...n };
      delete copy[term];
      return copy;
    });
    markDirty();
  };

  const setVariantNotice = (term: string, value: string) =>
    setVariantNotices((n) => ({ ...n, [term]: value }));

  const setVariantDraft = (term: string, value: string) => {
    setVariantDrafts((d) => ({ ...d, [term]: value }));
    // Editing the box clears any stale duplicate notice for that term.
    setVariantNotices((n) => (n[term] ? { ...n, [term]: "" } : n));
  };

  const addVariant = (term: string) => {
    if (dict === null) return;
    const result = addMistranscriptionVariant(dict, term, variantDrafts[term] ?? "");
    if (result.status === "added") {
      setDict(result.dict);
      setVariantDrafts((d) => ({ ...d, [term]: "" }));
      setVariantNotice(term, "");
      markDirty();
    } else if (result.status === "duplicate") {
      setVariantNotice(term, `"${result.variant}" is already listed.`);
    } else {
      // Empty input or a term that vanished under the page - clear the box, nothing to announce.
      setVariantDrafts((d) => ({ ...d, [term]: "" }));
      setVariantNotice(term, "");
    }
  };

  const removeVariant = (term: string, vi: number) => {
    if (dict === null) return;
    const variants = dict.commonMistranscriptions[term];
    if (variants === undefined || vi < 0 || vi >= variants.length) return;
    setDict({
      ...dict,
      commonMistranscriptions: {
        ...dict.commonMistranscriptions,
        [term]: variants.filter((_, idx) => idx !== vi),
      },
    });
    markDirty();
  };

  // ---- suggestions (devthrottle #2075 / #2115) ----
  const selectedCount = suggestions.filter((s) => selected[s.term]).length;

  // Run a scan NOW: mine the stored transcripts and have the screening model judge any new candidates.
  // The scheduled scan runs daily just after midnight in this account's time zone; this button is for
  // "I just dictated new vocabulary all day and want the suggestions now".
  const scanNow = async () => {
    setScanning(true);
    setSuggestMsg("Scanning your recent dictations...");
    try {
      applyScanResult(await scanSuggestions());
      setSuggestMsg("");
    } catch (err) {
      setSuggestMsg(`scan failed: ${describeAndReport(SURFACE, "scan your recent dictations", err)}`);
    } finally {
      setScanning(false);
    }
  };

  const toggleSelected = (term: string) =>
    setSelected((prev) => ({ ...prev, [term]: !prev[term] }));

  // Add every ticked suggestion to the glossary in one press: the term into Vocabulary and its wrong
  // spellings into Common mistranscriptions. The server persists it and returns the updated glossary and
  // the remaining suggestions, so this is NOT part of the manual-edit dirty buffer.
  const applySelected = async () => {
    const terms = suggestions.filter((s) => selected[s.term]).map((s) => s.term);
    if (terms.length === 0) return;
    setSuggestBusy(true);
    setSuggestMsg("Adding...");
    try {
      const result = await applySuggestions(terms);
      suggestionsChanged();
      setDict(result.dictionary);
      setSuggestions(result.suggestions);
      setSuggestMsg(
        `Added ${result.applied.length} term${result.applied.length === 1 ? "" : "s"} to your dictionary.`,
      );
      if (clearMsgTimer.current !== null) window.clearTimeout(clearMsgTimer.current);
      clearMsgTimer.current = window.setTimeout(() => setSuggestMsg(""), 4000);
    } catch (err) {
      setSuggestMsg(`add failed: ${describeAndReport(SURFACE, "add the suggested words", err)}`);
    } finally {
      setSuggestBusy(false);
    }
  };

  // Dismiss one term: stop suggesting it (remembered until restored). Removed from the panel at once and
  // added to the dismissed list.
  const dismissTerm = async (term: string) => {
    setSuggestBusy(true);
    try {
      await dismissSuggestion(term);
      suggestionsChanged();
      await Promise.all([refreshSuggestions(), refreshDismissed()]);
    } catch (err) {
      setSuggestMsg(`dismiss failed: ${describeAndReport(SURFACE, "dismiss the suggestion", err)}`);
    } finally {
      setSuggestBusy(false);
    }
  };

  // Restore a dismissed term: it becomes eligible again, reappearing only when the mining pass next ranks
  // it (so nothing may change on the page immediately - the hint copy says exactly that).
  const restoreTerm = async (term: string) => {
    setSuggestBusy(true);
    try {
      await restoreDismissed(term);
      suggestionsChanged();
      await Promise.all([refreshSuggestions(), refreshDismissed()]);
    } catch (err) {
      setSuggestMsg(`restore failed: ${describeAndReport(SURFACE, "restore the suggestion", err)}`);
    } finally {
      setSuggestBusy(false);
    }
  };

  // ---- save ----
  const save = async () => {
    if (dict === null || !dirty) return;
    setSaving(true);
    setSaveMsg("saving...");
    try {
      const fresh = await saveDictionary(dict);
      setDict(fresh);
      setDirty(false);
      setSaveMsg("Saved");
      setVariantDrafts({});
      if (clearMsgTimer.current !== null) window.clearTimeout(clearMsgTimer.current);
      clearMsgTimer.current = window.setTimeout(() => setSaveMsg((m) => (m === "Saved" ? "" : m)), 4000);
    } catch (err) {
      setSaveMsg(`save failed: ${describeAndReport(SURFACE, "save the dictionary", err)}`);
    } finally {
      setSaving(false);
    }
  };

  const terms = dict === null ? [] : Object.keys(dict.commonMistranscriptions);

  return (
    <div className="dc-root">
      <div className="dc-wrap">
        <div className="dc-top">
          <h1>Dictionary</h1>
        </div>
        <p className="dc-sub">
          The Gateway is the single source of truth for this glossary. It is used by{" "}
          <strong>phone-recording transcription</strong> and by{" "}
          <strong>live dictation/Speak on every Director</strong> connected to this Gateway. Edits apply
          on the next recording or dictation - no file copying.
        </p>

        {dict === null ? (
          <p className="dc-loading">{error ?? "Loading dictionary..."}</p>
        ) : (
          <>
            <div className="dc-saverow">
              <div />
              <div className="dc-saveactions">
                <span className="dc-savemsg">{saveMsg}</span>
                <button className="dc-save" onClick={() => void save()} disabled={!dirty || saving}>
                  Save
                </button>
              </div>
            </div>

            {/* ---- Suggestions (devthrottle #2075) ---- */}
            {suggestions.length > 0 ? (
              <div className="dc-section dc-suggest">
                <div className="dc-suggest-head">
                  <span className="dc-dot" />
                  <h2>
                    {suggestions.length} suggestion{suggestions.length === 1 ? "" : "s"} from your recent
                    dictations
                  </h2>
                </div>
                <p className="dc-hint">
                  We scanned your recent dictations for distinctive terms the speech model keeps
                  misspelling, and screened out ordinary words. Nothing changes until you press Add.
                </p>

                <div className="dc-scanrow">
                  <span className="dc-fineprint">{scanLabel(scannedAtUtc)}</span>
                  <button
                    className="dc-textlink"
                    type="button"
                    disabled={scanning || suggestBusy}
                    onClick={() => void scanNow()}
                  >
                    {scanning ? "Scanning..." : "Scan now"}
                  </button>
                </div>

                {!screeningOk && (
                  <p className="dc-notice" role="status">
                    The screening service could not be reached on the last scan, so new candidates are not
                    shown yet ({screeningError}). The terms below were screened earlier.
                  </p>
                )}

                {suggestions.map((s) => (
                  <div className="dc-srow" key={s.term}>
                    <input
                      type="checkbox"
                      checked={selected[s.term] ?? false}
                      onChange={() => toggleSelected(s.term)}
                      aria-label={`Add ${s.term}`}
                    />
                    <div>
                      <div className="dc-sterm">{s.term}</div>
                      <div className="dc-heard">
                        heard as{" "}
                        {s.variants.map((v, i) => (
                          <span key={`${v.heard}/${i}`}>
                            {i > 0 ? ", " : ""}
                            <b>{v.heard}</b>
                          </span>
                        ))}
                      </div>
                    </div>
                    <div className="dc-freq">
                      wrong {s.wrongCount} of {s.totalCount} times
                    </div>
                    <button
                      className="dc-row-dismiss"
                      type="button"
                      disabled={suggestBusy}
                      onClick={() => void dismissTerm(s.term)}
                    >
                      Dismiss
                    </button>
                  </div>
                ))}

                <div className="dc-suggest-actions">
                  <button
                    className="dc-add"
                    type="button"
                    disabled={suggestBusy || selectedCount === 0}
                    onClick={() => void applySelected()}
                  >
                    Add {selectedCount} selected to dictionary
                  </button>
                  <span className="dc-suggest-msg">{suggestMsg}</span>
                  <div className="dc-spacer" />
                  <span className="dc-fineprint">
                    Untick to skip a term this round. Dismiss to never see it again.
                  </span>
                </div>
              </div>
            ) : (
              <div className="dc-section">
                <div className="dc-quiet-line">
                  <span className="dc-tick">OK</span>
                  <span>
                    {scannedAtUtc === null
                      ? "No scan has run yet. A scan runs every night, or press Scan now."
                      : "No suggestions right now. We scan your dictations nightly for distinctive terms the model keeps getting wrong."}
                  </span>
                  <div className="dc-spacer" />
                  <span className="dc-fineprint">{scanLabel(scannedAtUtc)}</span>
                  <button
                    className="dc-textlink"
                    type="button"
                    disabled={scanning}
                    onClick={() => void scanNow()}
                  >
                    {scanning ? "Scanning..." : "Scan now"}
                  </button>
                  {dismissed.length > 0 && (
                    <button
                      className="dc-textlink"
                      type="button"
                      onClick={() => setShowDismissed((v) => !v)}
                    >
                      Dismissed terms ({dismissed.length})
                    </button>
                  )}
                </div>
                {!screeningOk && (
                  <p className="dc-notice" role="status">
                    The screening service could not be reached on the last scan, so new candidates are not
                    shown yet ({screeningError}).
                  </p>
                )}
                {suggestMsg !== "" && (
                  <p className="dc-notice" role="status">
                    {suggestMsg}
                  </p>
                )}
              </div>
            )}

            {/* When suggestions ARE present, keep the dismissed doorway reachable too. */}
            {suggestions.length > 0 && dismissed.length > 0 && (
              <div className="dc-dismiss-link-row">
                <button className="dc-textlink" type="button" onClick={() => setShowDismissed((v) => !v)}>
                  Dismissed terms ({dismissed.length})
                </button>
              </div>
            )}

            {/* ---- Dismissed terms (devthrottle #2075) ---- */}
            {showDismissed && (
              <div className="dc-section">
                <h2>Dismissed suggestions</h2>
                <p className="dc-hint">
                  Terms you told us to stop offering. Restore one and it becomes eligible again the next time
                  the evidence supports it.
                </p>
                {dismissed.length === 0 ? (
                  <p className="dc-empty">Nothing dismissed.</p>
                ) : (
                  dismissed.map((d) => (
                    <div className="dc-drow" key={d.term}>
                      <div>
                        <div className="dc-sterm">{d.term}</div>
                        <div className="dc-heard">
                          {d.variants.length > 0 ? (
                            <>
                              was heard as{" "}
                              {d.variants.map((v, i) => (
                                <span key={`${v.heard}/${i}`}>
                                  {i > 0 ? ", " : ""}
                                  <b>{v.heard}</b>
                                </span>
                              ))}
                              {d.totalCount > 0 ? ` - wrong ${d.wrongCount} of ${d.totalCount} times` : ""}
                            </>
                          ) : (
                            "dismissed"
                          )}
                        </div>
                      </div>
                      <button
                        className="dc-btn-restore"
                        type="button"
                        disabled={suggestBusy}
                        onClick={() => void restoreTerm(d.term)}
                      >
                        Restore
                      </button>
                    </div>
                  ))
                )}
              </div>
            )}

            {/* ---- Vocabulary ---- */}
            <div className="dc-section">
              <h2>Vocabulary</h2>
              <p className="dc-hint">Terms corrected to this spelling after transcription.</p>
              <div className="dc-chips">
                {dict.vocabulary.map((term, i) => (
                  <span className="dc-chip" key={`${term}/${i}`}>
                    {term}
                    <button className="dc-x" type="button" title="Remove" onClick={() => removeVocab(i)}>
                      x
                    </button>
                  </span>
                ))}
                <input
                  className="dc-addbox"
                  placeholder="+ add term"
                  value={newVocab}
                  onChange={(e) => {
                    setNewVocab(e.target.value);
                    if (vocabNotice !== "") setVocabNotice("");
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addVocab();
                  }}
                />
              </div>
              {vocabNotice !== "" && (
                <p className="dc-notice" role="status">
                  {vocabNotice}
                </p>
              )}
            </div>

            {/* ---- Common mistranscriptions ---- */}
            <div className="dc-section">
              <h2>Common mistranscriptions</h2>
              <p className="dc-hint">
                Correct term &larr; wrong spellings seen in practice. Fed to the cleanup pass.
              </p>
              <div>
                {terms.length === 0 ? (
                  <p className="dc-empty">No correction patterns yet.</p>
                ) : (
                  terms.map((term) => (
                    <div className="dc-mrow" key={term}>
                      <div className="dc-mterm">
                        <button className="dc-x" type="button" title="Remove term" onClick={() => removeTerm(term)}>
                          x
                        </button>
                        <span className="dc-name">{term}</span>
                        <span className="dc-arrow">&larr;</span>
                      </div>
                      <div className="dc-mvariants">
                        <div className="dc-chips">
                          {dict.commonMistranscriptions[term].map((variant, vi) => (
                            <span className="dc-chip" key={`${variant}/${vi}`}>
                              {variant}
                              <button
                                className="dc-x"
                                type="button"
                                title="Remove"
                                onClick={() => removeVariant(term, vi)}
                              >
                                x
                              </button>
                            </span>
                          ))}
                          <input
                            className="dc-addbox dc-variant-add"
                            placeholder="+ wrong spelling"
                            value={variantDrafts[term] ?? ""}
                            onChange={(e) => setVariantDraft(term, e.target.value)}
                            onKeyDown={(e) => {
                              if (e.key === "Enter") addVariant(term);
                            }}
                          />
                        </div>
                        {(variantNotices[term] ?? "") !== "" && (
                          <p className="dc-notice" role="status">
                            {variantNotices[term]}
                          </p>
                        )}
                      </div>
                    </div>
                  ))
                )}
              </div>
              <div className="dc-addterm-row">
                <input
                  className="dc-addbox dc-addterm"
                  placeholder="+ add a term to correct"
                  value={newTerm}
                  onChange={(e) => {
                    setNewTerm(e.target.value);
                    if (termNotice !== "") setTermNotice("");
                  }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") addTerm();
                  }}
                />
                {termNotice !== "" && (
                  <p className="dc-notice" role="status">
                    {termNotice}
                  </p>
                )}
              </div>
            </div>
          </>
        )}
      </div>

      {/* Warn before navigating away from unsaved dictionary edits (issue #1255). The blocker is armed
          only while `dirty` is true; confirming lets the pending navigation through, cancelling stays. */}
      <ConfirmDialog
        open={blocker.state === "blocked"}
        title="Leave with unsaved edits?"
        message="Your dictionary changes have not been saved. If you leave now they will be lost."
        confirmLabel="Leave without saving"
        cancelLabel="Stay on this page"
        onConfirm={() => {
          proceedingRef.current = true;
          blocker.proceed?.();
        }}
        onClose={() => {
          if (proceedingRef.current) {
            proceedingRef.current = false;
            return;
          }
          blocker.reset?.();
        }}
      />
    </div>
  );
}
