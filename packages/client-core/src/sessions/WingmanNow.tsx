// Now - the live stop of one session, and only the live stop (the Wingman tab, version 3, item 3). This is the view
// the owner sits on instead of the terminal, so it is never blank: all ten states are drawn.
//
// THIS VIEW DECIDES NOTHING. It renders the strings and flags of one WingmanNow object, in the order below, showing
// each piece exactly when the Gateway sent it. There is no branch on what a state MEANS anywhere in this file - no
// `state === "done"`, no colour rule, no wording of its own beyond fixed chrome (the buttons, the three small
// headings, and the clock and "ago" phrasing the Gateway cannot write because it does not know the reader's time
// zone). Adding a state to the product is a change in the Gateway's fold, not here (product CLAUDE.md rule 7).
//
// Lives in client-core so the shell stays thin; only the Cockpit mounts it (the owner's ruling - not the phone).
import { useState } from "react";
import type {
  WingmanNow as WingmanNowDto,
  WingmanNowOption,
  WingmanNowPast,
  WingmanNowWhen,
} from "./wingmanNowRead";
import "./wingmanNow.css";

/** A UTC instant as a clock time in the reader's own zone: "11:12 AM". */
export function formatClockTime(utc: string): string {
  return new Date(utc).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

/** How long ago an instant was, in plain words: "8 minutes ago". Always whole units, never rounded up past one. */
export function formatAgo(utc: string, now: Date): string {
  const seconds = Math.max(0, Math.floor((now.getTime() - new Date(utc).getTime()) / 1000));
  const say = (n: number, unit: string) => `${n} ${unit}${n === 1 ? "" : "s"} ago`;
  if (seconds < 60) return say(seconds, "second");
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return say(minutes, "minute");
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return say(hours, "hour");
  return say(Math.floor(hours / 24), "day");
}

/** The Gateway's timed sentence, finished with the reader's local clock: "Stopped at 11:12 AM, 8 minutes ago". */
export function formatWhen(when: WingmanNowWhen, now: Date): string {
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
  /** Answer the stop by picking one of the Gateway's options. `verdictId` is the verdict those options belong to;
   *  it rides with the index on the answer route, and the view passes it along rather than looking it up. */
  onAnswerOption?: (option: WingmanNowOption, verdictId: string | null) => Promise<WingmanNowOutcome>;
  /** Send the owner's own words to the session. */
  onSendReply?: (text: string) => Promise<WingmanNowOutcome>;
  onSnooze?: () => Promise<WingmanNowOutcome>;
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

export function WingmanNow({
  now,
  at = new Date(),
  actions = {},
}: {
  now: WingmanNowDto;
  /** The moment "ago" is measured from. A parameter so the phrasing is testable, not so anything decides on it. */
  at?: Date;
  actions?: WingmanNowActions;
}) {
  const answer = useOutcome();
  const snooze = useOutcome();
  return (
    <div className="wnow">
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

        {now.headline != null && <h2 className="wnow-headline">{now.headline}</h2>}
        {now.story != null && <p className="wnow-story">{now.story}</p>}

        {now.agentSaid && (
          <blockquote className="wnow-said">
            <span className="wnow-said-who">{now.agentSaid.who}</span>
            {now.agentSaid.sentence}
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
            {now.needs.recommends != null && <p className="wnow-recommends">{now.needs.recommends}</p>}
            {now.needs.question != null && <p className="wnow-question">{now.needs.question}</p>}
            {now.needs.options.length > 0 && (
              <ul className="wnow-options">
                {now.needs.options.map((option) => (
                  <li key={option.index}>
                    <button
                      type="button"
                      className="wnow-option"
                      disabled={!now.canAnswerByOption || !actions.onAnswerOption || answer.busy}
                      onClick={() => {
                        const send = actions.onAnswerOption;
                        if (!send) return;
                        void answer.run(() => send(option, now.needs?.verdictId ?? null));
                      }}
                    >
                      <span className="wnow-option-index">{option.index}</span>
                      <span className="wnow-option-body">
                        <span className="wnow-option-key">
                          {option.key}
                          {option.recommended && <b className="wnow-option-mark">RECOMMENDED</b>}
                        </span>
                        {option.note != null && <span className="wnow-option-note">{option.note}</span>}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {/* What the answer route said about the last tap - its refusal sentence unedited, so an answer that
                did nothing never looks like an answer that landed. */}
            <Outcome outcome={answer.outcome} />
            <ReplyBox placeholder={now.replyPlaceholder} onSend={actions.onSendReply} />
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
                {now.carryingOnDeadline.before}
                {now.carryingOnDeadline.atUtc != null && ` ${formatClockTime(now.carryingOnDeadline.atUtc)}`}
                {now.carryingOnDeadline.after}
              </p>
            )}
          </section>
        )}

        {now.lastAsked && (
          <section className="wnow-card wnow-card-asked" aria-label="What it was last asked">
            <h3>What it was last asked</h3>
            <p className="wnow-asked-text">{now.lastAsked.text}</p>
            <p className="wnow-when">
              {now.lastAsked.by != null ? `${now.lastAsked.by}, at ` : "At "}
              {formatClockTime(now.lastAsked.atUtc)}
            </p>
          </section>
        )}

        {now.answered && (
          <section className="wnow-card wnow-card-answered" aria-label="What you answered">
            <h3>What you answered</h3>
            <p className="wnow-answered-text">{now.answered.text}</p>
            <p className="wnow-when">
              {/* No full stop after the clock: some locales render it as "7:19 a.m.", which would double the stop. */}
              Sent at {formatClockTime(now.answered.atUtc)} - {now.answered.workingAgainAfterText}
            </p>
          </section>
        )}

        {now.needs == null && <ReplyBox placeholder={now.replyPlaceholder} onSend={actions.onSendReply} />}

        {now.nextNeedsYou && (
          <section className="wnow-next" aria-label="The next session that needs you">
            <span className="wnow-pill wnow-pill-next">
              <span className="wnow-dot" aria-hidden="true" />
              Next that needs you
            </span>
            <b className="wnow-next-name">{now.nextNeedsYou.name}</b>
            <span className="wnow-next-label">{now.nextNeedsYou.label}</span>
            {actions.onGoToSession && (
              <button
                type="button"
                className="wnow-btn"
                onClick={() => actions.onGoToSession?.(now.nextNeedsYou!.sessionId)}
              >
                Go there
              </button>
            )}
          </section>
        )}

        {(actions.onSnooze || actions.onOpenTerminal) && (
          <div className="wnow-quick">
            {actions.onSnooze && (
              <button
                type="button"
                className="wnow-btn"
                disabled={snooze.busy}
                onClick={() => {
                  const hold = actions.onSnooze;
                  if (!hold) return;
                  void snooze.run(() => hold());
                }}
              >
                {snooze.busy ? "Snoozing..." : "Snooze this session"}
              </button>
            )}
            {actions.onOpenTerminal && (
              <button type="button" className="wnow-btn" onClick={actions.onOpenTerminal}>
                Open the terminal
              </button>
            )}
            <Outcome outcome={snooze.outcome} />
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
  const voiceControl = now.voice;
  return (
    <div className="wnow-head">
      <span
        className="wnow-pill"
        style={{ borderColor: now.pillColourHex, color: now.pillColourHex }}
        title={now.pillColour}
      >
        <span className="wnow-dot" style={{ background: now.pillColourHex }} aria-hidden="true" />
        {now.pillText}
      </span>
      {now.unsure && now.unsureTag != null && <span className="wnow-tag">{now.unsureTag}</span>}
      {now.showWhyColour && actions.onWhyColour && (
        <button type="button" className="wnow-link" onClick={actions.onWhyColour}>
          Why this colour?
        </button>
      )}
      {now.when && <span className="wnow-when">{formatWhen(now.when, at)}</span>}
      <span className="wnow-spacer" />
      {saidOnTurnOn != null && <span className="wnow-voice-said">{saidOnTurnOn}</span>}
      <Outcome outcome={voice.outcome} />
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
          {voiceControl.label ?? "Play"}
        </button>
      )}
      {voiceControl.kind === "preparing" && (
        <span className="wnow-voice wnow-voice-preparing">{voiceControl.label}</span>
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
          {voiceControl.label}
        </button>
      )}
    </div>
  );
}

/**
 * The reply box. Always open wherever the Gateway sent a placeholder for it, so several asks are answered at once.
 *
 * THE OWNER'S WORDS SURVIVE A FAILURE. The box is emptied only when the send was ACCEPTED; a refusal, a server
 * error or a network drop leaves exactly what he typed in the box, with the Gateway's sentence underneath it, so the
 * next attempt is one click rather than typing it all again.
 */
function ReplyBox({
  placeholder,
  onSend,
}: {
  placeholder?: string | null;
  onSend?: (text: string) => Promise<WingmanNowOutcome>;
}) {
  const [text, setText] = useState("");
  const send = useOutcome();
  if (placeholder == null || !onSend) return null;
  return (
    <div className="wnow-reply">
      <textarea
        aria-label="Your reply to this session"
        placeholder={placeholder}
        value={text}
        onChange={(e) => setText(e.target.value)}
      />
      <div className="wnow-reply-bar">
        <button
          type="button"
          className="wnow-btn wnow-btn-primary"
          disabled={text.trim().length === 0 || send.busy}
          onClick={() => {
            void send.run(() => onSend(text)).then((result) => {
              if (result.accepted) setText("");
            });
          }}
        >
          {send.busy ? "Sending..." : "Send"}
        </button>
        <span className="wnow-when">Goes to the session as your message. Answers several asks at once.</span>
      </div>
      <Outcome outcome={send.outcome} />
    </div>
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
