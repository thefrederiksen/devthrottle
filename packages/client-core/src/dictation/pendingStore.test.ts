import { describe, expect, it } from "vitest";
import { migratePendingRecord, type PendingDictation } from "./pendingStore";

// The one-time migration of a durable record saved before the Send time existed (voice delivery, #3398).
// Such a record has no `sentAt`; its createdAt was stamped when it was saved, a second or so after Send, and
// is the only time it recorded - so it becomes the Send time, once, and is written back.

const base = {
  id: "rec-1",
  sessionId: "sid",
  blob: new Blob(["x"]),
  recordedMs: 1000,
  before: "",
  after: "",
  prefix: "",
  createdAt: Date.parse("2026-09-24T10:00:00.000Z"),
};

describe("migratePendingRecord", () => {
  it("gives a record saved before the field existed its createdAt as the Send time, and asks for a write-back", () => {
    const { rec, migrated } = migratePendingRecord(base);

    expect(migrated).toBe(true);
    expect(rec.sentAt).toBe(base.createdAt);
    expect(rec.id).toBe("rec-1");
  });

  it("leaves a record that already has a Send time untouched, with no write-back", () => {
    const stored: PendingDictation = { ...base, sentAt: Date.parse("2026-09-24T09:59:58.000Z") };

    const { rec, migrated } = migratePendingRecord(stored);

    expect(migrated).toBe(false);
    expect(rec).toBe(stored);
    expect(rec.sentAt).toBe(Date.parse("2026-09-24T09:59:58.000Z"));
  });

  it("keeps everything else on an old record", () => {
    const legacy = { ...base, staleDropped: true, droppedTranscript: "w" };

    const { rec } = migratePendingRecord(legacy);

    expect(rec.staleDropped).toBe(true);
    expect(rec.droppedTranscript).toBe("w");
  });

  // Phase 2, change 1: a dropped record saved before the Gateway's offerSendAnyway existed was dropped for a
  // reason the Gateway offers "Send anyway" for ("unconfirmed" did not exist yet), and was shown with it.
  it("gives an old dropped record the offer it was shown with, and asks for a write-back", () => {
    const stored: PendingDictation = { ...base, sentAt: 1, staleDropped: true, droppedTranscript: "w", droppedReason: "too-old" };

    const { rec, migrated } = migratePendingRecord(stored);

    expect(migrated).toBe(true);
    expect(rec.droppedOfferSendAnyway).toBe(true);
  });

  it("never overrides the Gateway's recorded decision not to offer it", () => {
    const stored: PendingDictation = {
      ...base,
      sentAt: 1,
      staleDropped: true,
      droppedReason: "unconfirmed",
      droppedOfferSendAnyway: false,
    };

    const { rec, migrated } = migratePendingRecord(stored);

    expect(migrated).toBe(false);
    expect(rec.droppedOfferSendAnyway).toBe(false);
  });

  it("gives a record that was not dropped no offer at all", () => {
    const { rec } = migratePendingRecord({ ...base, sentAt: 1 });

    expect(rec.droppedOfferSendAnyway).toBeUndefined();
  });
});
