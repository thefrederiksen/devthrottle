import { useId, useState } from "react";
import type {
  LicenseApprover,
  LicenseApprovers,
  LicenseLogEntry,
  LicenseMachine,
  LicenseOverview,
  LicenseSetApprover,
  LicenseSignInStarted,
} from "@devthrottle/client-core/licenses/licensesClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ErrorBanner, LoadingState } from "../components";
import { LICENSES_SURFACE, TonePill } from "./licenseParts";

// THE SIGN-INS OF THE LICENSES PAGE: on which machine a sign-in can be started (Add a spare), the sign-ins that have
// not ended, and which machine and browser profile approve the sign-ins of each account.
//
// THE PAGE DECIDES NOTHING HERE EITHER (CLAUDE.md rule 7). Whether Add a spare is offered on a machine, and the
// sentence saying what it does or why it is not offered, are the Gateway's (LicenseSignInFold); so are an approver's
// status, its tone and its sentence. What a person types - an account, a machine, its user, a browser profile - is
// sent as typed, and the Gateway says whether it will take it: a refusal is shown in its sentence, word for word.
//
// No sign-in key is here, nor the address a sign-in is approved at, nor an approval. The page starts a sign-in by
// naming an account and a machine; everything else passes between two Directors and never reaches a browser.

/** What a person fills in to say who approves an account. Every field is sent as typed, less the space around it. */
function ApproverEditor({
  editing,
  overview,
  onSave,
  onClose,
}: {
  /** The approver being changed; null when a new one is being set. */
  editing: LicenseApprover | null;
  /** Its accounts and machines are offered as suggestions and as nothing more: any name may be typed. */
  overview: LicenseOverview;
  onSave: (approver: LicenseSetApprover) => Promise<void>;
  onClose: () => void;
}) {
  const [accountEmail, setAccountEmail] = useState(editing?.accountEmail ?? "");
  const [machineName, setMachineName] = useState(editing?.machineName ?? "");
  const [osUser, setOsUser] = useState(editing?.osUser ?? "");
  const [browserProfile, setBrowserProfile] = useState(editing?.browserProfile ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const lists = useId();

  const typed: LicenseSetApprover = {
    accountEmail: accountEmail.trim(),
    machineName: machineName.trim(),
    osUser: osUser.trim(),
    browserProfile: browserProfile.trim(),
  };
  const complete = typed.accountEmail !== "" && typed.machineName !== "" && typed.osUser !== "" && typed.browserProfile !== "";

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSave(typed);
      onClose();
    } catch (err) {
      setError(describeAndReport(LICENSES_SURFACE, "set who approves the sign-ins", err));
      setBusy(false);
    }
  };

  return (
    <div className="lic-approver-editor" data-testid="lic-approver-editor">
      <label className="lic-field">
        <span>Claude account</span>
        {/* Changing an approver keeps its account: another account is another approver. */}
        <input
          type="email"
          className="lic-input"
          list={`${lists}-accounts`}
          value={accountEmail}
          disabled={busy || editing !== null}
          onChange={(event) => setAccountEmail(event.target.value)}
        />
      </label>
      <label className="lic-field">
        <span>Approving machine</span>
        <input
          type="text"
          className="lic-input"
          list={`${lists}-machines`}
          value={machineName}
          disabled={busy}
          onChange={(event) => setMachineName(event.target.value)}
        />
      </label>
      <label className="lic-field">
        <span>Operating system user on that machine</span>
        <input
          type="text"
          className="lic-input"
          list={`${lists}-users`}
          value={osUser}
          disabled={busy}
          onChange={(event) => setOsUser(event.target.value)}
        />
      </label>
      <label className="lic-field">
        <span>Browser profile on that machine</span>
        <input
          type="text"
          className="lic-input"
          value={browserProfile}
          disabled={busy}
          onChange={(event) => setBrowserProfile(event.target.value)}
        />
      </label>
      <datalist id={`${lists}-accounts`}>
        {overview.accounts.map((account) => (
          <option key={account.accountEmail} value={account.accountEmail} />
        ))}
      </datalist>
      <datalist id={`${lists}-machines`}>
        {[...new Set(overview.machines.map((machine) => machine.machineName))].map((name) => (
          <option key={name} value={name} />
        ))}
      </datalist>
      <datalist id={`${lists}-users`}>
        {[...new Set(overview.machines.map((machine) => machine.osUser))].map((user) => (
          <option key={user} value={user} />
        ))}
      </datalist>
      <div className="lic-approver-editor-actions">
        <Button variant="primary" disabled={busy || !complete} onClick={() => void save()}>
          {busy ? "Saving..." : "Save"}
        </Button>
        <Button variant="ghost" disabled={busy} onClick={onClose}>
          Cancel
        </Button>
      </div>
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
}: LicenseSignInsProps) {
  // The editor: closed, open for a new approver, or open on the one being changed.
  const [editor, setEditor] = useState<{ editing: LicenseApprover | null } | null>(null);

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
                    <Button variant="ghost" onClick={() => setEditor({ editing: approver })}>
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
          <Button variant="secondary" onClick={() => setEditor({ editing: null })}>
            Set an approver
          </Button>
        </div>
      ) : (
        <ApproverEditor
          // A different approver is a different editor: its fields start from that approver, not the last one.
          key={editor.editing?.accountEmail ?? ""}
          editing={editor.editing}
          overview={overview}
          onSave={onSetApprover}
          onClose={() => setEditor(null)}
        />
      )}
    </>
  );
}

/** The body of the Add a spare confirmation: the Gateway's sentence for the machine, and the account to sign in to. */
export function AddSpareChoice({
  machine,
  accountEmail,
  onType,
}: {
  machine: LicenseMachine;
  accountEmail: string;
  onType: (accountEmail: string) => void;
}) {
  return (
    <div className="lic-choice">
      <p className="lic-choice-text">{machine.addSpareText}</p>
      {/* Which account is the person's own choice: any email may be typed, and the Gateway refuses one it will not take. */}
      <label className="lic-field">
        <span>Claude account to sign in to</span>
        <input type="email" className="lic-input" value={accountEmail} onChange={(event) => onType(event.target.value)} />
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
