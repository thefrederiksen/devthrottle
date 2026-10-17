import { useEffect, useRef, useState } from "react";
import {
  listLicenseBrowserProfiles,
  type LicenseApprover,
  type LicenseApprovers,
  type LicenseBrowserProfile,
  type LicenseLogEntry,
  type LicenseMachine,
  type LicenseOverview,
  type LicenseSetApprover,
  type LicenseSignInStarted,
} from "@devthrottle/client-core/licenses/licensesClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ErrorBanner, LoadingState } from "../components";
import { LICENSES_SURFACE, TonePill } from "./licenseParts";

// THE SIGN-INS OF THE LICENSES PAGE: on which machine a sign-in can be started (Add a spare), the sign-ins that have
// not ended, and which machine and browser profile approve the sign-ins of each account.
//
// THE PAGE DECIDES NOTHING HERE EITHER (CLAUDE.md rule 7). Whether Add a spare is offered on a machine, and the
// sentence saying what it does or why it is not offered, are the Gateway's (LicenseSignInFold); so are an approver's
// status, its tone and its sentence, and whether a machine's browser profiles can be listed and why not. The approver
// form offers only what the Gateway and the machine's own Director listed - nothing is typed - and the Gateway says
// whether it will take the choice: a refusal is shown in its sentence, word for word.
//
// No sign-in key is here, nor the address a sign-in is approved at, nor an approval. The page starts a sign-in by
// naming an account and a machine; everything else passes between two Directors and never reaches a browser.

/** What the approver form is open on: an approver being changed, or a new one, for an account or for none yet. */
export interface ApproverEditorOpen {
  /** The approver being changed; null when a new one is being set. */
  editing: LicenseApprover | null;
  /** The account chosen when the form opens: set only when it was opened from that account's own card. */
  accountEmail: string | null;
  /** Counts the openings, so a second opening from a card starts a fresh form. */
  opening: number;
}

/** The browser profiles of the chosen machine's Director, as far as they are known. */
type Profiles =
  | { kind: "none-chosen" }
  | { kind: "cannot-list"; text: string }
  | { kind: "loading" }
  | { kind: "failed"; text: string }
  | { kind: "listed"; profiles: LicenseBrowserProfile[] };

/** One sentence per choice that is still missing, in the order of the form. */
function missingChoices(choice: LicenseSetApprover, machine: LicenseMachine | null): string[] {
  const where = choice.machineName === "" ? "the approving machine" : (machine?.label ?? choice.machineName);
  const missing: string[] = [];
  if (choice.accountEmail === "") missing.push("Choose the Claude account.");
  if (choice.machineName === "") missing.push("Choose the approving machine.");
  if (choice.osUser === "") missing.push(`Choose the operating system user on ${where}.`);
  if (choice.browserProfile === "") missing.push(`Choose the browser profile on ${where}.`);
  return missing;
}

/**
 * The form that says who approves an account: four pick lists, nothing typed. The accounts are the page's; the machines
 * and their users are the machines that have reported; the browser profiles are read from the Director that reports
 * the chosen machine. Save is never shut while the form is idle: pressed with a choice missing, it sends nothing and
 * says which.
 */
