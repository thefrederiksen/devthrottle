// The phase 3 proof harness (mission #4070). Mounts the SHARED ApiModeLiveChat exactly as each shell does, inside that
// shell's own stylesheet, and feeds it through the fake network from liveStream.testkit.ts - the same fakes the tests
// use, worded as the Gateway's ApiModeLiveRuling and ApiModeSessionFold word them. Nothing here is a Gateway; the
// scene is written frame by frame below, and capture.mjs photographs it.
//
//   ?shell=cockpit|phone   which shell's stylesheet and frame
//   ?scene=midturn|failure which conversation
import { createRoot } from "react-dom/client";
import { ApiModeLiveChat } from "../../../../../../packages/client-core/src/apimode/ApiModeLiveChat";
import {
  connectionItem, fakeLiveNetwork, receiptItem, replyDelta, replyFinal, toolCard, turnEndedItem, workingDisplay,
} from "../../../../../../packages/client-core/src/apimode/liveStream.testkit";
import type { ApiModeSessionDisplay } from "../../../../../../packages/client-core/src/apimode/liveTypes";

const params = new URLSearchParams(location.search);
const shell = params.get("shell") === "phone" ? "phone" : "cockpit";
const scene = params.get("scene") === "failure" ? "failure" : "midturn";

if (shell === "phone") await import("../../../../../../apps/mobile/src/styles.css");
else await import("../../../../../../apps/cockpit/src/styles.css");

const net = fakeLiveNetwork();

const midTurnDisplay: ApiModeSessionDisplay = {
  ...workingDisplay(2),
  queuedCount: 1,
  queueLabel: "1 prompt waiting: a turn is running",
};

const failureDisplay: ApiModeSessionDisplay = {
  ...workingDisplay(3),
  kind: "stopped",
  tone: "red",
  working: false,
  activityLabel: "Idle after turn 3",
  lastTurnEnding: "The last turn failed: Pi exited during the turn (exit code 137)",
  agentConnected: false,
  agentLabel: "Agent disconnected: Pi exited (exit code 137)",
};

function play() {
  const c = net.connections[0];
  const pong = "Reply with exactly the word PONG";
  const files = "List the files in this folder with bash, then tell me how many there are";
  if (scene === "midturn") {
    c.items("g1:1", [receiptItem("p1", "Delivered", "green", 1, undefined, pong)]);
    c.items("g1:4", [replyFinal("m1", "t1", "PONG", 4)]);
    c.items("g1:5", [turnEndedItem("t1", "Turn finished", "neutral", 5), receiptItem("p1", "Delivered", "green", 5, "t1", pong)]);
    c.items("g1:6", [receiptItem("p2", "Delivered", "green", 6, undefined, files)]);
    c.items("g1:8", [toolCard("c1", "t2", "bash", "{\"command\":\"ls -1\"}", "running", "neutral", 8)]);
    c.items("g1:9", [toolCard("c1", "t2", "bash", "{\"command\":\"ls -1\"}", "done", "green", 9)]);
    c.items("g1:10", [toolCard("c2", "t2", "bash", "{\"command\":\"ls -1 | wc -l\"}", "running", "neutral", 10)]);
    c.items("g1:11", [replyDelta("m2", "t2", "The folder holds `README.md`, `src/` and `tests/`. ", 11)]);
    c.items("g1:12", [replyDelta("m2", "t2", "Counting them now", 12)]);
    c.items("g1:13", [receiptItem("p3", "Queued: a turn is running", "neutral", 13, undefined, "Then summarise the README in one line")]);
    c.items("g1:14", [receiptItem("p4", "Refused: Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.", "red", 14, undefined, "Are you there?")]);
  } else {
    c.items("g1:1", [receiptItem("p1", "Delivered - stopped", "yellow", 1, "t1", "Write a long essay about rivers")]);
    c.items("g1:2", [replyFinal("m1", "t1", "Rivers begin as", 2)]);
    c.items("g1:3", [turnEndedItem("t1", "Turn stopped: stopped by the user", "yellow", 3)]);
    c.items("g1:4", [receiptItem("p2", "Delivered", "green", 4, undefined, "Count slowly to one hundred")]);
    c.items("g1:5", [toolCard("c1", "t2", "bash", "{\"command\":\"seq 1 100\"}", "stopped", "yellow", 5)]);
    c.items("g1:6", [replyDelta("m2", "t2", "1, 2, 3, 4, 5", 6)]);
    c.items("g1:7", [
      turnEndedItem("t2", "Turn failed: Pi exited during the turn (exit code 137)", "red", 7),
      receiptItem("p2", "Delivered - the turn failed: Pi exited during the turn (exit code 137)", "red", 7, "t2", "Count slowly to one hundred"),
    ]);
    c.items("g1:8", [connectionItem(8, "Agent disconnected: Pi exited", "red", "exit code 137")]);
  }
  document.body.dataset.ready = "true";
}

function Frame() {
  const display = scene === "midturn" ? midTurnDisplay : failureDisplay;
  const chat = <ApiModeLiveChat sessionId="7f8e1a40-0000-4000-8000-0000000000aa" display={display} streamDeps={net.deps} />;
  return shell === "phone" ? (
    <div className="terminal-screen" style={{ height: "100vh", display: "flex", flexDirection: "column" }}>
      <div className="chat-stage">{chat}</div>
    </div>
  ) : (
    <div className="session-content" style={{ height: "100vh", display: "flex" }}>
      <div className="session-pane">{chat}</div>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<Frame />);
// The fake network opens its stream once the component mounts; then the scene is written into it.
const wait = setInterval(() => {
  if (net.connections.length === 0) return;
  clearInterval(wait);
  play();
}, 20);
