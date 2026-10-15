import { describe, it, expect, vi, afterEach } from "vitest";
import { GatewayError } from "../api/client";
import { cronJobWriteBody, getCronPause, setCronJobEnabled, setCronPause, type CronJob } from "./scheduleClient";

// Pausing from where the state is read (issue #3910). What is held down:
//   * one schedule is paused or resumed to the state the CALLER names - never "the opposite of what is stored" -
//     and the write carries only the fields a person can change, so the Gateway's own scheduling state and display
//     words are never echoed back to it;
//   * pause-everything is one PUT that names the state, and a refusal carries the Gateway's own sentence.

const STORED: CronJob = {
  id: "cj_boss",
  name: "WarmForward Factory - Nora Hale",
  enabled: true,
  scheduleKind: "recurring",
  cronExpression: "0 9 * * *",
  runAt: null,
  timeZoneId: "America/New_York",
  target: { machine: "SOREN_NORTH" },
  action: { repoPath: "D:\\ReposFred\\warmforward-factory", seed: "You are Nora Hale.", workListName: null },
  preventOverlap: true,
  notifyOn: "failure",
  notifyWebhookUrl: "https://example.test/hook",
  createdUtc: "2026-10-01T00:00:00Z",
  lastFiredUtc: "2026-10-12T13:00:00Z",
  nextRunUtc: "2026-10-13T13:00:00Z",
  lastStatus: "started",
  lifecycle: "active",
  stateLabel: "Active",
  toggleLabel: "Pause",
  factory: "warmforward",
  seat: "nora-hale",
};

interface Call {
  url: string;
  method: string;
  body: unknown;
}

function gateway(answers: (call: Call) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const call = { url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(init.body as string) : null };
      calls.push(call);
      return answers(call);
    }),
  );
  return calls;
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

afterEach(() => vi.unstubAllGlobals());

describe("setCronJobEnabled", () => {
  it("pauses a schedule by id: reads it, then writes it back with enabled false and nothing it may not change", async () => {
    const calls = gateway((c) => json(c.method === "GET" ? STORED : { ...STORED, enabled: false }));

    const saved = await setCronJobEnabled("cj_boss", false);

    expect(saved.enabled).toBe(false);
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual(["GET /cron/jobs/cj_boss", "PUT /cron/jobs/cj_boss"]);
    const body = calls[1].body as Record<string, unknown>;
    expect(body.enabled).toBe(false);
    // Every setting it does not change goes back as it was.
    expect(body).toMatchObject({
      name: STORED.name,
      cronExpression: "0 9 * * *",
      timeZoneId: "America/New_York",
      preventOverlap: true,
      notifyOn: "failure",
      notifyWebhookUrl: "https://example.test/hook",
    });
    // The Gateway's own state and words are never echoed back to it.
    for (const readOnly of ["nextRunUtc", "lastFiredUtc", "lastStatus", "createdUtc", "lifecycle", "stateLabel", "toggleLabel"])
      expect(body).not.toHaveProperty(readOnly);
  });

  it("resumes to the state the caller names, even when the stored schedule is already in it", async () => {
    // A tap on a stale row: the page said Paused and offered Resume, the schedule was resumed elsewhere meanwhile.
    // The write still says enabled true - it never flips whatever happens to be stored.
    const calls = gateway(() => json(STORED));

    await setCronJobEnabled("cj_boss", true);

    expect((calls[1].body as { enabled: boolean }).enabled).toBe(true);
  });

  it("writes nothing when the schedule cannot be read, and says why in the Gateway's words", async () => {
    const calls = gateway(() => json({ error: "no such cron job" }, 404));

    await expect(setCronJobEnabled("cj_gone", false)).rejects.toThrow(/no such cron job/);
    expect(calls).toHaveLength(1);
  });
});

describe("cronJobWriteBody", () => {
  it("applies the override last and keeps the rest", () => {
    const body = cronJobWriteBody(STORED, { enabled: false });

    expect(body.enabled).toBe(false);
    expect(body.id).toBe("");
    expect(body.action).toEqual({ repoPath: STORED.action.repoPath, seed: STORED.action.seed, workListName: null });
  });

  it("sends a schedule's autoDismiss back as stored, so a pause changes nothing but enabled", () => {
    const stored: CronJob = { ...STORED, action: { ...STORED.action, autoDismiss: false } };

    const body = cronJobWriteBody(stored, { enabled: false });

    expect(JSON.parse(JSON.stringify(body)).action).toEqual({
      repoPath: STORED.action.repoPath,
      seed: STORED.action.seed,
      workListName: null,
      autoDismiss: false,
    });
  });
});

describe("the pause-everything switch", () => {
  const PAUSED = {
    paused: true,
    sinceUtc: "2026-10-12T14:00:00Z",
    banner: "Everything is paused (fixture).",
    actionLabel: "Resume everything",
    actionTitle: "Let schedules start again (fixture).",
  };

  it("reads the switch as the Gateway answers it", async () => {
    const calls = gateway(() => json(PAUSED));

    expect(await getCronPause()).toEqual(PAUSED);
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual(["GET /cron/pause"]);
  });

  it.each([true, false])("sets it with one PUT that names the state (%s)", async (paused) => {
    const calls = gateway(() => json({ ...PAUSED, paused }));

    expect((await setCronPause(paused)).paused).toBe(paused);
    expect(calls).toEqual([{ url: "/cron/pause", method: "PUT", body: { paused } }]);
  });

  it("carries the Gateway's own sentence when it refuses", async () => {
    gateway(() => json({ error: "Pausing or resuming every schedule is the owner's control." }, 403));

    const refusal = await setCronPause(true).catch((e: unknown) => e);

    expect(refusal).toBeInstanceOf(GatewayError);
    expect((refusal as GatewayError).status).toBe(403);
    expect((refusal as GatewayError).message).toContain("Pausing or resuming every schedule is the owner's control.");
  });
});
