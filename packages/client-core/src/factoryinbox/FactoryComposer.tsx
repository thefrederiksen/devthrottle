import { useCallback, useEffect, useRef, useState } from "react";
import { DictationDialog } from "../dictation/DictationDialog";
import type { CapturedUtterance } from "../dictation/backgroundSend";
import { describeAndReport } from "../errors/reportClientError";
import { sendFactoryInboxMessage, type FactoryInboxNote } from "./factoryInboxClient";
import {
  discardFactoryRecording,
  listFactoryRecordings,
  saveFactoryRecording,
  sendFactoryRecording,
  type FactoryRecording,
} from "./factoryInboxVoice";
import "./factoryInbox.css";

// THE FACTORY INBOX COMPOSER (Factory Inbox mission, phase 4; mockups A, B and D): pick a factory (on the list), Good or
// Bad, Mic, Send. One component for the list, the thread and the Cockpit's factory page, on both shells (rule 8).
//
// TYPED OR SPOKEN. Typed words go as they are. Mic opens the same voice box a session uses; Send in it hands over the
// AUDIO, which is saved in this browser and sent through the durable upload (factoryInboxVoice.ts) - the Gateway
// transcribes it and keeps the raw words (the verbatim rule). A recording that was paused has already been turned into
// words on the device, so those words come back into the box, to be read and sent as typed, never claimed as spoken.
//
// IT SENDS AND PRINTS; IT DECIDES NOTHING. A refusal (an archived factory, a factory with no Boss) is the Gateway's
// sentence, shown as it is and reported once.

/** Where a factory inbox failure is filed. One set of screens on both shells, so one surface. */
export const FACTORY_INBOX_SURFACE = "factory-inbox";

/** How long a recording that could not be sent waits before it is tried again, while the screen is open. */
const RETRY_MS = 15_000;

export interface FactoryChoice {
  factory: string;
  title: string;
}

export interface FactoryComposerProps {
  /** The factory the message goes to; null while none is picked. */
  factory: string | null;
  /** The factory's title, for the box's placeholder when there is no picker. */
  factoryTitle?: string;
  /** The factories to pick from. Omitted where the factory is fixed (a thread, a factory's page). */
  choices?: ReadonlyArray<FactoryChoice>;
  onFactoryChange?: (factory: string) => void;
  /** Which shell mounted it ("cockpit" or "mobile"): tags the voice box's capture measurements. */
  surface: "cockpit" | "mobile";
  /** A message was left: the caller reads its list or thread again. */
  onLeft: () => void;
  /** The box's own label for screen readers and tests. */
  label?: string;
}

