// Now - the live stop of one session, and only the live stop (the Wingman tab, version 3, item 3). This is the view
// the owner sits on instead of the terminal, so it is never blank: all eleven states are drawn.
//
// THIS VIEW DECIDES NOTHING. It renders the strings and flags of one WingmanNow object, in the order below, showing
// each piece exactly when the Gateway sent it. There is no branch on what a state MEANS anywhere in this file - no
// `state === "done"`, no colour rule, no wording of its own beyond fixed chrome (the buttons, the three small
// headings, and the clock and "ago" phrasing the Gateway cannot write because it does not know the reader's time
// zone). Adding a state to the product is a change in the
// Gateway's fold, not here (product CLAUDE.md rule 7).
//
// WHICH CONTROLS BELONG ON A STATE IS DECIDED THE SAME WAY, by ABSENCE rather than by a branch: the shell passes a
// snooze or a wake or neither, and a close or not, and an action with no handler is not drawn. The words on those
// buttons are chrome like every other button here; WHEN each is offered is the shell's, reading what the Gateway
// stamped on the session - never a rule written in this file.
//
// Lives in client-core so the shell stays thin; only the Cockpit mounts it (the owner's ruling - not the phone).
import { useState } from "react";
import type { ReactNode } from "react";
import type { WingmanNow as WingmanNowDto, WingmanNowPast, WingmanNowWhen } from "./wingmanNowRead";
import "./wingmanNow.css";

