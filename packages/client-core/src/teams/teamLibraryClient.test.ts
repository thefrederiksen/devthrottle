import { describe, it, expect, vi, afterEach } from "vitest";
import {
  addTeamSkill,
  addTeamWorkflowFrom,
  changeTeamItem,
  getStartingWorkflows,
  getTeamItemText,
  getTeamLibrary,
  removeTeamItem,
} from "./teamLibraryClient";

// The team's shared skills and workflows (devthrottle_internal#2304, S5): what the page sends to the Gateway's
// /teams/{teamId}/... routes, and how it reads the answers. The Gateway decides every permission; these pin that the
// client carries its answers verbatim, sends no author, and surfaces a refusal's own sentence.

function respond(body: unknown, status = 200, contentType = "application/json; charset=utf-8"): Response {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), { status, headers: { "Content-Type": contentType } });
}

const LIBRARY = {
  team: { id: "team-a", name: "Acme", role: "Developer" },
  canChange: false,
  changeRefusal: "In this team you are a Developer, and a Developer may not change the team's shared skills and workflows.",
  builtInNote: "DevThrottle's own built-in skills and workflows are available to every session as well.",
  count: 1,
  items: [{ id: "release", name: "Release", summary: "s", kind: "Skill", enabled: true, version: 2, changedAtUtc: "2026-10-02T10:00:00Z", changedBy: "priya@example.com", canChange: false }],
};

function stubFetch(...answers: Response[]) {
  const mock = vi.fn();
  for (const a of answers) mock.mockResolvedValueOnce(a);
  vi.stubGlobal("fetch", mock);
  return mock;
}

