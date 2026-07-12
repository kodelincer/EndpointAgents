import { describe, expect, it } from "vitest";
import { mergeActivity } from "./activity";
import type { Activity } from "./types";

const event = (eventId: string, timestampUtc: string): Activity => ({
  eventId, timestampUtc, type: "agent.connected", agentId: "agent-1", status: "online"
});

describe("mergeActivity", () => {
  it("deduplicates events and keeps chronological order", () => {
    const result = mergeActivity([event("b", "2026-07-12T10:00:02Z")], [
      event("a", "2026-07-12T10:00:01Z"), event("b", "2026-07-12T10:00:02Z")
    ]);
    expect(result.map(item => item.eventId)).toEqual(["a", "b"]);
  });

  it("retains only the newest 500 events", () => {
    const result = mergeActivity([], Array.from({ length: 510 }, (_, index) =>
      event(String(index), new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString())));
    expect(result).toHaveLength(500);
    expect(result[0].eventId).toBe("10");
  });
});
