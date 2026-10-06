import { describe, it, expect, vi, afterEach } from "vitest";
import { answerQuestion, getMyQuestions } from "./teamQuestionsClient";
import { gatewayErrorMessage } from "../api/client";

// The questions client (devthrottle_internal#2307): each call reaches its route with the right body, the Gateway's own
// refusal sentence reaches the page, and a Gateway that answers with its own web page - what one with Teams not
// released does - is never read as data.

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("the questions client", () => {
  it("reads the questions waiting on this person from the team's route", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ count: 0, waiting: [], answered: [] }));
    vi.stubGlobal("fetch", fetchMock);

    const list = await getMyQuestions("team 1");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/teams/team%201/questions");
    expect(init.method).toBe("GET");
    expect(list.count).toBe(0);
  });

  it("posts the chosen option, the words as typed and the version shown - and nothing that names the person", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ question: { questionId: "q/1" } }));
    vi.stubGlobal("fetch", fetchMock);

    const answered = await answerQuestion("t", { reportId: "r 1", questionId: "q/1", version: 3 }, "30", "  my words\n");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/teams/t/questions/r%201/q%2F1/answer");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ version: 3, optionValue: "30", comment: "  my words\n" });
    expect(answered.questionId).toBe("q/1");
  });

  it("a refusal carries the Gateway's own sentence", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "You already answered this question.", code: "already_answered" }, 409)));

    const err = await answerQuestion("t", { reportId: "r", questionId: "q", version: 1 }, "14", "").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "send your answer")).toContain("You already answered this question.");
  });

  it("a Gateway that answers with its own web page is not read as data", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("<!doctype html>", { status: 200, headers: { "Content-Type": "text/html" } })));

    await expect(getMyQuestions("t")).rejects.toThrow(/Teams are not available/);
  });
});
