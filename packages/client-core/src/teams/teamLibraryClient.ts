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

/**
 * How far an add got, held by the dialog across attempts. Adding is two writes - create (or copy), then publish - and
 * when the second fails the item already exists on the Gateway, so a second attempt must not create it again (the id
 * is taken): it rewrites what the first attempt left and publishes that.
 */
export interface AddProgress {
  /** The id the Gateway already holds from an earlier attempt; null until the first write succeeded. */
  createdId: string | null;
  /** The published version a copied workflow started at; null for a skill. */
  createdVersion: number | null;
}

export function newAddProgress(): AddProgress {
  return { createdId: null, createdVersion: null };
}

/**
 * Add a skill to the team: create it, then publish it so the team's sessions get it. A second attempt after a
 * publish that failed rewrites the unpublished skill the first attempt left, then publishes it.
 */
export async function addTeamSkill(
  teamId: string,
  skill: { id: string; name: string; summary: string; bodyMarkdown: string },
  progress: AddProgress,
  signal?: AbortSignal,
): Promise<void> {
  const content = { name: skill.name, summary: skill.summary, triggers: [] as string[], bodyMarkdown: skill.bodyMarkdown };
  if (progress.createdId === null) {
    await send(teamPath(teamId, "/skills"), json("POST", { id: skill.id, ...content }), `add ${skill.name}`, signal);
    progress.createdId = skill.id;
  } else {
    // A new skill's only versions are the ones this dialog wrote, so its newest version is the one to write over.
    const base = `/skills/${encodeURIComponent(progress.createdId)}`;
    const newest = (await listVersions(teamId, base, `add ${skill.name}`, signal))[0];
    if (newest === undefined) throw new GatewayError(502, `The Gateway has no version of ${progress.createdId} to finish adding.`);
    await send(teamPath(teamId, `${base}/draft`), json("PUT", { ...content, files: [] }, { "If-Match": newest.contentHash }), `add ${skill.name}`, signal);
  }
  await send(teamPath(teamId, `/skills/${encodeURIComponent(progress.createdId)}/publish`), json("POST"), `publish ${skill.name}`, signal);
}

interface VersionInfo {
  version: number;
  status: string;
  contentHash: string;
}

/** An item's version history, newest first. */
async function listVersions(teamId: string, base: string, what: string, signal?: AbortSignal): Promise<VersionInfo[]> {
  const res = await send(teamPath(teamId, `${base}/versions`), json("GET"), what, signal);
  const body = await readJson<{ versions?: VersionInfo[] }>(res, what);
  if (!Array.isArray(body.versions)) throw new GatewayError(502, "The Gateway's version history had no versions in it.");
  return body.versions;
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
 * Change a team skill or workflow: its name, its one line and its words (whichever are given). Everything else the
 * published version carries - triggers, files, steps - is kept exactly. Then published.
 *
 * The write is made against the hash of the version the person read, so a change someone else published in the
 * meantime is refused rather than overwritten. One exception: an unpublished draft left by a change whose publish
 * failed. The Gateway compares against that draft when one exists, and the library never shows it, so writing against
 * the published hash would be refused for every later change, for good. Every change this page makes publishes
 * straight after writing, so a draft that is still there is a change that did not finish; this change replaces it.
 */
export async function changeTeamItem(
  teamId: string,
  item: Pick<TeamLibraryItem, "id" | "kind" | "version">,
  change: { name?: string; summary?: string; text?: string },
  signal?: AbortSignal,
): Promise<void> {
  const root = item.kind === "Workflow" ? "/workflows" : "/skills";
  const base = `${root}/${encodeURIComponent(item.id)}`;
  const leftover = (await listVersions(teamId, base, `read ${item.id}`, signal)).find((v) => v.status === "draft");
  const current = await send(teamPath(teamId, `${base}/versions/${item.version}`), json("GET"), `read ${item.id}`, signal);
  const body = item.kind === "Workflow"
    ? await (async () => {
        const v = await readJson<WorkflowVersion>(current, `read ${item.id}`);
        return {
          hash: v.contentHash,
          content: {
            name: change.name ?? v.name, summary: change.summary ?? v.summary, whenToUse: v.whenToUse,
            humanCheckpoint: v.humanCheckpoint, steps: v.steps, instructionsMarkdown: change.text ?? v.instructionsMarkdown,
            outcomeCriteria: v.outcomeCriteria, files: v.files.map((f) => ({ fileName: f.fileName, content: f.content })),
          },
        };
      })()
    : await (async () => {
        const v = await readJson<SkillVersion>(current, `read ${item.id}`);
        return {
          hash: v.contentHash,
          content: {
            name: change.name ?? v.name, summary: change.summary ?? v.summary, triggers: v.triggers,
            bodyMarkdown: change.text ?? v.bodyMarkdown, files: v.files, license: v.license,
            compatibility: v.compatibility, allowedTools: v.allowedTools, metadata: v.metadata,
          },
        };
      })();
  const ifMatch = leftover?.contentHash ?? body.hash;
  await send(teamPath(teamId, `${base}/draft`), json("PUT", body.content, { "If-Match": ifMatch }), `change ${item.id}`, signal);
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

/**
 * Add a workflow to the team as a copy of a built-in one, under the team's own id and name. The Gateway's copy keeps
 * the built-in's name, so the copy is then renamed and published. A second attempt after a failed rename finishes the
 * rename on the copy the first attempt made, rather than copying again (the id is taken).
 */
export async function addTeamWorkflowFrom(
  teamId: string,
  sourceId: string,
  workflow: { id: string; name: string },
  progress: AddProgress,
  signal?: AbortSignal,
): Promise<void> {
  if (progress.createdId === null || progress.createdVersion === null) {
    const query = new URLSearchParams({ newId: workflow.id });
    const res = await send(teamPath(teamId, `/workflows/${encodeURIComponent(sourceId)}/clone?${query.toString()}`), json("POST"), `add ${workflow.name}`, signal);
    const copy = await readJson<{ id?: string; name?: string; version?: number }>(res, `add ${workflow.name}`);
    if (typeof copy.id !== "string" || typeof copy.version !== "number") {
      throw new GatewayError(502, "The Gateway's answer to copying the workflow is missing its id or version.");
    }
    progress.createdId = copy.id;
    progress.createdVersion = copy.version;
    if (copy.name === workflow.name) return;
  }
  await changeTeamItem(teamId, { id: progress.createdId, kind: "Workflow", version: progress.createdVersion }, { name: workflow.name }, signal);
}
