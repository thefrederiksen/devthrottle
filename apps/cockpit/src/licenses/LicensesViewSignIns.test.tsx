// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";

// THE SIGN-INS OF THE LICENSES PAGE, AT THE WIRE: who approves an account (read, set, remove), Add a spare and Renew
// now.
//
// Nothing of the page or of its client is replaced here - only the browser's fetch. So what these tests see is what
// leaves the browser (the route, the method, the content type, the body) and what a person then reads, worded by the
// same code that words it in the Cockpit. The answers are the Gateway's own examples (fixtures.ts). What they pin:
//   - each of the four writes goes to its own route, by its own method; a write with a body is application/json and
//     its body is exactly the fields the Gateway reads, and the removal names its account in the query with no body;
//   - nothing is sent until the person confirms, and Add a spare cannot be confirmed before an account is typed;
//   - a refusal - 400, 403 owner_only, 404, 409 - is shown in the Gateway's sentence, word for word, where the person
//     is looking, and what they typed stays;
//   - whether Add a spare or Renew now is offered is the Gateway's, per machine and per sign-in, and its sentence is
//     shown whole whether it is offered or not;
//   - a started sign-in is shown in the Gateway's sentences, and how it ended is read from its row of the log, never
//     asked for again;
//   - after an approver is set or removed the page shows the Gateway's sentence and reads the approvers again: it
//     keeps no list of its own;
//   - the approver form is four pick lists and nothing typed; the browser profiles are read from the Director that
//     reports the chosen machine, and Save pressed with a choice missing sends nothing and names each one;
//   - each approver row shows the Gateway's own sentences - who made it, why a sign-in cannot reach the machine, the
//     profile's health and the one thing to do - each once, and nothing worked out from canApproveNow.
import { LicensesView } from "./LicensesView";
import {
  APPROVERS,
  APPROVERS_EMPTY,
  APPROVER_REFUSED,
  APPROVER_REMOVED,
  APPROVER_REMOVE_OWNER_ONLY,
  APPROVER_SET,
  APPROVER_SET_OWNER_ONLY,
  FORECAST,
  LOG_PAGE,
  OVERVIEW,
  OVERVIEW_SIGN_IN_FIELDS,
  SIGN_IN_ALREADY_HELD,
  SIGN_IN_DIRECTOR_OFFLINE,
  SIGN_IN_LOG,
  SIGN_IN_OWNER_ONLY,
  SIGN_IN_RUNNING,
  SIGN_IN_STARTED,
  SIGN_IN_UNKNOWN_MACHINE,
  copyOf,
} from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";
const WINDOWS = "the-windows-machine";
const MAC = "the-mac";
const APPROVERS_ROUTE = "/gateway/licenses/approvers";
const ADD_SPARE = "POST /gateway/licenses/sign-ins/add-spare";
const RENEW = "POST /gateway/licenses/sign-ins/renew";
const SET_APPROVER = `PUT ${APPROVERS_ROUTE}`;
const REMOVE_APPROVER = `DELETE ${APPROVERS_ROUTE}`;
const READ_APPROVERS = `GET ${APPROVERS_ROUTE}`;

// The Gateway's refusal of each write to a caller that is not the owner's own signed-in phone or browser.
const START_OWNER_ONLY = SIGN_IN_OWNER_ONLY;
const SET_OWNER_ONLY = APPROVER_SET_OWNER_ONLY;
const REMOVE_OWNER_ONLY = APPROVER_REMOVE_OWNER_ONLY;

interface Sent {
  call: string;
  url: string;
  contentType: string | null;
  text: string | null;
}

type Answer = () => Response | Promise<Response>;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let sent: Sent[];
let answers: Record<string, Answer>;
let overview: typeof OVERVIEW;

/** Every write the page sent to a sign-in route. */
function writes(): Sent[] {
  return sent.filter((s) => [ADD_SPARE, RENEW, SET_APPROVER, REMOVE_APPROVER].includes(s.call));
}

function reads(call: string): number {
  return sent.filter((s) => s.call === call).length;
}

/**
 * The example overview with the Gateway's real example of the sign-in fields laid over it, each on the account, the
 * machine and the sign-in it names. After it: the-windows-machine is offered Add a spare, is renewing its sign-in for
 * account A (so Renew now is not offered there), and is offered Renew now for its spare of account B; the-mac's
 * Director is not connected, so nothing is offered on it; accounts A and B have an approver and account C has none.
 */
function withSignInFields(base: typeof OVERVIEW): typeof OVERVIEW {
  const laid = copyOf(base);
  for (const fields of OVERVIEW_SIGN_IN_FIELDS.accounts) {
    const account = laid.accounts.find((a) => a.accountEmail === fields.accountEmail);
    if (account === undefined) throw new Error(`the example overview has no ${fields.accountEmail}`);
    Object.assign(account, copyOf(fields));
  }
  for (const fields of OVERVIEW_SIGN_IN_FIELDS.machines) {
    const machine = laid.machines.find((m) => m.machineName === fields.machineName);
    if (machine === undefined) throw new Error(`the example overview has no ${fields.machineName}`);
    machine.canAddSpare = fields.canAddSpare;
    machine.addSpareText = fields.addSpareText;
    machine.signInsInProgress = copyOf(fields.signInsInProgress);
    const held = machine.active === null ? machine.spares : [machine.active, ...machine.spares];
    for (const one of fields.active === null ? fields.spares : [fields.active, ...fields.spares]) {
      const holding = held.find((h) => h.accountEmail === one.accountEmail);
      // The two pictures differ: the example's the-mac is on account C, which this overview's the-mac does not hold.
      if (holding !== undefined) Object.assign(holding, { canRenewNow: one.canRenewNow, renewNowText: one.renewNowText });
    }
  }
  return laid;
}

