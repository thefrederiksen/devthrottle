// The team's shared skills and workflows (Teams 6 - devthrottle_internal#2304, screen S5): the typed, same-origin
// client of the Gateway's /teams/{teamId}/library, /teams/{teamId}/skills and /teams/{teamId}/workflows.
//
// THE GATEWAY DECIDES WHO MAY DO WHAT (rule 7). Whether this person may change the library, the sentence when they
// may not, whether each item can be changed, and who changed it are all read from GET /teams/{teamId}/library and
// carried verbatim. Nothing here compares a role. A refusal - a Collaborator reading, a Developer changing - is the
// Gateway's 403 with its own sentence, thrown as a GatewayError so the page shows that sentence.
//
// WHO MADE A CHANGE is never sent. The Gateway records the member it identified and ignores any author a client
// names, so this client sends none - and so no email reaches a request, a query string or a log.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** One row of the page: a skill or a workflow the team owns. */
export interface TeamLibraryItem {
  id: string;
  name: string;
  summary: string;
  /** "Skill" or "Workflow", as the Gateway labels it. */
  kind: string;
  enabled: boolean;
  version: number;
  changedAtUtc: string;
  /** The member who made the published version, named by the Gateway. */
  changedBy: string;
  /** The Gateway's verdict for THIS person on THIS item. */
  canChange: boolean;
}

/** What GET /teams/{teamId}/library answered. */
export interface TeamLibrary {
  team: { id: string; name: string; role: string };
  canChange: boolean;
  /** Why this person may not change the library; null when they may. */
  changeRefusal: string | null;
  /** The sentence about DevThrottle's built-ins, shown under the list. */
  builtInNote: string;
  items: TeamLibraryItem[];
}

/** A built-in workflow a new team workflow can start from. */
export interface StartingWorkflow {
  id: string;
  name: string;
  summary: string;
}

function teamPath(teamId: string, rest: string): string {
  return `/teams/${encodeURIComponent(teamId)}${rest}`;
}

