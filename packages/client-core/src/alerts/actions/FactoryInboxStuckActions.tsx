import { useState } from "react";
import type { AlertDto } from "../alertsClient";
import { ownerDealtWith } from "../alertsClient";

export interface FactoryInboxStuckActionsProps {
  alert: AlertDto;
  onAnswered: () => void;
}

export function FactoryInboxStuckActions({ alert, onAnswered }: FactoryInboxStuckActionsProps) {
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleDealtWith = async () => {
    setIsLoading(true);
    setError(null);
    try {
      await ownerDealtWith(alert.id);
      onAnswered();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to mark as dealt with");
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <div className="alert-actions">
      <button
        onClick={handleDealtWith}
        disabled={isLoading}
        className="alert-action-button"
        title="Mark this alert as dealt with"
      >
        {isLoading ? "Marking..." : "Dealt with it"}
      </button>
      {error && <p className="alert-action-error">{error}</p>}
    </div>
  );
}