function machineId(machineName: string): string {
  const machine = overview.machines.find((m) => m.machineName === machineName);
  if (machine === undefined) throw new Error(`no ${machineName}`);
  return machine.machineId;
}

beforeEach(() => {
  sent = [];
  overview = withSignInFields(OVERVIEW);
  answers = {
    "GET /gateway/licenses": () => json(200, overview),
    "GET /gateway/licenses/forecast": () => json(200, FORECAST),
    "GET /gateway/licenses/log": () => json(200, LOG_PAGE),
    [READ_APPROVERS]: () => json(200, APPROVERS),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const url = String(input);
      const call = `${init?.method ?? "GET"} ${url.split("?")[0]}`;
      const text = typeof init?.body === "string" ? init.body : null;
      sent.push({ call, url, contentType: new Headers(init?.headers).get("Content-Type"), text });
      const answer = answers[call];
      // Anything else the Cockpit sends (its own error reports) is answered and ignored.
      return answer === undefined ? json(200, {}) : answer();
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function card(accountEmail: string): Promise<HTMLElement> {
  return await screen.findByRole("region", { name: accountEmail });
}

/** The line of one machine's sign-in on the card of an account. */
async function holding(accountEmail: string, machineLabel: string): Promise<HTMLElement> {
  const lines = within(await card(accountEmail)).getAllByTestId("lic-holding");
  const line = lines.find((l) => l.querySelector(".lic-holding-machine")?.textContent === machineLabel);
  if (line === undefined) throw new Error(`no sign-in for ${accountEmail} on ${machineLabel}`);
  return line;
}

/** The row of one machine in the Sign-ins table. */
async function machineRow(label: string): Promise<HTMLElement> {
  const rows = await screen.findAllByTestId("lic-sign-in-machine");
  const row = rows.find((r) => r.querySelector(".lic-strong")?.textContent === label);
  if (row === undefined) throw new Error(`no row for ${label}`);
  return row;
}

function refusalIn(dialog: HTMLElement): string | null {
  return dialog.querySelector(".ui-confirm-error")?.textContent ?? null;
}

describe("LicensesView: what the overview says of sign-ins", () => {
  it("shows on each card who approves the account, in the Gateway's sentence and tone, and adds no word", async () => {
    render(<LicensesView />);

    const a = within(await card(ACCOUNT_A)).getByTestId("lic-account-approver");
    expect(a.textContent).toBe(OVERVIEW_SIGN_IN_FIELDS.accounts[0].approverText);
    expect(a.className).toContain("lic-tone-warning");
    const c = within(await card(ACCOUNT_C)).getByTestId("lic-account-approver");
    expect(c.textContent).toBe("No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.");
    expect(c.className).toContain("lic-tone-neutral");
    expect(c.className).not.toContain("lic-tone-warning");
  });

  it("offers Renew now only on the sign-in the Gateway offers it for, and shows its sentence on every sign-in", async () => {
    render(<LicensesView />);

    // The spare of account B on the-windows-machine: offered, with what it does.
    const offered = await holding(ACCOUNT_B, WINDOWS);
    expect(within(offered).getByRole("button", { name: "Renew now" })).toBeTruthy();
    expect(within(offered).getByTestId("lic-holding-renew-text").textContent).toBe(
      "Signs the-windows-machine in to account-b@example.com again, replacing the sign-in it holds.",
    );
    // The same account on the-mac, whose Director is not connected: not offered, with why.
    const offline = await holding(ACCOUNT_B, MAC);
    expect(within(offline).queryByRole("button", { name: "Renew now" })).toBeNull();
    expect(within(offline).getByTestId("lic-holding-renew-text").textContent).toBe(
      "The Director on the-mac is not connected, so a sign-in cannot be started there.",
    );
    // Account A on the-windows-machine, where a renewal is already running: not offered, with why.
    const running = await holding(ACCOUNT_A, WINDOWS);
    expect(within(running).queryByRole("button", { name: "Renew now" })).toBeNull();
    expect(within(running).getByTestId("lic-holding-renew-text").textContent).toBe(
      "A sign-in for account-a@example.com on the-windows-machine is already running.",
    );
    expect(screen.getAllByRole("button", { name: "Renew now" }).length).toBe(1);
  });

  it("lists every machine with the Gateway's Add a spare sentence, the button only where it is offered, and the sign-ins that have not ended", async () => {
    render(<LicensesView />);

    const windows = await machineRow(WINDOWS);
    expect(within(windows).getByRole("button", { name: "Add a spare" })).toBeTruthy();
    expect(within(windows).getByTestId("lic-add-spare-text").textContent).toBe(
      "Signs the-windows-machine in to another account, to be held there as a spare.",
    );
    expect(within(windows).getAllByTestId("lic-sign-in-progress").map((line) => line.textContent)).toEqual([
      "the-windows-machine is renewing its sign-in for account-a@example.com.",
    ]);
    const mac = await machineRow(MAC);
    expect(within(mac).queryByRole("button", { name: "Add a spare" })).toBeNull();
    expect(within(mac).getByTestId("lic-add-spare-text").textContent).toBe(
      "The Director on the-mac is not connected, so a sign-in cannot be started there.",
    );
    expect(within(mac).queryByTestId("lic-sign-in-progress")).toBeNull();
    expect(screen.getAllByTestId("lic-sign-in-machine").length).toBe(2);
    expect(writes()).toEqual([]);
  });

  it("calls a machine by the Gateway's label in the Sign-ins table and in the confirmation", async () => {
    // Two users of one computer are two machines; the Gateway tells them apart in the label.
    const windows = overview.machines.find((m) => m.machineName === WINDOWS);
    if (windows === undefined) throw new Error("no the-windows-machine");
    windows.label = "the-windows-machine (owner)";
    render(<LicensesView />);

    fireEvent.click(within(await machineRow("the-windows-machine (owner)")).getByRole("button", { name: "Add a spare" }));

    expect(screen.getByRole("alertdialog", { name: "Add a spare on the-windows-machine (owner)?" })).toBeTruthy();
  });
});

describe("LicensesView: Renew now at the wire", () => {
  /** Open the Renew now confirmation of account B's spare on the-windows-machine and return it. */
  async function openRenew(): Promise<HTMLElement> {
    fireEvent.click(within(await holding(ACCOUNT_B, WINDOWS)).getByRole("button", { name: "Renew now" }));
    return screen.getByRole("alertdialog", { name: "Renew the sign-in for account-b@example.com on the-windows-machine?" });
  }

  function confirm(dialog: HTMLElement): HTMLButtonElement {
    return within(dialog).getByRole("button", { name: "Renew now" }) as HTMLButtonElement;
  }

  it("sends nothing until confirmed, then one application/json POST naming the account and the machine, and shows the Gateway's answer", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openRenew();
    // The confirmation says what will happen in the Gateway's sentence for that sign-in.
    expect(dialog.querySelector(".ui-confirm-message")?.textContent).toBe(
      "Signs the-windows-machine in to account-b@example.com again, replacing the sign-in it holds.",
    );
    expect(writes()).toEqual([]);

    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(RENEW);
    expect(writes()[0].contentType).toBe("application/json");
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_B, machineId: machineId(WINDOWS) }));
    // The whole outcome so far in the Gateway's one sentence, then the sign-in's row: the Gateway's label, a space and
    // the Gateway's sentence. The page adds no word anywhere in it.
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    const row = within(answer).getByTestId("lic-sign-in-answer-row");
    expect(row.textContent).toBe("Signing in the-windows-machine is renewing its sign-in for account-a@example.com.");
    expect(row.querySelector(".lic-pill")?.className).toContain("lic-tone-neutral");
    expect(within(answer).queryByTestId("lic-sign-in-answer-reason")).toBeNull();
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("reads how the sign-in ended from its row of the log, with its reason, and never starts it again", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openRenew();
    // By the time the page reads the log again the sign-in has failed: the Gateway's own example of a failed
    // sign-in, on the row the answer named.
    const ended = copyOf(SIGN_IN_LOG);
    ended.entries = [{ ...ended.entries[2], entryId: SIGN_IN_STARTED.entry.entryId }];
    answers["GET /gateway/licenses/log"] = () => json(200, ended);

    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    await waitFor(() => expect(within(answer).getByTestId("lic-sign-in-answer-row").textContent).toContain("Failed"));
    const row = within(answer).getByTestId("lic-sign-in-answer-row");
    expect(row.textContent).toBe("Failed The sign-in for account-b@example.com on the-windows-machine was not renewed.");
    expect(row.querySelector(".lic-pill")?.className).toContain("lic-tone-bad");
    expect(within(answer).getByTestId("lic-sign-in-answer-reason").textContent).toBe(
      "The machine set to approve this account had no Director connected.",
    );
    // What was started is still said as the Gateway said it.
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    expect(writes().length).toBe(1);
  });

  it.each([
    ["409 sign_in_running", 409, SIGN_IN_RUNNING],
    ["409 director_offline", 409, SIGN_IN_DIRECTOR_OFFLINE],
    ["404 unknown_machine", 404, SIGN_IN_UNKNOWN_MACHINE],
    ["403 owner_only", 403, START_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps the confirmation open", async (_name, status, refusal) => {
    answers[RENEW] = () => json(status, refusal);
    render(<LicensesView />);
    const dialog = await openRenew();

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(refusal.error);
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("takes the answer to a started sign-in off the screen when it is dismissed", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    fireEvent.click(confirm(await openRenew()));
    const answer = await screen.findByTestId("lic-sign-in-answer");

    fireEvent.click(within(answer).getByRole("button", { name: "Dismiss" }));

    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("sends nothing when the confirmation is cancelled", async () => {
    render(<LicensesView />);
    const dialog = await openRenew();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(writes()).toEqual([]);
  });
});

describe("LicensesView: Add a spare at the wire", () => {
  /** Open the Add a spare confirmation of the-windows-machine and return it. */
  async function openAddSpare(): Promise<HTMLElement> {
    fireEvent.click(within(await machineRow(WINDOWS)).getByRole("button", { name: "Add a spare" }));
    return screen.getByRole("alertdialog", { name: "Add a spare on the-windows-machine?" });
  }

  function confirm(dialog: HTMLElement): HTMLButtonElement {
    return within(dialog).getByRole("button", { name: "Add a spare" }) as HTMLButtonElement;
  }

  function accountField(dialog: HTMLElement): HTMLInputElement {
    return within(dialog).getByLabelText("Claude account to sign in to") as HTMLInputElement;
  }

  it("cannot be confirmed and sends nothing before an account is typed, then sends that account and the machine", async () => {
    answers[ADD_SPARE] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openAddSpare();
    // The Gateway's sentence for the machine, and an empty field: which account is the person's own choice.
    expect(within(dialog).getByText("Signs the-windows-machine in to another account, to be held there as a spare.")).toBeTruthy();
    expect(accountField(dialog).value).toBe("");
    expect(confirm(dialog).disabled).toBe(true);
    fireEvent.click(confirm(dialog));
    // Space alone is not an account.
    fireEvent.change(accountField(dialog), { target: { value: "   " } });
    expect(confirm(dialog).disabled).toBe(true);
    fireEvent.click(confirm(dialog));
    await Promise.resolve();
    expect(writes()).toEqual([]);

    fireEvent.change(accountField(dialog), { target: { value: " account-d@example.com " } });
    expect(confirm(dialog).disabled).toBe(false);
    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(ADD_SPARE);
    expect(writes()[0].contentType).toBe("application/json");
    // The account as typed, less the space around it; the machine by the identifier the overview gave.
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: "account-d@example.com", machineId: machineId(WINDOWS) }));
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("shuts the account field while the call is out, and opens it again with what was typed when the Gateway refuses", async () => {
    let answerNow: (response: Response) => void = () => undefined;
    answers[ADD_SPARE] = () => new Promise<Response>((resolve) => (answerNow = resolve));
    render(<LicensesView />);
    const dialog = await openAddSpare();
    expect(accountField(dialog).disabled).toBe(false);
    fireEvent.change(accountField(dialog), { target: { value: ACCOUNT_A } });

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(accountField(dialog).disabled).toBe(true));
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    answerNow(json(409, SIGN_IN_ALREADY_HELD));
    await waitFor(() => expect(accountField(dialog).disabled).toBe(false));
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    expect(writes().length).toBe(1);
  });

  it("opens empty every time: the account typed for one machine is not carried to the next", async () => {
    render(<LicensesView />);
    const first = await openAddSpare();
    fireEvent.change(accountField(first), { target: { value: "account-d@example.com" } });
    fireEvent.click(within(first).getByRole("button", { name: "Cancel" }));

    const second = await openAddSpare();

    expect(accountField(second).value).toBe("");
    expect(confirm(second).disabled).toBe(true);
    expect(writes()).toEqual([]);
  });

  it.each([
    ["409 already_held", 409, SIGN_IN_ALREADY_HELD],
    ["409 director_offline", 409, SIGN_IN_DIRECTOR_OFFLINE],
    ["403 owner_only", 403, START_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps what was typed", async (_name, status, refusal) => {
    answers[ADD_SPARE] = () => json(status, refusal);
    render(<LicensesView />);
    const dialog = await openAddSpare();
    fireEvent.change(accountField(dialog), { target: { value: ACCOUNT_A } });

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(refusal.error);
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });
});

describe("LicensesView: who approves the sign-ins of an account", () => {
  const MAC_PROFILES = "GET /directors/dir-mac/browsers";
  const WINDOWS_PROFILES = "GET /directors/dir-windows/browsers";
  const GUEST_PROFILES = "GET /directors/dir-windows-guest/browsers";

  beforeEach(() => {
    // Both Directors are connected, so the Gateway says their browser profiles can be listed. A second user of
    // the-windows-machine reports too, from its own Director, so that machine has two users to choose from.
    for (const machine of overview.machines) {
      machine.canListBrowserProfiles = true;
      machine.browserProfilesText = `The browser profiles of ${machine.label}'s Director.`;
    }
    const windows = overview.machines.find((m) => m.machineName === WINDOWS);
    if (windows === undefined) throw new Error(`no ${WINDOWS}`);
    overview.machines.push({
      ...copyOf(windows),
      machineId: "guest-machine-id",
      osUser: "guest",
      label: `${WINDOWS} (guest)`,
      directorId: "dir-windows-guest",
      browserProfilesText: `The browser profiles of ${WINDOWS} (guest)'s Director.`,
    });
    answers[MAC_PROFILES] = () => json(200, { browsers: [{ id: "work", name: "work" }, { id: "personal", name: "Personal" }] });
    answers[WINDOWS_PROFILES] = () => json(200, { browsers: [{ id: "work", name: "work" }] });
    answers[GUEST_PROFILES] = () => json(200, { browsers: [{ id: "guest-profile", name: "Guest" }] });
  });

  function editor(): HTMLElement {
    return screen.getByTestId("lic-approver-editor");
  }

  function field(label: string): HTMLSelectElement {
    return within(editor()).getByLabelText(label) as HTMLSelectElement;
  }

  const ACCOUNT = "Claude account";
  const MACHINE = "Approving machine";
  const USER = "Operating system user on that machine";
  const PROFILE = "Browser profile on that machine";

  function choose(label: string, value: string): void {
    fireEvent.change(field(label), { target: { value } });
  }

  /** The values a pick list offers, less its "Choose ..." line. */
  function offered(label: string): string[] {
    return [...field(label).options].map((option) => option.value).filter((value) => value !== "");
  }

  /** Choose all four, waiting for the machine's browser profiles to be listed before choosing one. */
  async function chooseAll(account: string, machine: string, user: string, profile: string): Promise<void> {
    choose(ACCOUNT, account);
    choose(MACHINE, machine);
    choose(USER, user);
    await waitFor(() => expect(offered(PROFILE)).toContain(profile));
    choose(PROFILE, profile);
  }

  function saveButton(): HTMLButtonElement {
    return within(editor()).getByRole("button", { name: "Save" }) as HTMLButtonElement;
  }

  function missingLines(): string[] {
    const list = within(editor()).queryByTestId("lic-approver-missing");
    return list === null ? [] : [...list.querySelectorAll("li")].map((line) => line.textContent ?? "");
  }

  function profilesText(): string | null {
    return within(editor()).queryByTestId("lic-approver-profiles")?.textContent ?? null;
  }

  async function openNew(): Promise<void> {
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
  }

  it("shows the Gateway's sentence in place of the list when no approver is set", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-approvers-empty")).textContent).toBe(APPROVERS_EMPTY.emptyText);
    expect(screen.queryByTestId("lic-approvers")).toBeNull();
    expect(screen.queryByTestId("lic-approver")).toBeNull();
  });

  it("takes whether the list is empty from the Gateway's own word for it, not from counting the rows", async () => {
    // The Gateway never sends an answer whose word and rows disagree; these two are made to, so that which of the
    // two the page goes by can be seen.
    const saidEmpty = { ...copyOf(APPROVERS), isEmpty: true, emptyText: APPROVERS_EMPTY.emptyText };
    answers[READ_APPROVERS] = () => json(200, saidEmpty);
    const first = render(<LicensesView />);
    expect((await screen.findByTestId("lic-approvers-empty")).textContent).toBe(APPROVERS_EMPTY.emptyText);
    expect(screen.queryByTestId("lic-approver")).toBeNull();
    first.unmount();

    const saidNotEmpty = { ...copyOf(APPROVERS_EMPTY), isEmpty: false, emptyText: null };
    answers[READ_APPROVERS] = () => json(200, saidNotEmpty);
    render(<LicensesView />);
    expect(await screen.findByTestId("lic-approvers")).not.toBeNull();
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
    expect(screen.queryByTestId("lic-approver")).toBeNull();
  });

  /** The status cell's sentences of one row, by what each one is; null for one the row does not draw. */
  function sentences(row: HTMLElement): Record<string, string | null> {
    const of = (id: string) => within(row).queryByTestId(id)?.textContent ?? null;
    return {
      status: of("lic-approver-status"),
      source: of("lic-approver-source"),
      reach: of("lic-approver-reach"),
      health: of("lic-approver-health"),
      todo: of("lic-approver-todo"),
    };
  }

  /** The approvers answer with account A's row changed, as the Gateway words that state (its own tests' sentences). */
  function withRowA(change: Partial<(typeof APPROVERS.approvers)[number]>): typeof APPROVERS {
    const answer = copyOf(APPROVERS);
    Object.assign(answer.approvers[0], change);
    return answer;
  }

  // The Gateway's words for an approver whose last check found the approval page asking for a fresh sign-in
  // (LicenseSignInEndpointsTests.AnApproverThatNeedsAFreshSignIn_CannotApprove_AndTheRowSaysTheOneThingToDo).
  const NEEDS_FRESH_SIGN_IN = {
    canApproveNow: false,
    status: "cannot-approve",
    statusText: "Cannot approve",
    statusTone: "warning" as const,
    source: "owner",
    sourceText: "Set by you.",
    reachText: null,
    state: "needs-fresh-sign-in",
    healthText: 'The approver for account-a@example.com (browser profile "work" on the-mac) needs a fresh claude.ai sign-in on the approval page.',
    whatToDo: "Open that browser on the-mac and sign in to claude.ai once.",
  };

  it("lists each approver with the Gateway's label, the profile, and the status in its tone", async () => {
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");
    expect(rows.length).toBe(2);
    const cells = (row: HTMLElement) => [...row.querySelectorAll("td")].slice(0, 3).map((cell) => cell.textContent);
    expect(cells(rows[0])).toEqual([ACCOUNT_A, "the-mac", "work"]);
    expect(within(rows[0]).getByTestId("lic-approver-status").className).toContain("lic-tone-good");
    expect(cells(rows[1])).toEqual([ACCOUNT_B, "the-laptop", "personal"]);
    expect(within(rows[1]).getByTestId("lic-approver-status").className).toContain("lic-tone-warning");
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("shows a healthy registered row in the Gateway's words: Ready, who registered it, its health, and nothing to do", async () => {
    render(<LicensesView />);

    const row = (await screen.findAllByTestId("lic-approver"))[0];

    const given = APPROVERS.approvers[0];
    expect(sentences(row)).toEqual({ status: "Ready", source: given.sourceText, reach: null, health: given.healthText, todo: null });
    expect(given.sourceText).toBe("Registered by the-mac's Director.");
  });

  it("shows a row that needs a fresh sign-in with the Gateway's health sentence and the one thing to do", async () => {
    answers[READ_APPROVERS] = () => json(200, withRowA(NEEDS_FRESH_SIGN_IN));
    render(<LicensesView />);

    const row = (await screen.findAllByTestId("lic-approver"))[0];

    expect(sentences(row)).toEqual({
      status: "Cannot approve",
      source: "Set by you.",
      reach: null,
      health: NEEDS_FRESH_SIGN_IN.healthText,
      todo: NEEDS_FRESH_SIGN_IN.whatToDo,
    });
    expect(within(row).getByTestId("lic-approver-status").className).toContain("lic-tone-warning");
  });

  it("shows a row that has not been checked, on a machine that never reported, with why and the one thing to do", async () => {
    render(<LicensesView />);

    const row = (await screen.findAllByTestId("lic-approver"))[1];

    const given = APPROVERS.approvers[1];
    expect(sentences(row)).toEqual({
      status: "Not reporting yet",
      source: "Set by you.",
      reach: given.reachText,
      health: given.healthText,
      todo: given.whatToDo,
    });
  });

  it("says each sentence once: the row's sentences together are the Gateway's whole text, with nothing of the page's own", async () => {
    answers[READ_APPROVERS] = () => json(200, withRowA(NEEDS_FRESH_SIGN_IN));
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");

    // Row B: the Gateway's text is who approves it, then why a sign-in cannot reach it; the row says the second part
    // once (reach) and adds the health and what to do, which the text does not carry for an unreachable machine.
    const b = APPROVERS.approvers[1];
    expect(b.text.endsWith(` ${b.reachText}`)).toBe(true);
    // Row A: the Gateway's text for a profile that cannot approve is its health and what to do; the row shows exactly
    // those two, and no reach sentence, because the machine is reachable.
    const statusCell = rows[0].querySelectorAll("td")[3];
    expect(statusCell.textContent).toBe(
      `Cannot approve${NEEDS_FRESH_SIGN_IN.sourceText}${NEEDS_FRESH_SIGN_IN.healthText}${NEEDS_FRESH_SIGN_IN.whatToDo}`,
    );
  });

  it("never turns canApproveNow into words: the status shown is the Gateway's statusText, whatever canApproveNow says", async () => {
    // The relay asks a connected approver whatever its health, so the page promises nothing from canApproveNow.
    answers[READ_APPROVERS] = () => json(200, withRowA({ ...NEEDS_FRESH_SIGN_IN, canApproveNow: true }));
    render(<LicensesView />);

    const row = (await screen.findAllByTestId("lic-approver"))[0];

    expect(sentences(row).status).toBe("Cannot approve");
    expect(row.textContent).not.toContain("Ready");
  });

  it("shows the profile's display name once a check has seen it, and always the identifier the record holds", async () => {
    answers[READ_APPROVERS] = () => json(200, withRowA({ browserProfile: "work-chrome", browserProfileName: "Work Chrome" }));
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");

    expect(within(rows[0]).getByTestId("lic-approver-profile-name").textContent).toBe("Work Chrome");
    expect(within(rows[0]).getByTestId("lic-approver-profile-id").textContent).toBe("work-chrome");
    // No check has named row B's profile: the identifier alone, with no name of the page's own.
    expect(within(rows[1]).queryByTestId("lic-approver-profile-name")).toBeNull();
    expect(within(rows[1]).getByTestId("lic-approver-profile-id").textContent).toBe("personal");
  });

  it("offers Remove on a registered row: one DELETE naming its account, after the confirmation", async () => {
    answers[REMOVE_APPROVER] = () => json(200, APPROVER_REMOVED);
    render(<LicensesView />);
    const row = (await screen.findAllByTestId("lic-approver"))[0];
    expect(sentences(row).source).toBe("Registered by the-mac's Director.");

    fireEvent.click(within(row).getByRole("button", { name: "Remove" }));
    const dialog = screen.getByRole("alertdialog", { name: `Remove the approver of ${ACCOUNT_A}?` });
    expect(writes()).toEqual([]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await screen.findByTestId("lic-approver-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].url).toBe("/gateway/licenses/approvers?accountEmail=account-a%40example.com");
  });

  it("calls an approving machine by the Gateway's label, not by its machine name", async () => {
    // Two users of one computer are two machines; the Gateway tells them apart in the label.
    const labelled = copyOf(APPROVERS);
    labelled.approvers[0].label = "the-mac (owner)";
    answers[READ_APPROVERS] = () => json(200, labelled);
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");
    expect(rows[0].querySelectorAll("td")[1].textContent).toBe("the-mac (owner)");
  });

  it("offers four pick lists and nothing to type: the page's accounts, the machines that reported, and their users", async () => {
    render(<LicensesView />);
    await openNew();

    expect(editor().querySelectorAll("input, textarea").length).toBe(0);
    expect(offered(ACCOUNT)).toEqual([ACCOUNT_A, ACCOUNT_B, ACCOUNT_C]);
    expect(offered(MACHINE)).toEqual([MAC, WINDOWS]);
    choose(MACHINE, WINDOWS);
    expect(offered(USER)).toEqual(["owner", "guest"]);
  });

  it("opens from Set an approver with nothing chosen at all, and Save is not shut", async () => {
    render(<LicensesView />);
    await openNew();

    expect([field(ACCOUNT).value, field(MACHINE).value, field(USER).value, field(PROFILE).value]).toEqual(["", "", "", ""]);
    expect(saveButton().disabled).toBe(false);
    expect(missingLines()).toEqual([]);
    // No machine is chosen, so no Director is asked for anything.
    expect(reads(MAC_PROFILES) + reads(WINDOWS_PROFILES)).toBe(0);
  });

  it("opens from an account's own card with that account chosen and nothing else, and brings the form to the eye", async () => {
    render(<LicensesView />);

    fireEvent.click(within(await card(ACCOUNT_C)).getByRole("button", { name: "Set the approver" }));

    expect(field(ACCOUNT).value).toBe(ACCOUNT_C);
    expect(field(ACCOUNT).disabled).toBe(false);
    expect([field(MACHINE).value, field(USER).value, field(PROFILE).value]).toEqual(["", "", ""]);
    expect(document.activeElement).toBe(field(ACCOUNT));
  });

  it("opens fresh from another card: the account chosen for one card is not carried to the next", async () => {
    render(<LicensesView />);
    fireEvent.click(within(await card(ACCOUNT_C)).getByRole("button", { name: "Set the approver" }));
    choose(MACHINE, MAC);

    fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Set the approver" }));

    expect(field(ACCOUNT).value).toBe(ACCOUNT_A);
    expect(field(MACHINE).value).toBe("");
  });

  it("chooses the user for you when the machine has only one, and lists that machine's browser profiles from its own Director", async () => {
    render(<LicensesView />);
    await openNew();

    choose(MACHINE, MAC);

    expect(field(USER).value).toBe("owner");
    await waitFor(() => expect(offered(PROFILE)).toEqual(["work", "personal"]));
    expect([...field(PROFILE).options].map((o) => o.textContent)).toEqual(["Choose a browser profile", "work", "Personal (personal)"]);
    expect(reads(MAC_PROFILES)).toBe(1);
    expect(reads(WINDOWS_PROFILES)).toBe(0);
    expect(profilesText()).toBeNull();
  });

  it("clears the user and the browser profile when the machine is changed", async () => {
    render(<LicensesView />);
    await openNew();
    choose(MACHINE, MAC);
    await waitFor(() => expect(offered(PROFILE)).toContain("work"));
    choose(PROFILE, "work");
    expect([field(USER).value, field(PROFILE).value]).toEqual(["owner", "work"]);

    choose(MACHINE, WINDOWS);

    // Two users report from the-windows-machine, so none is chosen for you; with no user there is no Director to ask.
    expect([field(USER).value, field(PROFILE).value]).toEqual(["", ""]);
    expect(offered(PROFILE)).toEqual([]);
    expect(field(PROFILE).disabled).toBe(true);
  });

  it("clears the browser profile when the user is changed, and lists the other user's profiles from that user's Director", async () => {
    render(<LicensesView />);
    await openNew();
    choose(MACHINE, WINDOWS);
    choose(USER, "owner");
    await waitFor(() => expect(offered(PROFILE)).toEqual(["work"]));
    choose(PROFILE, "work");

    choose(USER, "guest");

    expect(field(PROFILE).value).toBe("");
    await waitFor(() => expect(offered(PROFILE)).toEqual(["guest-profile"]));
    expect(reads(GUEST_PROFILES)).toBe(1);
  });

  it("says it is loading the browser profiles, naming the machine, while the Director has not answered", async () => {
    answers[MAC_PROFILES] = () => new Promise<Response>(() => undefined);
    render(<LicensesView />);
    await openNew();

    choose(MACHINE, MAC);

    await waitFor(() => expect(profilesText()).toBe("Loading the browser profiles on the-mac..."));
    expect(offered(PROFILE)).toEqual([]);
    expect(field(PROFILE).disabled).toBe(true);
  });

  it("says in the Gateway's words that the Director is not connected, lists no profile, and asks it nothing", async () => {
    const mac = overview.machines.find((m) => m.machineName === MAC);
    if (mac === undefined) throw new Error(`no ${MAC}`);
    // The Gateway's own answer for a machine whose Director is not connected (the example overview).
    const offline = OVERVIEW.machines.find((m) => m.machineName === MAC);
    if (offline === undefined) throw new Error(`no ${MAC}`);
    mac.canListBrowserProfiles = offline.canListBrowserProfiles;
    mac.browserProfilesText = offline.browserProfilesText;
    render(<LicensesView />);
    await openNew();

    choose(MACHINE, MAC);

    expect(profilesText()).toBe("the-mac's Director is not connected, so its browser profiles cannot be listed.");
    expect(offered(PROFILE)).toEqual([]);
    expect(reads(MAC_PROFILES)).toBe(0);
  });

  it("says so when the machine's Director has no browser profiles", async () => {
    answers[MAC_PROFILES] = () => json(200, { browsers: [] });
    render(<LicensesView />);
    await openNew();

    choose(MACHINE, MAC);

    await waitFor(() => expect(profilesText()).toBe("the-mac's Director has no browser profiles."));
    expect(offered(PROFILE)).toEqual([]);
  });

  it("shows the Gateway's sentence when the Director cannot be asked for its browser profiles", async () => {
    const sentence = "The Director on the-mac is not connected right now, so the command was not delivered.";
    answers[MAC_PROFILES] = () => json(502, { error: sentence });
    render(<LicensesView />);
    await openNew();

    choose(MACHINE, MAC);

    await waitFor(() => expect(profilesText()).toContain(sentence));
    expect(offered(PROFILE)).toEqual([]);
  });

  it.each([
    [
      "nothing chosen",
      async () => undefined,
      [
        "Choose the Claude account.",
        "Choose the approving machine.",
        "Choose the operating system user on the approving machine.",
        "Choose the browser profile on the approving machine.",
      ],
    ],
    [
      "the account missing",
      async () => {
        choose(MACHINE, MAC);
        await waitFor(() => expect(offered(PROFILE)).toContain("work"));
        choose(PROFILE, "work");
      },
      ["Choose the Claude account."],
    ],
    [
      "the machine missing",
      async () => choose(ACCOUNT, ACCOUNT_A),
      [
        "Choose the approving machine.",
        "Choose the operating system user on the approving machine.",
        "Choose the browser profile on the approving machine.",
      ],
    ],
    [
      "the user missing",
      async () => {
        choose(ACCOUNT, ACCOUNT_A);
        choose(MACHINE, WINDOWS);
      },
      ["Choose the operating system user on the-windows-machine.", "Choose the browser profile on the-windows-machine."],
    ],
    [
      "the browser profile missing",
      async () => {
        choose(ACCOUNT, ACCOUNT_A);
        choose(MACHINE, MAC);
        await waitFor(() => expect(offered(PROFILE)).toContain("work"));
      },
      ["Choose the browser profile on the-mac."],
    ],
  ])("with %s, Save sends nothing and says exactly what is missing, one line each", async (_name, fill, lines) => {
    render(<LicensesView />);
    await openNew();
    await fill();
    // Nothing is said before Save is pressed.
    expect(missingLines()).toEqual([]);

    fireEvent.click(saveButton());

    expect(missingLines()).toEqual(lines);
    expect(writes()).toEqual([]);
    expect(saveButton().disabled).toBe(false);
    expect(screen.getByTestId("lic-approver-editor")).not.toBeNull();
  });

  it("takes each missing line away as its choice is made", async () => {
    render(<LicensesView />);
    await openNew();
    fireEvent.click(saveButton());
    expect(missingLines().length).toBe(4);

    choose(ACCOUNT, ACCOUNT_A);
    choose(MACHINE, MAC);

    expect(missingLines()).toEqual(["Choose the browser profile on the-mac."]);
  });

  it("sets an approver with exactly one application/json PUT of the four choices, shows the Gateway's sentence, and reads the approvers again", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_A, MAC, "owner", "work");
    expect(writes()).toEqual([]);
    // From here the Gateway lists the approver it stored.
    answers[READ_APPROVERS] = () => json(200, APPROVERS);
    const readsBefore = reads(READ_APPROVERS);

    fireEvent.click(saveButton());

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(SET_APPROVER);
    expect(writes()[0].contentType).toBe("application/json");
    // The four choices, and no other field.
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_A, machineName: MAC, osUser: "owner", browserProfile: "work" }));
    expect(screen.queryByTestId("lic-approver-editor")).toBeNull();
    // The list on screen is the Gateway's next answer, not one the page made up from what was chosen.
    await waitFor(() => expect(screen.getAllByTestId("lic-approver").length).toBe(2));
    expect(reads(READ_APPROVERS)).toBe(readsBefore + 1);
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
  });

  it("sends the profile's identifier, not its name, so renaming the profile does not break the approver", async () => {
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_C, MAC, "owner", "personal");

    fireEvent.click(saveButton());

    await screen.findByTestId("lic-approver-answer");
    expect(JSON.parse(writes()[0].text ?? "{}").browserProfile).toBe("personal");
  });

  it("keeps the list as the Gateway last gave it when the read after a set has not answered: it adds no row of its own", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_A, MAC, "owner", "work");
    answers[READ_APPROVERS] = () => new Promise<Response>(() => undefined);

    fireEvent.click(saveButton());

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);
    expect(screen.queryByTestId("lic-approver")).toBeNull();
    expect(screen.getByTestId("lic-approvers-empty").textContent).toBe(APPROVERS_EMPTY.emptyText);
  });

  it.each([
    ["400 invalid_browser_profile", 400, APPROVER_REFUSED],
    ["403 owner_only", 403, SET_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps the form open with what was chosen", async (_name, status, refusal) => {
    answers[SET_APPROVER] = () => json(status, refusal);
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_C, MAC, "owner", "work");

    fireEvent.click(saveButton());

    expect((await within(editor()).findByTestId("lic-approver-error")).textContent).toBe(refusal.error);
    expect([field(ACCOUNT).value, field(MACHINE).value, field(USER).value, field(PROFILE).value]).toEqual([ACCOUNT_C, MAC, "owner", "work"]);
    expect(saveButton().disabled).toBe(false);
    expect(missingLines()).toEqual([]);
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("takes the sentence of the answer before off the screen when the next write is refused: it is not that write's outcome", async () => {
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_A, MAC, "owner", "work");
    fireEvent.click(saveButton());
    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);

    answers[REMOVE_APPROVER] = () => json(403, REMOVE_OWNER_ONLY);
    const dialog = await openRemove();
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(refusalIn(dialog)).toBe(REMOVE_OWNER_ONLY.error));
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("changes an approver from its own row: the form opens on what is stored and keeps its account", async () => {
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    const rows = await screen.findAllByTestId("lic-approver");

    fireEvent.click(within(rows[0]).getByRole("button", { name: "Change" }));

    expect(field(ACCOUNT).value).toBe(ACCOUNT_A);
    expect(field(ACCOUNT).disabled).toBe(true);
    expect([field(MACHINE).value, field(USER).value]).toEqual([MAC, "owner"]);
    await waitFor(() => expect(field(PROFILE).value).toBe("work"));
    choose(PROFILE, "personal");
    fireEvent.click(saveButton());

    await screen.findByTestId("lic-approver-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_A, machineName: MAC, osUser: "owner", browserProfile: "personal" }));
  });

  it("changes an approver on a machine that never reported with no machine chosen: that machine is not in the list", async () => {
    render(<LicensesView />);
    const rows = await screen.findAllByTestId("lic-approver");

    fireEvent.click(within(rows[1]).getByRole("button", { name: "Change" }));

    expect(field(ACCOUNT).value).toBe(ACCOUNT_B);
    expect([field(MACHINE).value, field(USER).value, field(PROFILE).value]).toEqual(["", "", ""]);
    expect(offered(MACHINE)).not.toContain("the-laptop");
  });

  it("sends nothing when the form is cancelled", async () => {
    render(<LicensesView />);
    await openNew();
    await chooseAll(ACCOUNT_C, MAC, "owner", "work");

    fireEvent.click(within(editor()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByTestId("lic-approver-editor")).toBeNull();
    expect(writes()).toEqual([]);
  });

  /** Open the removal confirmation of account B's approver and return it. */
  async function openRemove(): Promise<HTMLElement> {
    const rows = await screen.findAllByTestId("lic-approver");
    fireEvent.click(within(rows[1]).getByRole("button", { name: "Remove" }));
    return screen.getByRole("alertdialog", { name: "Remove the approver of account-b@example.com?" });
  }

  it("removes an approver only when confirmed: one DELETE naming the account in the query, no body, then the Gateway's sentence and a new read", async () => {
    answers[REMOVE_APPROVER] = () => json(200, APPROVER_REMOVED);
    render(<LicensesView />);
    const dialog = await openRemove();
    // The confirmation says what is being removed in the Gateway's sentence for that approver.
    expect(dialog.querySelector(".ui-confirm-message")?.textContent).toBe(APPROVERS.approvers[1].text);
    expect(writes()).toEqual([]);
    // From here the Gateway lists what is left.
    const left = copyOf(APPROVERS);
    left.approvers = [left.approvers[0]];
    answers[READ_APPROVERS] = () => json(200, left);
    const readsBefore = reads(READ_APPROVERS);

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_REMOVED.text);
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(REMOVE_APPROVER);
    expect(writes()[0].url).toBe("/gateway/licenses/approvers?accountEmail=account-b%40example.com");
    expect(writes()[0].text).toBeNull();
    expect(writes()[0].contentType).toBeNull();
    expect(screen.queryByRole("alertdialog")).toBeNull();
    await waitFor(() => expect(screen.getAllByTestId("lic-approver").length).toBe(1));
    expect(reads(READ_APPROVERS)).toBe(readsBefore + 1);
  });

  it("shows a refused removal in the Gateway's sentence, word for word, and removes nothing from the screen", async () => {
    answers[REMOVE_APPROVER] = () => json(403, REMOVE_OWNER_ONLY);
    render(<LicensesView />);
    const dialog = await openRemove();

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(REMOVE_OWNER_ONLY.error);
    expect(screen.getAllByTestId("lic-approver").length).toBe(2);
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("says so when the approvers cannot be read, and still draws the machines", async () => {
    answers[READ_APPROVERS] = () => json(403, { code: "no_account", error: "no account is bound to this request" });
    render(<LicensesView />);

    expect(await screen.findByText("no account is bound to this request")).toBeTruthy();
    expect(screen.getAllByTestId("lic-sign-in-machine").length).toBe(overview.machines.length);
    expect(screen.queryByTestId("lic-approvers")).toBeNull();
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
  });
});