function json(method: string, body?: unknown, extra?: Record<string, string>): RequestInit {
  return {
    method,
    headers: {
      ...authHeaders(),
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...(extra ?? {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  };
}

async function send(path: string, init: RequestInit, what: string, signal?: AbortSignal): Promise<Response> {
  const res = await gatewayFetch(path, { ...init, signal });
  if (!res.ok) throw await GatewayError.from(res, what);
  return res;
}

async function readJson<T>(res: Response, what: string): Promise<T> {
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json") {
    throw new GatewayError(
      502,
      `The Gateway answered "${what}" with ${type || "an unlabelled body"} instead of data. It may be running a build from before team libraries existed.`,
    );
  }
  return (await res.json()) as T;
}

/** The page's read: the team, what this person may do, and the team's own skills and workflows. */
export async function getTeamLibrary(teamId: string, signal?: AbortSignal): Promise<TeamLibrary> {
  const what = "read the team's skills and workflows";
  const res = await send(teamPath(teamId, "/library"), json("GET"), what, signal);
  const body = await readJson<Partial<TeamLibrary>>(res, what);
  if (!body.team || !Array.isArray(body.items) || typeof body.canChange !== "boolean") {
    throw new GatewayError(502, "The Gateway's answer about the team's skills and workflows is missing the team, the list or the permission.");
  }
  return {
    team: body.team,
    canChange: body.canChange,
    changeRefusal: body.changeRefusal ?? null,
    builtInNote: body.builtInNote ?? "",
    items: body.items,
  };
}

/** The words an agent reads: a skill's body, or a workflow's instructions. */
export async function getTeamItemText(teamId: string, item: Pick<TeamLibraryItem, "id" | "kind">, signal?: AbortSignal): Promise<string> {
  const path = item.kind === "Workflow"
    ? teamPath(teamId, `/workflows/${encodeURIComponent(item.id)}/instructions`)
    : teamPath(teamId, `/skills/${encodeURIComponent(item.id)}/body`);
  const res = await send(path, { method: "GET", headers: { ...authHeaders(), Accept: "text/markdown" } }, `read ${item.id}`, signal);
  return res.text();
}

/** Add a skill to the team: create it, then publish it so the team's sessions get it. */
export async function addTeamSkill(
  teamId: string,
  skill: { id: string; name: string; summary: string; bodyMarkdown: string },
  signal?: AbortSignal,
): Promise<void> {
  await send(teamPath(teamId, "/skills"), json("POST", { ...skill, triggers: [] }), `add ${skill.name}`, signal);
  await send(teamPath(teamId, `/skills/${encodeURIComponent(skill.id)}/publish`), json("POST"), `publish ${skill.name}`, signal);
}

interface SkillVersion {
  name: string;
  summary: string;
  triggers: string[];
  bodyMarkdown: string;
  files: unknown[];
  license?: string | null;
  compatibility?: string | null;
  allowedTools?: string | null;
  metadata?: Record<string, string>;
  contentHash: string;
}

interface WorkflowVersion {
  name: string;
  summary: string;
  whenToUse: string;
  humanCheckpoint: string;
  steps: unknown[];
  instructionsMarkdown: string;
  outcomeCriteria: unknown[];
  files: { fileName: string; content: string }[];
  contentHash: string;
}

/**
 * Change a team skill or workflow: its one line and its words. Everything else the published version carries -
 * triggers, files, steps - is kept exactly. Written against the published version's hash, so a change someone else
 * published in the meantime is refused rather than overwritten. Then published.
 */
export async function changeTeamItem(
  teamId: string,
  item: Pick<TeamLibraryItem, "id" | "kind" | "version">,
  change: { summary: string; text: string },
  signal?: AbortSignal,
): Promise<void> {
  const root = item.kind === "Workflow" ? "/workflows" : "/skills";
  const base = `${root}/${encodeURIComponent(item.id)}`;
  const current = await send(teamPath(teamId, `${base}/versions/${item.version}`), json("GET"), `read ${item.id}`, signal);
  const body = item.kind === "Workflow"
    ? await (async () => {
        const v = await readJson<WorkflowVersion>(current, `read ${item.id}`);
        return {
          hash: v.contentHash,
          content: {
            name: v.name, summary: change.summary, whenToUse: v.whenToUse, humanCheckpoint: v.humanCheckpoint,
            steps: v.steps, instructionsMarkdown: change.text, outcomeCriteria: v.outcomeCriteria,
            files: v.files.map((f) => ({ fileName: f.fileName, content: f.content })),
          },
        };
      })()
    : await (async () => {
        const v = await readJson<SkillVersion>(current, `read ${item.id}`);
        return {
          hash: v.contentHash,
          content: {
            name: v.name, summary: change.summary, triggers: v.triggers, bodyMarkdown: change.text, files: v.files,
            license: v.license, compatibility: v.compatibility, allowedTools: v.allowedTools, metadata: v.metadata,
          },
        };
      })();
  await send(teamPath(teamId, `${base}/draft`), json("PUT", body.content, { "If-Match": body.hash }), `change ${item.id}`, signal);
  await send(teamPath(teamId, `${base}/publish`), json("POST"), `publish ${item.id}`, signal);
}

/** Remove a team skill or workflow. It is archived on the Gateway: the team's sessions no longer get it. */
export async function removeTeamItem(teamId: string, item: Pick<TeamLibraryItem, "id" | "kind">, signal?: AbortSignal): Promise<void> {
  const root = item.kind === "Workflow" ? "/workflows" : "/skills";
  await send(teamPath(teamId, `${root}/${encodeURIComponent(item.id)}`), json("DELETE"), `remove ${item.id}`, signal);
}

/** DevThrottle's built-in workflows, which a new team workflow starts from. */
export async function getStartingWorkflows(teamId: string, signal?: AbortSignal): Promise<StartingWorkflow[]> {
  const what = "read the workflows a team workflow can start from";
  const res = await send(teamPath(teamId, "/workflows"), json("GET"), what, signal);
  const body = await readJson<{ workflows?: { id: string; name: string; summary: string; isBuiltIn?: boolean }[] }>(res, what);
  if (!Array.isArray(body.workflows)) throw new GatewayError(502, "The Gateway's list of workflows had no workflows in it.");
  return body.workflows.filter((w) => w.isBuiltIn === true).map((w) => ({ id: w.id, name: w.name, summary: w.summary }));
}

/** Add a workflow to the team as a copy of a built-in one, under a new id. The copy is the team's to change. */
export async function addTeamWorkflowFrom(teamId: string, sourceId: string, newId: string, signal?: AbortSignal): Promise<void> {
  const query = new URLSearchParams({ newId });
  await send(teamPath(teamId, `/workflows/${encodeURIComponent(sourceId)}/clone?${query.toString()}`), json("POST"), `add ${newId}`, signal);
}
