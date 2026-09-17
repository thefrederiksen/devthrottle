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

/** Everything the Now view can do. Each is wired by the shell that mounts it; an action with no handler is not drawn. */
export interface WingmanNowActions {
  /** Open Debug on the colour rules for this row. */
  onWhyColour?: () => void;
  /** Play the narration the Gateway has already made. */
  onPlayVoice?: () => void;
  /** Turn voice mode on for this session from here. */
  onTurnOnVoice?: () => void;
  /** Answer the stop by picking one of the Gateway's options. */
  onAnswerOption?: (option: WingmanNowOption) => void;
  /** Send the owner's own words to the session. */
  onSendReply?: (text: string) => void;
  onSnooze?: () => void;
  onOpenTerminal?: () => void;
  /** Open the account settings where the Wingman is switched on. */
  onOpenSettings?: () => void;
  /** Go to another session in the account. */
  onGoToSession?: (sessionId: string) => void;
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
                      disabled={!now.canAnswerByOption || !actions.onAnswerOption}
                      onClick={() => actions.onAnswerOption?.(option)}
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
            <ReplyBox placeholder={now.replyPlaceholder} onSend={actions.onSendReply} />
          </section>
        ) : (
          now.unsure && now.unsureLine != null && <p className="wnow-unsure-line">{now.unsureLine}</p>
        )}

        {now.calmCard && (
          <section className="wnow-card wnow-card-calm" aria-label={now.calmCard.heading}>
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
              <button type="button" className="wnow-btn" onClick={actions.onSnooze}>
                Snooze this session
              </button>
            )}
            {actions.onOpenTerminal && (
              <button type="button" className="wnow-btn" onClick={actions.onOpenTerminal}>
                Open the terminal
              </button>
            )}
          </div>
        )}

        {now.lastStop && <Past past={now.lastStop} />}
        {now.lastGood && <Past past={now.lastGood} />}
      </div>
    </div>
  );
}

function Header({ now, at, actions }: { now: WingmanNowDto; at: Date; actions: WingmanNowActions }) {
  const [turnedOn, setTurnedOn] = useState(false);
  const voice = now.voice;
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
      {turnedOn && voice.afterTurnOnText != null && <span className="wnow-voice-said">{voice.afterTurnOnText}</span>}
      {voice.kind === "play" && actions.onPlayVoice && (
        <button type="button" className="wnow-voice wnow-voice-play" onClick={actions.onPlayVoice}>
          {voice.label ?? "Play"}
        </button>
      )}
      {voice.kind === "preparing" && (
        <span className="wnow-voice wnow-voice-preparing">{voice.label}</span>
      )}
      {voice.kind === "turn-on" && actions.onTurnOnVoice && (
        <button
          type="button"
          className="wnow-voice wnow-voice-turn-on"
          onClick={() => {
            setTurnedOn(true);
            actions.onTurnOnVoice?.();
          }}
        >
          {voice.label}
        </button>
      )}
    </div>
  );
}

/** The reply box. Always open wherever the Gateway sent a placeholder for it, so several asks are answered at once. */
function ReplyBox({ placeholder, onSend }: { placeholder?: string | null; onSend?: (text: string) => void }) {
  const [text, setText] = useState("");
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
          disabled={text.trim().length === 0}
          onClick={() => {
            onSend(text);
            setText("");
          }}
        >
          Send
        </button>
        <span className="wnow-when">Goes to the session as your message. Answers several asks at once.</span>
      </div>
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
