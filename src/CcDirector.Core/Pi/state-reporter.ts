import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { randomBytes, randomUUID } from "node:crypto";
import { existsSync, linkSync, mkdirSync, readFileSync, readdirSync, realpathSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";

const stateEventMap = {
  "agent_start": "turn_started",
  "agent_settled": "turn_ended",
  "ui_prompt_start": "menu_opened",
  "ui_prompt_end": "menu_closed"
} as const;

const supportedPromptKinds = new Set(["select", "confirm", "input", "editor", "custom"]);
const sequencePattern = /^(\d+)\.state\.json$/;
const tokenPattern = /^[0-9a-f]{32}$/;

interface PiLifecycleEvent {
  type: string;
  kind?: unknown;
  title?: unknown;
}

type StateEventName = (typeof stateEventMap)[keyof typeof stateEventMap] | "unsupported";

function commandLineVersion(): string | null {
  let current = resolve(dirname(realpathSync(process.argv[1] ?? process.execPath)));
  for (let depth = 0; depth < 8; depth++) {
    const packagePath = join(current, "package.json");
    if (existsSync(packagePath)) {
      try {
        const value = JSON.parse(readFileSync(packagePath, "utf8")) as { name?: unknown; version?: unknown };
        if (value.name === "@earendil-works/pi-coding-agent" && typeof value.version === "string" && value.version.length > 0) {
          return value.version;
        }
      } catch {
        // This was not Pi's readable package manifest. Continue towards the package root.
      }
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return null;
}

function holderIsAlive(pid: number): boolean {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== "ESRCH";
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((complete) => setTimeout(complete, milliseconds));
}

async function acquireSequenceLock(directory: string): Promise<() => void> {
  const lockPath = join(directory, ".sequence.lock");
  const candidate = join(directory, `.sequence.lock.${process.pid}.${randomBytes(8).toString("hex")}.tmp`);
  writeFileSync(candidate, JSON.stringify({ pid: process.pid }), { encoding: "utf8", mode: 0o600, flag: "wx" });
  try {
    for (let attempt = 0; attempt < 100; attempt++) {
      try {
        // A hard link publishes the complete holder record in one kernel operation. A process that
        // dies before this leaves no lock; one that dies after it leaves a lock naming its process id.
        linkSync(candidate, lockPath);
        unlinkSync(candidate);
        return () => {
          try {
            const holder = JSON.parse(readFileSync(lockPath, "utf8")) as { pid?: unknown };
            if (holder.pid === process.pid) unlinkSync(lockPath);
          } catch {
            // A later writer owns it, or it is already gone. Never remove an unverified lock.
          }
        };
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
        try {
          const holder = JSON.parse(readFileSync(lockPath, "utf8")) as { pid?: unknown };
          if (typeof holder.pid !== "number" || !holderIsAlive(holder.pid)) {
            unlinkSync(lockPath);
            continue;
          }
        } catch (readError) {
          if ((readError as NodeJS.ErrnoException).code === "ENOENT") continue;
        }
        await delay(20);
      }
    }
    throw new Error("The Pi state-event sequence lock stayed owned by a running process for two seconds.");
  } finally {
    try { unlinkSync(candidate); } catch { /* Published or already removed. */ }
  }
}

function nextSequence(directory: string): number {
  const marker = join(directory, ".sequence");
  let highest = 0;
  try {
    const recorded = Number(readFileSync(marker, "utf8"));
    if (Number.isSafeInteger(recorded) && recorded > 0) highest = recorded;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
  }
  for (const name of readdirSync(directory)) {
    const match = sequencePattern.exec(name);
    if (match) highest = Math.max(highest, Number(match[1]));
  }
  const next = highest + 1;
  const temporary = `${marker}.${process.pid}.${randomBytes(8).toString("hex")}.tmp`;
  writeFileSync(temporary, String(next), { encoding: "utf8", mode: 0o600 });
  renameSync(temporary, marker);
  return next;
}

function describePrompt(event: PiLifecycleEvent): string {
  const kind = typeof event.kind === "string" ? event.kind : "unknown";
  const title = typeof event.title === "string" && event.title.length > 0 ? `:${event.title}` : "";
  return `${kind}${title}`;
}

function translate(event: PiLifecycleEvent, runActive: boolean): { event: StateEventName; detail: string | null } {
  const mapped = stateEventMap[event.type as keyof typeof stateEventMap];
  if (!mapped) return { event: "unsupported", detail: event.type || "missing_event_type" };
  if ((event.type === "ui_prompt_start" || event.type === "ui_prompt_end")
      && (typeof event.kind !== "string" || !supportedPromptKinds.has(event.kind))) {
    return { event: "unsupported", detail: `${event.type}:${String(event.kind ?? "missing_kind")}` };
  }
  return {
    // Closing an extension prompt does not settle Pi's run. The tool and model continue working,
    // so restore the higher-priority agent fact instead of falsely reporting that Pi awaits input.
    event: event.type === "ui_prompt_end" && runActive ? "turn_started" : mapped,
    detail: event.type.startsWith("ui_prompt_") ? describePrompt(event) : null,
  };
}

export default function stateReporter(pi: ExtensionAPI): void {
  const directory = process.env.CC_STATE_EVENT_DIR;
  const token = process.env.CC_STATE_EVENT_TOKEN;
  const sessionId = process.env.CC_SESSION_ID;
  const cliVersion = commandLineVersion();

  if (!directory || !token || !sessionId || !cliVersion || !tokenPattern.test(token)
      || basename(directory) !== `${sessionId}.${token}`) return;

  mkdirSync(directory, { recursive: true });
  let runActive = false;

  const report = async (lifecycleEvent: PiLifecycleEvent, runWasActive = runActive): Promise<void> => {
    const release = await acquireSequenceLock(directory);
    try {
      const sequence = nextSequence(directory);
      const translated = translate(lifecycleEvent, runWasActive);
      const record = {
        version: 1,
        eventId: randomUUID(),
        sessionId,
        sequence,
        occurredAtUtc: new Date().toISOString(),
        event: translated.event,
        source: "agent",
        agent: "pi",
        detail: translated.detail,
        cliVersion,
      };
      const destination = join(directory, `${sequence}.state.json`);
      const temporary = join(directory, `.${sequence}.${process.pid}.${randomBytes(8).toString("hex")}.tmp`);
      writeFileSync(temporary, JSON.stringify(record), { encoding: "utf8", mode: 0o600 });
      renameSync(temporary, destination);
    } finally {
      release();
    }
  };

  pi.on("agent_start", (event) => {
    runActive = true;
    return report(event);
  });
  pi.on("agent_settled", (event) => {
    runActive = false;
    return report(event);
  });
  pi.on("ui_prompt_start", (event) => report(event));
  pi.on("ui_prompt_end", (event) => report(event));
}