/** A UTC instant as a clock time in the reader's own zone: "11:12 AM". */
export function formatClockTime(utc: string): string {
  return new Date(utc).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

/** How long has passed since an instant, in plain words: "8 minutes". Always whole units, never rounded up past one. */
export function formatElapsed(utc: string, now: Date): string {
  const seconds = Math.max(0, Math.floor((now.getTime() - new Date(utc).getTime()) / 1000));
  const say = (n: number, unit: string) => `${n} ${unit}${n === 1 ? "" : "s"}`;
  if (seconds < 60) return say(seconds, "second");
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return say(minutes, "minute");
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return say(hours, "hour");
  return say(Math.floor(hours / 24), "day");
}

/** How long ago an instant was, in plain words: "8 minutes ago". */
export function formatAgo(utc: string, now: Date): string {
  return `${formatElapsed(utc, now)} ago`;
}

/**
 * The Gateway's timed sentence, finished with the reader's local clock: "Stopped at 11:12 AM, 8 minutes ago".
 *
 * TWO SHAPES, AND THE GATEWAY NAMES WHICH. `elapsedOnly` means the sentence is the elapsed time on its own -
 * "Working for 6 minutes" - and the clock time is not shown at all. A session spends most of its life in that
 * shape, and before the flag was read here it rendered as "Working for 7:14 a.m.", which says nothing true. The
 * view still decides no words: it is told which shape this is rather than inferring it from the state.
 */
export function formatWhen(when: WingmanNowWhen, now: Date): string {
  if (when.elapsedOnly) return `${when.lead} ${formatElapsed(when.atUtc, now)}`;
  const head = `${when.lead} ${formatClockTime(when.atUtc)}`;
  return when.showAgo ? `${head}, ${formatAgo(when.atUtc, now)}` : head;
}

/**
 * What a write action did, as the shell that made the call read it off the Gateway.
 *
 * `message` is the GATEWAY'S OWN SENTENCE about what happened - the answer route's refusal, the prompt route's
 * failure - and it is shown verbatim, never edited and never replaced by a sentence written here. An empty message
 * means the Gateway said nothing worth showing, so nothing is drawn.
 *
 * `accepted` is what the owner's typed words depend on: the reply box keeps them until a send is accepted, so a
 * refusal never destroys what he wrote.
 *
 * EVERY ACTION BELOW RESOLVES, AND NONE OF THEM REJECTS. The shell catches the failure, because the shell is where
 * the Gateway's sentence can be read; a rejected promise here would be the swallowed failure this channel exists to
 * end.
 */
export interface WingmanNowOutcome {
  accepted: boolean;
  message: string;
}

/** Everything the Now view can do. Each is wired by the shell that mounts it; an action with no handler is not drawn. */
export interface WingmanNowActions {
  /** Open Debug on the colour rules for this row. */
  onWhyColour?: () => void;
  /** Play the narration the Gateway has already made. */
  onPlayVoice?: () => Promise<WingmanNowOutcome>;
  /** Turn voice mode on for this session from here. */
  onTurnOnVoice?: () => Promise<WingmanNowOutcome>;
  /** Snooze the session. Passed only while the session is not already snoozed, so the button never does nothing. */
  onSnooze?: () => Promise<WingmanNowOutcome>;
  /** Wake a snoozed session. Passed only while it IS snoozed - the other half of the same pair. */
  onUnsnooze?: () => Promise<WingmanNowOutcome>;
  /**
   * Close a session whose work is finished. Passed only where closing is the natural next step.
   *
   * It OPENS the shell's own stop question rather than closing anything, which is why it answers nothing: the
   * confirmation, the request and its failure all belong to the one owner the shell already mounts for them, and
   * that owner outlives this screen. A second confirmation here would be a second question for one act.
   */
  onClose?: () => void;
  onOpenTerminal?: () => void;
  /** Open the account settings where the Wingman is switched on. */
  onOpenSettings?: () => void;
  /** Go to another session in the account. */
  onGoToSession?: (sessionId: string) => void;
}

/**
 * Run one write action and keep what it answered, so the control that started it can say what happened.
 *
 * `busy` is true while the call is in flight - every control that starts one disables itself, so the owner cannot
 * fire the same answer twice while waiting.
 */
function useOutcome() {
  const [outcome, setOutcome] = useState<WingmanNowOutcome | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (action: () => Promise<WingmanNowOutcome>): Promise<WingmanNowOutcome> => {
    setBusy(true);
    setOutcome(null);
    try {
      const result = await action();
      setOutcome(result);
      return result;
    } finally {
      setBusy(false);
    }
  };
  return { outcome, busy, run };
}

/** What the Gateway said about the last write, verbatim: an alert when it was refused, a quiet line when it landed. */
function Outcome({ outcome }: { outcome: WingmanNowOutcome | null }) {
  if (outcome === null || outcome.message === "") return null;
  return (
    <p
      className={`wnow-outcome ${outcome.accepted ? "wnow-outcome-done" : "wnow-outcome-refused"}`}
      role={outcome.accepted ? "status" : "alert"}
    >
      {outcome.message}
    </p>
  );
}

/**
 * The ONE place on this tab where the owner types (the review's item B1).
 *
 * The box is not built here. The shell that mounts Now supplies it, so the Wingman tab and the page's own composer
 * are the SAME control rather than two boxes with two Send buttons on one screen - which is what shipped, with the
 * one beside the question drawn as the quiet button and the one at the bottom of the page drawn as the loud one.
 *
 * The GATEWAY still decides whether there is a box at all and what is written inside it: this is drawn exactly where
 * `replyPlaceholder` arrived, and the placeholder is handed straight to the shell. A state the Gateway gave no
 * placeholder has no reply box, the same as before.
 */
export type WingmanNowReplyBox = (placeholder: string, state: string) => ReactNode;

export function WingmanNow({
  now,
  at = new Date(),
  actions = {},
  replyBox,
}: {
  now: WingmanNowDto;
  /** The moment "ago" is measured from. A parameter so the phrasing is testable, not so anything decides on it. */
  at?: Date;
  actions?: WingmanNowActions;
  /** The shell's own message box, drawn wherever the Gateway offered a reply. Nothing is drawn without it. */
  replyBox?: WingmanNowReplyBox;
}) {
  const quick = useOutcome();
  const reply =
    now.replyPlaceholder != null && replyBox ? (
      <div className="wnow-reply">
        {/* The Gateway's placeholder AND the Gateway's state word, both handed over as they arrived. The shell
            offers one sending button per state and needs to know which state it is drawing a box for; nothing is
            decided here, and the shell reads the same word it already reads for the close. */}
        {replyBox(now.replyPlaceholder, now.state)}
        {/* WHAT SENDING DOES, beside the one box - the Gateway's sentence, drawn verbatim. It has been folded per
            state since the route merged and nothing rendered it, so the screen offered a box and said nothing about
            where its words would go. */}
        {now.replyHint != null && <p className="wnow-reply-hint">{now.replyHint}</p>}
      </div>
    ) : null;
  return (
    <div className="wnow">
      {/* WHICH SESSION THIS IS, FIRST, IN EVERY STATE. The Gateway has sent this line since the route merged and
          the view dropped it, so the pane never said whose stop was on it while the owner moved between a dozen
          sessions - and the answer he types here goes to that session. It is the Gateway's own words; the view
          only puts them first. */}
      {now.sessionLine ? <p className="wnow-session">{now.sessionLine}</p> : null}
      <Header now={now} at={at} actions={actions} />
      <div className="wnow-body">
        {now.switchedOff && (
          <section className="wnow-off" aria-label="The Wingman is switched off">
            <h2 className="wnow-headline wnow-headline-quiet">{now.switchedOff.headline}</h2>
            <p className="wnow-story">
              {now.switchedOff.story}{" "}
              {actions.onOpenSettings && (
                <button type="button" className="wnow-link" onClick={actions.onOpenSettings}>
                  {now.switchedOff.settingsLinkText}
                </button>
              )}
            </p>
          </section>
        )}

        {now.failedHeadline != null && <h2 className="wnow-headline wnow-headline-failed">{now.failedHeadline}</h2>}
        {now.failedStory != null && <p className="wnow-story">{now.failedStory}</p>}

        {/* WHEN IT COMES BACK, on the state that was two words and nothing else. The Gateway sends either this or a
            headline saying what ends the snooze, never both, so they share the line rather than competing for it. */}
        {now.snoozedUntil && <h2 className="wnow-headline">{formatWhen(now.snoozedUntil, at)}</h2>}
        {now.headline != null && <h2 className="wnow-headline">{now.headline}</h2>}
        {now.story != null && <p className="wnow-story">{now.story}</p>}

        {now.agentSaid && (
          <blockquote className="wnow-said">
            <span className="wnow-said-who">{now.agentSaid.who}</span>
            {now.agentSaid.text}
          </blockquote>
        )}
        {now.wholeReply != null && (
          <details className="wnow-full">
            <summary>The whole reply, word for word</summary>
            <pre>{now.wholeReply}</pre>
          </details>
        )}

        {now.lastWords && (
          <blockquote className="wnow-said">
            <span className="wnow-said-who">{now.lastWords.who}</span>
            {now.lastWords.text}
          </blockquote>
        )}

        {now.needs ? (
          <section className="wnow-card wnow-card-needs" aria-label={now.needs.heading}>
            <h3>{now.needs.heading}</h3>
            {now.unsure && now.unsureLine != null && <p className="wnow-unsure-line">{now.unsureLine}</p>}
            {/* NO ANSWER BUTTONS (the turn pipeline mission, phase 4): a menu is simply "needs you", the narration
                says to open the session to choose, and the reply box is the way to answer. */}
            {now.needs.question != null && <p className="wnow-question">{now.needs.question}</p>}
            {reply}
          </section>
        ) : (
          now.unsure && now.unsureLine != null && <p className="wnow-unsure-line">{now.unsureLine}</p>
        )}

        {now.calmCard && (
          <section
            /* The card's colour is the Gateway's own choice of tone, named not decided here: the approved mockup
               tints done and report cyan and carrying on purple. A card with no tone keeps the neutral one. */
            className={`wnow-card wnow-card-calm${now.calmCard.tone ? ` wnow-card-calm-${now.calmCard.tone}` : ""}`}
            aria-label={now.calmCard.heading}
          >
            <h3>{now.calmCard.heading}</h3>
            {now.calmCard.body != null && <p>{now.calmCard.body}</p>}
            {now.carryingOnDeadline && (
              <p>
                {now.carryingOnDeadline.before} {formatClockTime(now.carryingOnDeadline.atUtc)}
                {now.carryingOnDeadline.after}
              </p>
            )}
          </section>
        )}

        {now.lastAsked && (
          <section className="wnow-card wnow-card-asked" aria-label={now.lastAsked.heading}>
            {/* THE GATEWAY'S WORDS, not this file's. The heading and the words before the time both arrive
                finished - "at", or "You, at" - so the view joins the lead and the local clock and chooses neither
                the wording nor the punctuation. The words are not touched here either: a long ask is FOLDED, never
                shortened, because what was really sent is the point of the card. */}
            <h3>{now.lastAsked.heading}</h3>
            <Asked text={now.lastAsked.text} />
            <p className="wnow-when">
              {now.lastAsked.whenLead} {formatClockTime(now.lastAsked.atUtc)}
            </p>
          </section>
        )}

        {now.answered && (
          <section className="wnow-card wnow-card-answered" aria-label={now.answered.headline}>
            {/* THE WHOLE FIRST LINE, FINISHED, from the Gateway: "You answered: allow the merge". `answered.text`
                is the same words without that lead, so drawing both would say it twice. */}
            <h3>{now.answered.headline}</h3>
            <p className="wnow-when">
              {/* No full stop after the clock: some locales render it as "7:19 a.m.", which would double the stop.
                  THE CONFIRMATION IS LEGITIMATELY ABSENT when this Gateway cannot tell whether the session went
                  back to work, and then the sentence ends at the time - never at a dangling dash. */}
              {now.answered.sentLead} {formatClockTime(now.answered.atUtc)}
              {now.answered.workingAgainAfterText != null && ` - ${now.answered.workingAgainAfterText}`}
            </p>
          </section>
        )}

        {now.needs == null && reply}

        {now.nextNeedsYou && (
          <section className="wnow-next" aria-label="The next session that needs you">
            <span className="wnow-pill wnow-pill-next">
              <span className="wnow-dot" aria-hidden="true" />
              {now.nextNeedsYou.heading}
            </span>
            <b className="wnow-next-name">{now.nextNeedsYou.name}</b>
            {now.nextNeedsYou.label != null && <span className="wnow-next-label">{now.nextNeedsYou.label}</span>}
            {actions.onGoToSession && (
              <button
                type="button"
                className="wnow-btn"
                onClick={() => actions.onGoToSession?.(now.nextNeedsYou!.sessionId)}
              >
                {now.nextNeedsYou.linkText}
              </button>
            )}
          </section>
        )}

        {/* THE QUICK ACTIONS FIT THE STATE, and they do it by being ABSENT rather than by this file working out what
            a state means. The shell passes snooze OR wake, never both, and passes close only where closing is the
            next step - so a snoozed session is never offered "Snooze this session" again, which is what shipped and
            what nobody could predict the effect of (the review's item B3). */}
        {(actions.onSnooze || actions.onUnsnooze || actions.onClose || actions.onOpenTerminal) && (
          <div className="wnow-quick">
            {actions.onSnooze && (
              <QuickAction
                label="Snooze this session"
                busyLabel="Snoozing..."
                outcome={quick}
                run={actions.onSnooze}
              />
            )}
            {/* WAKE, not "unsnooze". The pair reads as one plain verb each way round, and it is the first button on
                a snoozed session - the one thing he came to that page to do. */}
            {actions.onUnsnooze && (
              <QuickAction
                label="Wake this session"
                busyLabel="Waking..."
                outcome={quick}
                run={actions.onUnsnooze}
              />
            )}
            {actions.onOpenTerminal && (
              <button type="button" className="wnow-btn" onClick={actions.onOpenTerminal}>
                Open the terminal
              </button>
            )}
            {/* THE ONLY BUTTON ON THIS SCREEN THAT DESTROYS ANYTHING, so it is last and it is set apart. It sat in
                the middle of the row drawn exactly like the two harmless buttons either side of it. It still asks
                before it closes - the shell's own stop question, which is the one owner of the confirmation. */}
            {actions.onClose && (
              <button type="button" className="wnow-btn wnow-btn-apart" onClick={actions.onClose}>
                Close this session
              </button>
            )}
            <Outcome outcome={quick.outcome} />
          </div>
        )}

        {now.lastStop && <Past past={now.lastStop} />}
        {now.lastGood && <Past past={now.lastGood} />}
      </div>
    </div>
  );
}

function Header({ now, at, actions }: { now: WingmanNowDto; at: Date; actions: WingmanNowActions }) {
  // THE SENTENCE ITSELF, not a flag that it was said. The Gateway writes afterTurnOnText only while the stop still
  // offers "turn voice on", so the moment the next refresh flips the voice kind, a flag would have nothing left to
  // render and the line would vanish from under the owner. Keeping the words keeps them on screen.
  const [saidOnTurnOn, setSaidOnTurnOn] = useState<string | null>(null);
  const voice = useOutcome();
  // THE CONTRACT SAYS THIS IS NEVER NULL - one of its kinds is "nothing to offer" - and the fold always writes it.
  // A Gateway that sent none anyway would take the whole screen down with it, and the Cockpit has no error boundary
  // to catch that, so the absence is drawn as the kind that draws no control.
  const voiceControl = now.voice ?? { kind: "none" as const, label: null, afterTurnOnText: null };
  return (
    <div className="wnow-head">
      {/* A TINTED FILL, not a thin ring, as the approved mockup draws it: at a glance the pill reads as a status
          rather than as another outlined control. The colour is still only ever the Gateway's own hex - the tint is
          mixed from that one value, so nothing here chooses a second colour. */}
      <span
        className="wnow-pill"
        style={{
          borderColor: now.pillColourHex ?? undefined,
          color: now.pillColourHex ?? undefined,
          background: now.pillColourHex ? `color-mix(in srgb, ${now.pillColourHex} 15%, transparent)` : undefined,
        }}
        title={now.pillColour ?? undefined}
      >
        <span className="wnow-dot" style={{ background: now.pillColourHex ?? undefined }} aria-hidden="true" />
        {now.pillText}
      </span>
      {now.unsure && now.unsureTag != null && <span className="wnow-tag">{now.unsureTag}</span>}
      {now.showWhyColour && actions.onWhyColour && (
        <button type="button" className="wnow-link" onClick={actions.onWhyColour}>
          Why this colour?
        </button>
      )}
      {now.when && <span className="wnow-when">{formatWhen(now.when, at)}</span>}
      {/* THE VOICE CONTROL SITS AT THE END OF THIS LINE, beside the time - not pushed to the far edge of the page.
          It was a bare green word a full screen width away from the words it reads out, so nothing on screen said
          what it would play (the review's item B8). The spacer that pushed it there is gone, and it carries a
          speaker, as the approved mockup draws it. The LABEL is still the Gateway's own, rendered verbatim. */}
      {voiceControl.kind === "play" && actions.onPlayVoice && (
        <button
          type="button"
          className="wnow-voice wnow-voice-play"
          disabled={voice.busy}
          onClick={() => {
            const play = actions.onPlayVoice;
            if (!play) return;
            void voice.run(() => play());
          }}
        >
          <Speaker />
          {voiceControl.label ?? "Play"}
        </button>
      )}
      {voiceControl.kind === "preparing" && (
        <span className="wnow-voice wnow-voice-preparing">
          <Speaker />
          {voiceControl.label}
        </span>
      )}
      {voiceControl.kind === "turn-on" && actions.onTurnOnVoice && (
        <button
          type="button"
          className="wnow-voice wnow-voice-turn-on"
          disabled={voice.busy}
          onClick={() => {
            const turnOn = actions.onTurnOnVoice;
            if (!turnOn) return;
            void voice.run(() => turnOn()).then((result) => {
              // The Gateway's own sentence about what turning voice on did to THIS stop, kept for as long as the
              // screen lives. Only a switch that was accepted says it happened.
              if (result.accepted) setSaidOnTurnOn(voiceControl.afterTurnOnText ?? null);
            });
          }}
        >
          <Speaker />
          {voiceControl.label}
        </button>
      )}
      {saidOnTurnOn != null && <span className="wnow-voice-said">{saidOnTurnOn}</span>}
      <Outcome outcome={voice.outcome} />
    </div>
  );
}

/** The speaker on the voice control, so the button says what it does before its words are read. */
function Speaker() {
  return (
    <svg className="wnow-voice-icon" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">
      <path d="M3 9v6h4l5 4V5L7 9H3zm13.5 3a4.5 4.5 0 0 0-2.5-4v8a4.5 4.5 0 0 0 2.5-4z" />
    </svg>
  );
}

/**
 * ABOUT HOW MANY LINES OF A LONG ASK ARE WORTH FOLDING AWAY.
 *
 * It is a LAYOUT measure, not a meaning: the words themselves are never touched, because what was really sent is
 * the whole point of the card. Either of the two is enough on its own - a prompt with four newlines in it, or one
 * unbroken paragraph long enough to run past three lines of the 820-pixel reading column. The clamp itself is the
 * stylesheet's; these only decide whether a fold is worth offering at all, so a one-line ask carries no control.
 */
const ASKED_LINES_SHOWN = 3;
const ASKED_CHARACTERS_SHOWN = 240;

/** True when this ask is long enough that folding it is worth a control. */
export function askIsLong(text: string): boolean {
  return text.split("\n").length > ASKED_LINES_SHOWN || text.length > ASKED_CHARACTERS_SHOWN;
}

/**
 * What the session was last asked, held to about three lines with the rest one click away (the review's item N4).
 *
 * A scheduled prompt is eight lines of file paths and command lines, and it was the whole page: the reply box slid
 * off the bottom of the screen with every long ask. The words are NOT shortened - a summary of what was really sent
 * would be this view writing the Gateway's sentence for it - they are folded, and the fold opens.
 */
function Asked({ text }: { text: string }) {
  const [showAll, setShowAll] = useState(false);
  if (!askIsLong(text)) return <p className="wnow-asked-text">{text}</p>;
  return (
    <>
      <p className={`wnow-asked-text${showAll ? "" : " wnow-asked-clamped"}`}>{text}</p>
      <button type="button" className="wnow-link wnow-asked-more" onClick={() => setShowAll(!showAll)}>
        {showAll ? "Show less of it" : "Show all of it"}
      </button>
    </>
  );
}

/** One button in the quick-actions row. The row shares one outcome line, so whichever ran last is the one reported. */
function QuickAction({
  label,
  busyLabel,
  outcome,
  run,
}: {
  label: string;
  busyLabel: string;
  outcome: ReturnType<typeof useOutcome>;
  run: () => Promise<WingmanNowOutcome>;
}) {
  return (
    <button type="button" className="wnow-btn" disabled={outcome.busy} onClick={() => void outcome.run(run)}>
      {outcome.busy ? busyLabel : label}
    </button>
  );
}

/** A stop marked as past, so it is never read as the live one. */
function Past({ past }: { past: WingmanNowPast }) {
  return (
    <p className="wnow-past">
      <b>
        {past.lead}, {formatClockTime(past.atUtc)}:
      </b>{" "}
      {past.text}
    </p>
  );
}
