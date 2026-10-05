// The questions waiting on a member of a team (devthrottle_internal#2307, screen S8): the routes under
// /teams/{teamId}/questions, as the Cockpit's Questions page reads them.
//
// A question is in a dev report its author sent to this person. They answer it by PICKING ONE OPTION. The choice goes
// to the session that asked; anything they write goes to the person who asked, never to an agent. Both of those are
// the Gateway's doing - this client only carries the choice and the words to it.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Every word the page shows - who asked, the recommended mark, where the words
// go, what became of an answer - and whether a question can still be answered or commented on, arrives finished from
// the Gateway. A page renders these verbatim; it never works out what a state means.
import { call } from "./invitationsClient";

/** One option of a question. */
export interface QuestionOption {
  value: string;
  label: string;
  /** True on the one option the report recommends. */
  recommended: boolean;
}

/** What became of this person's answer to a question. */
export interface QuestionAnswer {
  optionValue: string;
  optionLabel: string;
  /** "You chose ..." */
  chosenLabel: string;
  /** Where the answer stands for the session, in the Gateway's words: held, delivered. */
  statusLabel: string;
  atUtc: string;
  /** The words this person wrote with it, read back to them only; null when they wrote none. */
  yourComment: string | null;
  /** Where those words went, or null. */
  yourCommentLabel: string | null;
}

/** One question, as the Gateway describes it to this person. */
export interface TeamQuestion {
  reportId: string;
  /** The version of the report this person holds - an answer names it. */
  version: number;
  reportTitle: string;
  questionId: string;
  question: string;
  options: QuestionOption[];
  /** The word drawn beside the recommended option. */
  recommendedLabel: string;
  /** Who asked: the person behind the session that wrote the report. */
  askedBy: string;
  askedAtUtc: string;
  /** Whether this person may answer it now. */
  canAnswer: boolean;
  /** Whether a comment box is offered. */
  canComment: boolean;
  commentPlaceholder: string;
  /** Beside the send button: where the words go, or why none can be sent. */
  commentNote: string;
  sendLabel: string;
  /** Their answer, or null while it waits on them. */
  answer: QuestionAnswer | null;
}

export interface TeamQuestions {
  /** How many wait on this person. */
  count: number;
  /** The line under the page's title. */
  subtitle: string;
  /** What the page says when nothing waits. */
  emptyText: string;
  waiting: TeamQuestion[];
  answered: TeamQuestion[];
  answeredHeading: string;
}

const base = (teamId: string) => `/teams/${encodeURIComponent(teamId)}/questions`;

/** GET /teams/{teamId}/questions - the questions waiting on this person, and the ones they answered. */
export function getMyQuestions(teamId: string, signal?: AbortSignal): Promise<TeamQuestions> {
  return call<TeamQuestions>("GET", base(teamId), "load your questions", undefined, signal);
}

/** POST /teams/{teamId}/questions/{reportId}/{questionId}/answer - answer by choice. The comment, when there is one,
 *  goes to the person who asked; the Gateway never puts it in front of an agent. Answers the question as it now stands. */
export async function answerQuestion(
  teamId: string,
  q: { reportId: string; questionId: string; version: number },
  optionValue: string,
  comment: string,
  signal?: AbortSignal,
): Promise<TeamQuestion> {
  const path = `${base(teamId)}/${encodeURIComponent(q.reportId)}/${encodeURIComponent(q.questionId)}/answer`;
  const body = await call<{ question: TeamQuestion }>("POST", path, "send your answer", { version: q.version, optionValue, comment }, signal);
  return body.question;
}
