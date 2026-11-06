import { useYourInboxCount } from "../yourinbox/useYourInboxCount";
import "./factoryInbox.css";

// ONE INBOX ENTRY, TWO TABS (Factory Inbox mission, the owner's answer on names): "Factory inbox" - messages the owner
// leaves in a factory - and "Your inbox" - messages to the owner. Both shells show this one bar (rule 8). Your inbox's
// tab carries the Gateway's unread count in the Gateway's words ("3 unread"), the same answer the Inbox entry in the
// Cockpit's rail and the phone's menu reads (useYourInboxCount), so the three can never disagree.

export type InboxTabId = "factories" | "yours";

export interface InboxTab {
  id: InboxTabId;
  label: string;
}

export const INBOX_TABS: ReadonlyArray<InboxTab> = [
  { id: "factories", label: "Factory inbox" },
  { id: "yours", label: "Your inbox" },
];

export interface InboxTabsProps {
  active: InboxTabId;
  onSelect: (tab: InboxTabId) => void;
}

export function InboxTabs({ active, onSelect }: InboxTabsProps) {
  const count = useYourInboxCount();
  return (
    <div className="fi-tabs" role="tablist" aria-label="Inbox">
      {INBOX_TABS.map((t) => (
        <button
          key={t.id}
          type="button"
          role="tab"
          className="fi-tab"
          aria-selected={t.id === active}
          onClick={() => onSelect(t.id)}
        >
          {t.label}
          {t.id === "yours" && count !== null && count.label !== "" && (
            <span className="fi-tab-count" data-testid="yi-tab-count">
              {count.label}
            </span>
          )}
        </button>
      ))}
    </div>
  );
}