function ApproverEditor({
  open,
  overview,
  onSave,
  onClose,
}: {
  open: ApproverEditorOpen;
  overview: LicenseOverview;
  onSave: (approver: LicenseSetApprover) => Promise<void>;
  onClose: () => void;
}) {
  const machineNames = [...new Set(overview.machines.map((m) => m.machineName))];
  const usersOf = (name: string) => overview.machines.filter((m) => m.machineName === name).map((m) => m.osUser);
  // A user is chosen for you when the machine has only the one.
  const onlyUser = (name: string) => {
    const users = usersOf(name);
    return users.length === 1 ? users[0] : "";
  };

  const { editing } = open;
  // An approver on a machine that has not reported cannot be shown as chosen: that machine is not in the list.
  const startMachine = editing !== null && machineNames.includes(editing.machineName) ? editing.machineName : "";
  const startUser =
    editing !== null && startMachine !== "" && usersOf(startMachine).includes(editing.osUser) ? editing.osUser : onlyUser(startMachine);
  const [accountEmail, setAccountEmail] = useState(editing?.accountEmail ?? open.accountEmail ?? "");
  const [machineName, setMachineName] = useState(startMachine);
  const [osUser, setOsUser] = useState(startMachine === "" ? "" : startUser);
  const [browserProfile, setBrowserProfile] = useState(startMachine === "" ? "" : (editing?.browserProfile ?? ""));
  const [profiles, setProfiles] = useState<Profiles>({ kind: "none-chosen" });
  const [tried, setTried] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const first = useRef<HTMLSelectElement>(null);

  const machine = overview.machines.find((m) => m.machineName === machineName && m.osUser === osUser) ?? null;
  const directorId = machine?.directorId ?? null;
  const canList = machine?.canListBrowserProfiles ?? false;
  const cannotListText = machine?.browserProfilesText ?? "";

  // Opened from an account's card, far above it: the form is brought to the eye.
  useEffect(() => {
    if (open.accountEmail !== null) first.current?.focus();
  }, [open.accountEmail]);

  // The browser profiles of the chosen machine, read from its own Director each time the machine or user changes.
  useEffect(() => {
    if (directorId === null) {
      setProfiles({ kind: "none-chosen" });
      return;
    }
    if (!canList) {
      setProfiles({ kind: "cannot-list", text: cannotListText });
      return;
    }
    const controller = new AbortController();
    setProfiles({ kind: "loading" });
    listLicenseBrowserProfiles(directorId, controller.signal).then(
      (listed) => {
        if (controller.signal.aborted) return;
        setProfiles({ kind: "listed", profiles: listed });
        // A stored approver may name its profile by its name; the form chooses it by its identifier, or not at all.
        setBrowserProfile((chosen) => listed.find((p) => p.id === chosen || p.name === chosen)?.id ?? "");
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        setProfiles({ kind: "failed", text: describeAndReport(LICENSES_SURFACE, "list the browser profiles", err) });
      },
    );
    return () => controller.abort();
  }, [directorId, canList, cannotListText]);

  const choice: LicenseSetApprover = { accountEmail, machineName, osUser, browserProfile };
  const missing = tried ? missingChoices(choice, machine) : [];

  // Another machine, or another user on it, has other browser profiles: what was chosen for the last one goes.
  const chooseMachine = (name: string) => {
    setMachineName(name);
    setOsUser(onlyUser(name));
    setBrowserProfile("");
  };

  const chooseUser = (user: string) => {
    setOsUser(user);
    setBrowserProfile("");
  };

  const save = async () => {
    setTried(true);
    setError(null);
    if (missingChoices(choice, machine).length > 0) return;
    setBusy(true);
    try {
      await onSave(choice);
      onClose();
    } catch (err) {
      setError(describeAndReport(LICENSES_SURFACE, "set who approves the sign-ins", err));
      setBusy(false);
    }
  };

  const listed = profiles.kind === "listed" ? profiles.profiles : [];
  const where = machine?.label ?? machineName;
  const profilesText =
    profiles.kind === "cannot-list" || profiles.kind === "failed"
      ? profiles.text
      : profiles.kind === "loading"
        ? `Loading the browser profiles on ${where}...`
        : profiles.kind === "listed" && listed.length === 0
          ? `${where}'s Director has no browser profiles.`
          : null;

  return (
    <div className="lic-approver-editor" data-testid="lic-approver-editor">
      <label className="lic-field">
        <span>Claude account</span>
        {/* Changing an approver keeps its account: another account is another approver. */}
        <select
          ref={first}
          className="lic-input"
          value={accountEmail}
          disabled={busy || editing !== null}
          onChange={(event) => setAccountEmail(event.target.value)}
        >
          <option value="">Choose an account</option>
          {overview.accounts.map((account) => (
            <option key={account.accountEmail} value={account.accountEmail}>
              {account.accountEmail}
            </option>
          ))}
        </select>
      </label>
      <label className="lic-field">
        <span>Approving machine</span>
        <select className="lic-input" value={machineName} disabled={busy} onChange={(event) => chooseMachine(event.target.value)}>
          <option value="">Choose a machine</option>
          {machineNames.map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </select>
      </label>
      <label className="lic-field">
        <span>Operating system user on that machine</span>
        <select
          className="lic-input"
          value={osUser}
          disabled={busy || machineName === ""}
          onChange={(event) => chooseUser(event.target.value)}
        >
          <option value="">{machineName === "" ? "Choose the machine first" : "Choose a user"}</option>
          {usersOf(machineName).map((user) => (
            <option key={user} value={user}>
              {user}
            </option>
          ))}
        </select>
      </label>
      <label className="lic-field">
        <span>Browser profile on that machine</span>
        <select
          className="lic-input"
          value={browserProfile}
          disabled={busy || listed.length === 0}
          onChange={(event) => setBrowserProfile(event.target.value)}
        >
          <option value="">{machine === null ? "Choose the machine and user first" : "Choose a browser profile"}</option>
          {listed.map((profile) => (
            <option key={profile.id} value={profile.id}>
              {profile.name === profile.id ? profile.name : `${profile.name} (${profile.id})`}
            </option>
          ))}
        </select>
      </label>
      <div className="lic-approver-editor-actions">
        <Button variant="primary" disabled={busy} onClick={() => void save()}>
          {busy ? "Saving..." : "Save"}
        </Button>
        <Button variant="ghost" disabled={busy} onClick={onClose}>
          Cancel
        </Button>
      </div>
      {profilesText !== null && (
        <div className="lic-approver-profiles" role="status" data-testid="lic-approver-profiles">
          {profilesText}
        </div>
      )}
      {missing.length > 0 && (
        // error-report-exempt: the choices the person has not made yet, said when Save is pressed; nothing failed
        <ul className="lic-approver-missing" role="alert" data-testid="lic-approver-missing">
          {missing.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      )}
      {error !== null && (
        <div className="lic-approver-error" role="alert" data-testid="lic-approver-error">
          {error}
        </div>
      )}
    </div>
  );
}

export interface LicenseSignInsProps {
  overview: LicenseOverview;
  /** Who approves each account; null while it has not been read. */
  approvers: LicenseApprovers | null;
  /** The sentence of a failed read of the approvers, already reported; null when the last read worked. */
  approversError: string | null;
  onRetryApprovers: () => void;
  /** The Gateway's sentence for the last approver set or removed here; null when there is none to show. */
  approverAnswer: string | null;
  onAddSpare: (machine: LicenseMachine) => void;
  /** Store an approver. Throws on a refusal. */
  onSetApprover: (approver: LicenseSetApprover) => Promise<void>;
  onRemoveApprover: (approver: LicenseApprover) => void;
  /** What the approver form is open on; null while it is closed. The page holds it, so an account's card can open it. */
  editor: ApproverEditorOpen | null;
  onOpenEditor: (editing: LicenseApprover | null) => void;
  onCloseEditor: () => void;
}

/** The Sign-ins part of the page: one row per machine, then who approves each account. */
export function LicenseSignIns({
  overview,
  approvers,
  approversError,
  onRetryApprovers,
  approverAnswer,
  onAddSpare,
  onSetApprover,
  onRemoveApprover,
  editor,
  onOpenEditor,
  onCloseEditor,
}: LicenseSignInsProps) {
  return (
    <>
      <div className="lic-table-wrap">
        <table className="lic-table" data-testid="lic-sign-ins">
          <thead>
            <tr>
              <th>Machine</th>
              <th>Sign-ins in progress</th>
              <th>Add a spare</th>
            </tr>
          </thead>
          <tbody>
            {overview.machines.map((machine) => (
              <tr key={machine.machineId} data-testid="lic-sign-in-machine">
                <td className="lic-nowrap">
                  <span className="lic-strong">{machine.label}</span>
                </td>
                <td>
                  {machine.signInsInProgress.map((signIn) => (
                    <div key={signIn.signInId} data-testid="lic-sign-in-progress">
                      {signIn.text}
                    </div>
                  ))}
                </td>
                <td>
                  <div className="lic-offer">
                    {machine.canAddSpare && (
                      <Button variant="secondary" onClick={() => onAddSpare(machine)}>
                        Add a spare
                      </Button>
                    )}
                    <span className="lic-dim" data-testid="lic-add-spare-text">
                      {machine.addSpareText}
                    </span>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <h3 className="lic-h3">Who approves a sign-in</h3>
      {/* error-reported-by: usePolled */}
      {approversError !== null && <ErrorBanner message={approversError} onRetry={onRetryApprovers} />}
      {approvers === null ? (
        approversError === null && <LoadingState />
      ) : approvers.isEmpty ? (
        // No approver is set for any account: the Gateway's own sentence, in place of the list.
        <p className="lic-approvers-empty" data-testid="lic-approvers-empty">
          {approvers.emptyText}
        </p>
      ) : (
        <div className="lic-table-wrap">
          <table className="lic-table" data-testid="lic-approvers">
            <thead>
              <tr>
                <th>Account</th>
                <th>Machine</th>
                <th>Browser profile</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {approvers.approvers.map((approver) => (
                <tr key={approver.accountEmail} data-testid="lic-approver" data-status={approver.status}>
                  <td className="lic-nowrap">{approver.accountEmail}</td>
                  <td className="lic-nowrap">{approver.label}</td>
                  <td className="lic-nowrap">{approver.browserProfile}</td>
                  <td>
                    <TonePill text={approver.statusText} tone={approver.statusTone} testId="lic-approver-status" />
                    <div data-testid="lic-approver-text">{approver.text}</div>
                  </td>
                  <td className="lic-nowrap">
                    <Button variant="ghost" onClick={() => onOpenEditor(approver)}>
                      Change
                    </Button>
                    <Button variant="ghost" onClick={() => onRemoveApprover(approver)}>
                      Remove
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {approverAnswer !== null && (
        <p className="lic-approver-answer" role="status" data-testid="lic-approver-answer">
          {approverAnswer}
        </p>
      )}
      {editor === null ? (
        <div className="lic-older">
          <Button variant="secondary" onClick={() => onOpenEditor(null)}>
            Set an approver
          </Button>
        </div>
      ) : (
        <ApproverEditor
          // A different opening is a different editor: its choices start from what it was opened on, not the last one.
          key={editor.opening}
          open={editor}
          overview={overview}
          onSave={onSetApprover}
          onClose={onCloseEditor}
        />
      )}
    </>
  );
}

/** The body of the Add a spare confirmation: the Gateway's sentence for the machine, and the account to sign in to. */
export function AddSpareChoice({
  machine,
  accountEmail,
  busy,
  onType,
}: {
  machine: LicenseMachine;
  accountEmail: string;
  /** The call is out: the field is shut until the Gateway answers. */
  busy: boolean;
  onType: (accountEmail: string) => void;
}) {
  return (
    <div className="lic-choice">
      <p className="lic-choice-text">{machine.addSpareText}</p>
      {/* Which account is the person's own choice: any email may be typed, and the Gateway refuses one it will not take. */}
      <label className="lic-field">
        <span>Claude account to sign in to</span>
        <input
          type="text"
          inputMode="email"
          autoComplete="off"
          className="lic-input"
          value={accountEmail}
          disabled={busy}
          onChange={(event) => onType(event.target.value)}
        />
      </label>
    </div>
  );
}

/**
 * What the Gateway answered to the last sign-in started here: its one sentence, then the sign-in's row of the log. A
 * sign-in takes minutes and the answer does not wait for it; how it ended arrives on the same row of the log, found
 * by its entryId. The page never starts the same sign-in again to find out.
 */
export function SignInAnswer({
  started,
  log,
  onDismiss,
}: {
  started: LicenseSignInStarted;
  log: ReadonlyArray<LicenseLogEntry>;
  onDismiss: () => void;
}) {
  // The row of the log is the newer word on the same sign-in; until it is on screen, the answer's own.
  const row = log.find((entry) => entry.entryId === started.entry.entryId) ?? started.entry;
  return (
    <div className="lic-answer" role="status" data-testid="lic-sign-in-answer">
      <div>
        <p className="lic-answer-text" data-testid="lic-sign-in-answer-text">
          {started.text}
        </p>
        <ul className="lic-answer-list">
          <li data-testid="lic-sign-in-answer-row">
            <TonePill text={row.stateText} tone={row.stateTone} /> {row.summary}
          </li>
          {row.reasonText !== null && (
            <li className="lic-dim" data-testid="lic-sign-in-answer-reason">
              {row.reasonText}
            </li>
          )}
        </ul>
      </div>
      <Button variant="ghost" onClick={onDismiss}>
        Dismiss
      </Button>
    </div>
  );
}
