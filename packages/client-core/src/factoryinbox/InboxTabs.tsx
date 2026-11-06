import "./factoryInbox.css";

// ONE INBOX ENTRY, TWO TABS (Factory Inbox mission, the owner's answer on names): "Factory inbox" - messages the owner
// leaves in a factory - and "Your inbox" - messages to the owner. Both shells show this one bar (rule 8). Your inbox is
// a later task of the mission: it is added here, as one more entry in INBOX_TABS, and its page mounted where each shell
// mounts the Factory inbox. Until then the bar holds the one tab, so the place the second goes is already on screen.

export type InboxTabId = "factories" | "yours";

export interface InboxTab {
  id: InboxTabId;
  label: string;
}

export const INBOX_TABS: ReadonlyArray<InboxTab> = [{ id: "factories", label: "Factory inbox" }];

export interface InboxTabsProps {
  active: InboxTabId;
  onSelect: (tab: InboxTabId) => void;
}

export function InboxTabs({ active, onSelect }: InboxTabsProps) {
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
        </button>
      ))}
    </div>
  );
}