export function FactoryComposer({ factory, factoryTitle, choices, onFactoryChange, surface, onLeft, label }: FactoryComposerProps) {
  const [text, setText] = useState("");
  const [note, setNote] = useState<FactoryInboxNote>("none");
  const [sending, setSending] = useState(false);
  const [dictating, setDictating] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [recordings, setRecordings] = useState<FactoryRecording[]>([]);
  const driving = useRef(false);
  const retryTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onLeftRef = useRef(onLeft);
  onLeftRef.current = onLeft;

  // Send every saved recording for this factory that has not been refused; one pass at a time.
  const drive = useCallback(async (target: string) => {
    if (driving.current) return;
    driving.current = true;
    try {
      const saved = await listFactoryRecordings(target);
      for (const rec of saved) {
        if (rec.refusal !== undefined) continue;
        setStatus("Sending your recording...");
        const outcome = await sendFactoryRecording(rec);
        if (outcome.kind === "left") {
          setStatus("Your spoken message was left.");
          setError(null);
          onLeftRef.current();
        } else if (outcome.kind === "nothing") {
          setStatus(null);
          setError(describeAndReport(FACTORY_INBOX_SURFACE, "leave the spoken message", outcome.error));
        } else if (outcome.kind === "refused") {
          setStatus(null);
          setError(describeAndReport(FACTORY_INBOX_SURFACE, "leave the spoken message", outcome.error));
        } else {
          setStatus("Your recording is saved on this device and will be sent again.");
          setError(describeAndReport(FACTORY_INBOX_SURFACE, "leave the spoken message", outcome.error, { background: true }));
          if (retryTimer.current !== null) clearTimeout(retryTimer.current);
          retryTimer.current = setTimeout(() => void drive(target), RETRY_MS);
          break;
        }
      }
      setRecordings(await listFactoryRecordings(target));
    } finally {
      driving.current = false;
    }
  }, []);

  useEffect(() => {
    if (factory === null) return undefined;
    void drive(factory).catch((err: unknown) =>
      setError(describeAndReport(FACTORY_INBOX_SURFACE, "send the saved recordings", err)),
    );
    return () => {
      if (retryTimer.current !== null) clearTimeout(retryTimer.current);
      retryTimer.current = null;
    };
  }, [factory, drive]);

  const send = useCallback(async () => {
    if (factory === null || text.trim().length === 0) return;
    setSending(true);
    setStatus(null);
    try {
      await sendFactoryInboxMessage(factory, text, note);
      setText("");
      setNote("none");
      setError(null);
      setStatus("Your message was left.");
      onLeft();
    } catch (err: unknown) {
      setError(describeAndReport(FACTORY_INBOX_SURFACE, "leave the message", err));
    } finally {
      setSending(false);
    }
  }, [factory, text, note, onLeft]);

  const sendAudio = useCallback(
    (captured: CapturedUtterance) => {
      if (factory === null) return;
      // Words of an earlier, paused part were transcribed on the device: into the box, to be sent as typed.
      if (captured.prefixText.trim().length > 0) setText((t) => joinWords(t, captured.prefixText));
      const rec: FactoryRecording = {
        id: crypto.randomUUID(),
        factory,
        note,
        blob: captured.blob,
        sentAt: captured.sentAt,
      };
      setNote("none");
      void saveFactoryRecording(rec).then(
        () => drive(factory),
        (err: unknown) => setError(describeAndReport(FACTORY_INBOX_SURFACE, "keep the recording", err)),
      );
    },
    [factory, note, drive],
  );

  const discard = useCallback(
    async (id: string) => {
      try {
        await discardFactoryRecording(id);
        if (factory !== null) setRecordings(await listFactoryRecordings(factory));
        setError(null);
      } catch (err: unknown) {
        setError(describeAndReport(FACTORY_INBOX_SURFACE, "discard the recording", err));
      }
    },
    [factory],
  );

  const toggle = (which: FactoryInboxNote) => setNote((n) => (n === which ? "none" : which));
  const placeholder = choices === undefined ? `Message ${factoryTitle ?? factory ?? ""}...` : "Write your message...";
  const refused = recordings.filter((r) => r.refusal !== undefined);
  const waiting = recordings.length - refused.length;

  return (
    <div className="fi-composer" data-testid="fi-composer">
      {choices !== undefined && (
        <label className="fi-composer-pick">
          <span>Leave a message in</span>
          <select
            value={factory ?? ""}
            onChange={(e) => onFactoryChange?.(e.target.value)}
            aria-label="Factory"
            disabled={choices.length === 0}
          >
            {choices.map((c) => (
              <option key={c.factory} value={c.factory}>
                {c.title}
              </option>
            ))}
          </select>
        </label>
      )}
      <textarea
        className="fi-composer-text"
        value={text}
        placeholder={placeholder}
        aria-label={label ?? "Message"}
        rows={2}
        onChange={(e) => setText(e.target.value)}
      />
      <div className="fi-composer-buttons">
        <button type="button" className="fi-note" data-note="good" aria-pressed={note === "good"} onClick={() => toggle("good")}>
          Good
        </button>
        <button type="button" className="fi-note" data-note="bad" aria-pressed={note === "bad"} onClick={() => toggle("bad")}>
          Bad
        </button>
        <button type="button" className="fi-mic" onClick={() => setDictating(true)} disabled={factory === null}>
          Mic
        </button>
        <button
          type="button"
          className="fi-send"
          onClick={() => void send()}
          disabled={factory === null || sending || text.trim().length === 0}
        >
          {sending ? "Sending..." : "Send"}
        </button>
      </div>
      {status !== null && (
        <p className="fi-composer-status" role="status">
          {status}
        </p>
      )}
      {waiting > 0 && (
        <p className="fi-composer-status" data-testid="fi-waiting-recordings">
          {waiting === 1 ? "1 recording is waiting to be sent." : `${waiting} recordings are waiting to be sent.`}
        </p>
      )}
      {error !== null && (
        <div className="fi-composer-error" role="alert">
          {error}
        </div>
      )}
      {refused.map((r) => (
        <div className="fi-composer-refused" key={r.id} data-testid="fi-refused-recording">
          <span>A recording was not left: {r.refusal}</span>
          <button type="button" onClick={() => void discard(r.id)}>
            Discard recording
          </button>
        </div>
      ))}
      {dictating && (
        <DictationDialog
          surface={surface}
          showInsert={false}
          onSend={(words) => setText((t) => joinWords(t, words))}
          onSendAudio={sendAudio}
          onClose={() => setDictating(false)}
        />
      )}
    </div>
  );
}

function joinWords(a: string, b: string): string {
  const left = a.trim();
  const right = b.trim();
  if (left.length === 0) return right;
  if (right.length === 0) return left;
  return `${left} ${right}`;
}
