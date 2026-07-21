import { describe, expect, it } from "vitest";
import { connectionLabel, mergeSupportRequests } from "./support";
import type { SupportRequest } from "./types";

const request = (requestId: string, receivedAtUtc: string): SupportRequest => ({
  requestId, clientRequestId: requestId, receivedAtUtc, subject: "Connection problem",
  messageText: "نمی‌توانم متصل شوم", textDirection: "rtl", agentId: "agent-1",
  machineName: "pc-1", userName: "DOMAIN\\user", sessionId: 1, logExcerpts: [],
  runtime: { agentId: "agent-1", machineName: "pc-1", version: "1.0",
    connectionStatus: 2, functionMode: 0, startedAtUtc: receivedAtUtc, activeCommands: 0 }
});

describe("mergeSupportRequests", () => {
  it("deduplicates requests and keeps chronological order", () => {
    const result = mergeSupportRequests([request("b", "2026-07-19T10:00:02Z")], [
      request("a", "2026-07-19T10:00:01Z"), request("b", "2026-07-19T10:00:02Z")
    ]);
    expect(result.map(item => item.requestId)).toEqual(["a", "b"]);
  });

  it("retains only the newest 200 requests", () => {
    const result = mergeSupportRequests([], Array.from({ length: 210 }, (_, index) =>
      request(String(index), new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString())));
    expect(result).toHaveLength(200);
    expect(result[0].requestId).toBe("10");
  });

  it("maps the service connection enum for helpdesk display", () => {
    expect(connectionLabel(2)).toBe("Connected");
    expect(connectionLabel(5)).toBe("Faulted");
  });
});
