// The live conversation of one API-mode session, kept current from the Gateway's live stream (mission #4070, phase 3).
//
// The conversation is built from the stream ALONE, replayed from the start: not from /history, whose replies carry no
// message id, so a reply loaded from it could not be matched to the same reply arriving live and would show twice.
// The stream is opened with no cursor; the Gateway replays what it holds, then follows live, and resets the view when
// it cannot continue it.
import { useEffect, useRef, useState } from "react";
import { describeAndReport } from "../errors/reportClientError";
import { applyItems, applyReset, emptyConversation, type LiveConversation } from "./liveConversation";
import { LIVE_CONNECTING_MESSAGE, openLiveStream, type LiveStreamDeps, type LiveStreamState } from "./liveStream";

const SURFACE = "api-mode-live-chat";

export interface ApiModeLiveChat {
  conversation: LiveConversation;
  stream: LiveStreamState;
  /** A sentence for something this client could not apply, already reported; null when there is none. */
  problem: string | null;
}

export function useApiModeLiveChat(sessionId: string | undefined, deps?: LiveStreamDeps): ApiModeLiveChat {
  const [conversation, setConversation] = useState<LiveConversation>(emptyConversation);
  const [stream, setStream] = useState<LiveStreamState>({ open: false, message: LIVE_CONNECTING_MESSAGE });
  const [problem, setProblem] = useState<string | null>(null);
  // The conversation is applied here, outside React's state updater, so an item that breaks the contract throws in
  // the stream callback below - where it is caught and reported - and not later inside a render.
  const held = useRef<LiveConversation>(conversation);

  useEffect(() => {
    // A new session is a new conversation: nothing held for the last one stands.
    held.current = emptyConversation();
    setConversation(held.current);
    setStream({ open: false, message: LIVE_CONNECTING_MESSAGE });
    setProblem(null);
    if (!sessionId) return;
    const close = openLiveStream(
      sessionId,
      {
        onItems: (items) => {
          // An entry point (a stream callback): an item that breaks the contract is reported here, never guessed at.
          try {
            held.current = applyItems(held.current, items);
            setConversation(held.current);
          } catch (err) {
            setProblem(describeAndReport(SURFACE, "show the live conversation", err, { sessionId }));
          }
        },
        onReset: (reset) => {
          held.current = applyReset(held.current, reset);
          setConversation(held.current);
        },
        onState: setStream,
        onProblem: setProblem,
      },
      deps,
    );
    return () => {
      close();
    };
    // deps is a test seam, fixed for the life of the component.
  }, [sessionId]);

  return { conversation, stream, problem };
}
