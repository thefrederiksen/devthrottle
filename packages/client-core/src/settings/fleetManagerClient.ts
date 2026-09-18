// The Fleet Manager setting (the Fleet Manager mission, step 5): where the account's Fleet Manager runs, and
// starting, restarting and moving it.
//
// Everything the tab shows comes folded from the Gateway (CLAUDE.md rule 7): every sentence, label, state, which
// computer can be chosen and which button is offered. This file only carries it. A refusal throws a GatewayError
// carrying the Gateway's own sentence (GatewayError.from), which the tab shows as it is.
import { authHeaders, GatewayError } from "../api/client";

export interface FleetManagerAction {
  offered: boolean;
  label: string;
  note?: string | null;
  /** For the save button only: "move" saves and moves the running Fleet Manager; "save" only records the choice. */
  verb?: "move" | "save" | null;
  busyLabel: string;
  /** When set, the tab asks before acting. */
  confirmTitle?: string | null;
  confirmMessage?: string | null;
}

export interface FleetManagerAgentChoice {
  value: string;
  displayName: string;
  offeredOnSavedMachine?: boolean | null;
}

export interface FleetManagerMachineChoice {
  machine: string;
  state: "running" | "launcher-will-start" | "not-reachable";
  stateLabel: string;
  detail?: string | null;
  tone: "ok" | "go" | "bad";
  selectable: boolean;
  lastSeenUtc?: string | null;
}

export interface FleetManagerStatus {
  state: "running" | "not-running" | "unreachable" | "no-computer";
  sentence: string;
  /** The short state the Fleet Manager page shows under its title, for example "idle, watching 5 sessions". */
  line: string;
  /** True while the running Fleet Manager is in the middle of a turn. */
  thinking: boolean;
  tone: "ok" | "idle" | "bad";
  sessionId?: string | null;
  sinceUtc?: string | null;
  watching: number;
  open: FleetManagerAction;
  start: FleetManagerAction;
  restart: FleetManagerAction;
  /** While a restart or a move is under way: the Gateway's sentence saying what it waits for. */
  replacement?: string | null;
  replacementTone?: "ok" | "idle" | "bad" | null;
  successorSessionId?: string | null;
  /** What the Fleet Manager page may show and use now - decided on the Gateway. */
  page: FleetManagerPageControls;
}

/** The Fleet Manager page's controls for the current state. The page renders these and decides nothing. */
export interface FleetManagerPageControls {
  where?: string | null;
  changeLabel: string;
  composerUsable: boolean;
  composerPlaceholder: string;
  composerOffText?: string | null;
  composerHint: string;
  quickPromptsUsable: boolean;
  quickPromptBusyLabel: string;
  thinkingShown: boolean;
  notRunningBarShown: boolean;
  settingsLabel: string;
}

export interface FleetManagerPlacement {
  agent?: string | null;
  agentLabel: string;
  machine?: string | null;
  isDefault: boolean;
  defaultNote?: string | null;
  agentNote: string;
  agents: FleetManagerAgentChoice[];
  machineNote: string;
  machines: FleetManagerMachineChoice[];
  status: FleetManagerStatus;
  save: FleetManagerAction;
  generatedAtUtc: string;
}

const PREFIX = "/gateway/fleet-manager";

async function call(method: "GET" | "PUT" | "POST", path: string, what: string, body?: unknown): Promise<FleetManagerPlacement> {
  const res = await fetch(path, {
    method,
    headers: {
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...authHeaders(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  // The Gateway's own sentence travels on the error, and the tab shows it as it is.
  if (!res.ok) throw await GatewayError.from(res, what);
  return (await res.json()) as FleetManagerPlacement;
}

/** GET /gateway/fleet-manager/placement - the folded answer the tab renders. */
export function getFleetManagerPlacement(): Promise<FleetManagerPlacement> {
  return call("GET", `${PREFIX}/placement`, "read the Fleet Manager setting");
}

/** PUT /gateway/fleet-manager/placement - record where it runs. */
export function saveFleetManagerPlacement(agent: string, machine: string): Promise<FleetManagerPlacement> {
  return call("PUT", `${PREFIX}/placement`, "save where the Fleet Manager runs", { agent, machine });
}

/** POST /gateway/fleet-manager/start - start it where the setting says. Can take up to 90 seconds. */
export function startFleetManager(): Promise<FleetManagerPlacement> {
  return call("POST", `${PREFIX}/start`, "start the Fleet Manager");
}

/** POST /gateway/fleet-manager/restart - a new one in the saved place; the old one closes after its turn. */
export function restartFleetManager(): Promise<FleetManagerPlacement> {
  return call("POST", `${PREFIX}/restart`, "restart the Fleet Manager");
}

/** POST /gateway/fleet-manager/move - save, then restart (or start) there. */
export function moveFleetManager(agent: string, machine: string): Promise<FleetManagerPlacement> {
  return call("POST", `${PREFIX}/move`, "move the Fleet Manager", { agent, machine });
}