function call(mock: ReturnType<typeof vi.fn>, i: number): { url: string; method: string; body: unknown; headers: Record<string, string> } {
  const [url, init] = mock.mock.calls[i] as [string, RequestInit];
  const headers = (init.headers ?? {}) as Record<string, string>;
  return { url, method: init.method ?? "GET", body: init.body === undefined ? undefined : JSON.parse(String(init.body)), headers };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("teamLibraryClient", () => {
  it("GetTeamLibrary_Answer_IsCarriedVerbatim", async () => {
    const mock = stubFetch(respond(LIBRARY));
    const library = await getTeamLibrary("team-a");
    expect(call(mock, 0).url).toBe("/teams/team-a/library");
    expect(library.canChange).toBe(false);
    expect(library.changeRefusal).toBe(LIBRARY.changeRefusal);
    expect(library.items).toEqual(LIBRARY.items);
  });

  it("GetTeamLibrary_ACollaboratorIsRefused_ThrowsWithTheGatewaysSentence", async () => {
    const sentence = "In this team you are a Collaborator, and a Collaborator may not use the team's shared skills and workflows.";
    stubFetch(respond({ error: sentence, code: "team_action_refused" }, 403));
    await expect(getTeamLibrary("team-a")).rejects.toMatchObject({ status: 403, serverReason: sentence });
  });

  it("GetTeamLibrary_AnAppShellInsteadOfData_IsAFailureNotAnEmptyList", async () => {
    stubFetch(respond("<!doctype html><html></html>", 200, "text/html"));
    await expect(getTeamLibrary("team-a")).rejects.toMatchObject({ status: 502 });
  });

  it("GetTeamLibrary_AnswerMissingThePermission_Throws", async () => {
    stubFetch(respond({ team: LIBRARY.team, items: [] }));
    await expect(getTeamLibrary("team-a")).rejects.toMatchObject({ status: 502 });
  });

  it("GetTeamItemText_ASkillAndAWorkflow_ReadTheirOwnRoutes", async () => {
    const mock = stubFetch(respond("# Body", 200, "text/markdown"), respond("# Instructions", 200, "text/markdown"));
    expect(await getTeamItemText("team-a", { id: "release", kind: "Skill" })).toBe("# Body");
    expect(await getTeamItemText("team-a", { id: "flow", kind: "Workflow" })).toBe("# Instructions");
    expect(call(mock, 0).url).toBe("/teams/team-a/skills/release/body");
    expect(call(mock, 1).url).toBe("/teams/team-a/workflows/flow/instructions");
  });

  it("AddTeamSkill_CreatesThenPublishes_AndSendsNoAuthor", async () => {
    const mock = stubFetch(respond({ skillId: "release" }, 201), respond({ id: "release" }));
    await addTeamSkill("team-a", { id: "release", name: "Release", summary: "s", bodyMarkdown: "# b" });
    expect(call(mock, 0)).toMatchObject({ url: "/teams/team-a/skills", method: "POST" });
    expect(call(mock, 0).body).not.toHaveProperty("authoredBy");
    expect(call(mock, 1)).toMatchObject({ url: "/teams/team-a/skills/release/publish", method: "POST" });
  });

  it("AddTeamSkill_ADeveloperIsRefused_ThrowsAndDoesNotPublish", async () => {
    const mock = stubFetch(respond({ error: "a Developer may not change the team's shared skills and workflows." }, 403));
    await expect(addTeamSkill("team-a", { id: "x", name: "X", summary: "s", bodyMarkdown: "b" })).rejects.toMatchObject({ status: 403 });
    expect(mock).toHaveBeenCalledTimes(1);
  });

  it("ChangeTeamItem_ASkill_KeepsEverythingButTheWords_WritesAgainstThePublishedHash_ThenPublishes", async () => {
    const version = {
      name: "Release", summary: "old", triggers: ["release"], bodyMarkdown: "# old", files: [{ fileName: "a.md", content: "x" }],
      license: null, compatibility: null, allowedTools: null, metadata: { k: "v" }, contentHash: "hash-2",
    };
    const mock = stubFetch(respond(version), respond({}), respond({}));
    await changeTeamItem("team-a", { id: "release", kind: "Skill", version: 2 }, { summary: "new", text: "# new" });

    expect(call(mock, 0).url).toBe("/teams/team-a/skills/release/versions/2");
    const put = call(mock, 1);
    expect(put).toMatchObject({ url: "/teams/team-a/skills/release/draft", method: "PUT" });
    expect(put.headers["If-Match"]).toBe("hash-2");
    expect(put.body).toEqual({
      name: "Release", summary: "new", triggers: ["release"], bodyMarkdown: "# new", files: version.files,
      license: null, compatibility: null, allowedTools: null, metadata: { k: "v" },
    });
    expect(call(mock, 2)).toMatchObject({ url: "/teams/team-a/skills/release/publish", method: "POST" });
  });

  it("ChangeTeamItem_AWorkflow_KeepsItsSteps", async () => {
    const steps = [{ name: "Build", description: "d", doer: "Developer", done: "merged" }];
    const version = {
      name: "Flow", summary: "old", whenToUse: "w", humanCheckpoint: "h", steps, instructionsMarkdown: "# old",
      outcomeCriteria: [], files: [{ fileName: "f.md", content: "c", contentHash: "z" }], contentHash: "wf-hash",
    };
    const mock = stubFetch(respond(version), respond({}), respond({}));
    await changeTeamItem("team-a", { id: "flow", kind: "Workflow", version: 1 }, { summary: "new", text: "# new" });
    const put = call(mock, 1);
    expect(put.url).toBe("/teams/team-a/workflows/flow/draft");
    expect(put.headers["If-Match"]).toBe("wf-hash");
    expect(put.body).toMatchObject({ steps, instructionsMarkdown: "# new", summary: "new", files: [{ fileName: "f.md", content: "c" }] });
  });

  it("RemoveTeamItem_DeletesOnTheRightRoute", async () => {
    const mock = stubFetch(respond({ archived: true }), respond({ archived: true }));
    await removeTeamItem("team-a", { id: "release", kind: "Skill" });
    await removeTeamItem("team-a", { id: "flow", kind: "Workflow" });
    expect(call(mock, 0)).toMatchObject({ url: "/teams/team-a/skills/release", method: "DELETE" });
    expect(call(mock, 1)).toMatchObject({ url: "/teams/team-a/workflows/flow", method: "DELETE" });
  });

  it("GetStartingWorkflows_ListsOnlyTheBuiltIns", async () => {
    stubFetch(respond({ workflows: [
      { id: "mission", name: "Mission", summary: "m", isBuiltIn: true },
      { id: "team-own", name: "Own", summary: "o", isBuiltIn: false },
    ] }));
    expect(await getStartingWorkflows("team-a")).toEqual([{ id: "mission", name: "Mission", summary: "m" }]);
  });

  it("AddTeamWorkflowFrom_ClonesUnderTheNewId_WithNoAuthor", async () => {
    const mock = stubFetch(respond({ id: "team-review" }, 201));
    await addTeamWorkflowFrom("team-a", "standalone-with-review", "team-review");
    expect(call(mock, 0)).toMatchObject({ url: "/teams/team-a/workflows/standalone-with-review/clone?newId=team-review", method: "POST" });
    expect(call(mock, 0).url).not.toContain("by=");
  });

  it("TeamIds_AreEncodedIntoThePath", async () => {
    const mock = stubFetch(respond(LIBRARY));
    await getTeamLibrary("a/b");
    expect(call(mock, 0).url).toBe("/teams/a%2Fb/library");
  });
});
