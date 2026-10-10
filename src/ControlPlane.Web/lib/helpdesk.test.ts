import { describe, expect, it } from "vitest";
import { mergeHelpdeskConversations, mergeHelpdeskDetail, repairAttempts } from "./helpdesk";
import type { HelpdeskConversationDetail, HelpdeskConversationSummary } from "./types";

const summary = (conversationId: string, status: HelpdeskConversationSummary["status"],
  updatedAtUtc: string): HelpdeskConversationSummary => ({
  conversationId, status, updatedAtUtc, agentId: "agent", userName: "user", sessionId: 1,
  createdAtUtc: updatedAtUtc, lastSequence: 1, responderMode: "Hermes", unreadCount: 1,
  preview: conversationId, textDirection: "ltr", machineName: "pc", version: "1", isOnline: true
});

describe("helpdesk reconciliation", () => {
  it("replaces live summaries and keeps operational status order", () => {
    const result = mergeHelpdeskConversations(
      [summary("open", "Open", "2026-01-01T00:00:00Z")],
      [summary("new", "New", "2026-01-02T00:00:00Z"),
        summary("open", "Resolved", "2026-01-03T00:00:00Z")]);
    expect(result.map(item => `${item.conversationId}:${item.status}`))
      .toEqual(["new:New", "open:Resolved"]);
  });

  it("deduplicates transcript and raw events across live refreshes", () => {
    const base: HelpdeskConversationDetail = {
      conversation: summary("conversation", "New", "2026-01-01T00:00:00Z"),
      messages: [{ messageId: "one", conversationId: "conversation", sequence: 1,
        role: 0, body: "first", textDirection: "ltr", state: 2,
        createdAtUtc: "2026-01-01T00:00:00Z" }], actions: [], progressEvents: [],
      trace: [{ eventId: "one", conversationId: "conversation", sequence: 1,
        timestampUtc: "2026-01-01T00:00:00Z", direction: "AgentToControlPlane",
        eventType: "chat.message.user", outcome: "Completed", payload: {} }]
    };
    const incoming = structuredClone(base);
    incoming.messages.push({ ...incoming.messages[0], messageId: "two", sequence: 2, body: "reply", role: 3 });
    incoming.trace.push({ ...incoming.trace[0], eventId: "two", sequence: 2,
      timestampUtc: "2026-01-01T00:00:01Z", direction: "ControlPlaneToAgent" });
    const result = mergeHelpdeskDetail(base, incoming);
    expect(result.messages.map(item => item.messageId)).toEqual(["one", "two"]);
    expect(result.trace.map(item => item.eventId)).toEqual(["one", "two"]);
  });
});

describe("helpdesk escalation", () => {
  it("lists escalated conversations first", () => {
    const result = mergeHelpdeskConversations([], [
      summary("new", "New", "2026-01-03T00:00:00Z"),
      summary("stuck", "Escalated", "2026-01-01T00:00:00Z")]);
    expect(result.map(item => item.conversationId)).toEqual(["stuck", "new"]);
  });

  it("reads repair attempts from the timeline in order", () => {
    const detail: HelpdeskConversationDetail = {
      conversation: summary("c", "Escalated", "2026-01-01T00:00:00Z"),
      messages: [], actions: [], progressEvents: [],
      trace: [
        { eventId: "1", conversationId: "c", timestampUtc: "2026-01-01T00:00:01Z",
          direction: "Agent", eventType: "agent.repair_attempt", outcome: "Success",
          payload: { ToolName: "run_repair_package", ArtifactId: "spooler-a",
            Outcome: "Completed", Error: null, AttemptedAtUtc: "2026-01-01T00:00:01Z" } },
        { eventId: "2", conversationId: "c", timestampUtc: "2026-01-01T00:00:02Z",
          direction: "Agent", eventType: "agent.escalation", outcome: "Escalated",
          payload: { reason: "Still broken" } },
        { eventId: "3", conversationId: "c", timestampUtc: "2026-01-01T00:00:03Z",
          direction: "Agent", eventType: "agent.repair_attempt", outcome: "Error",
          payload: { ToolName: "run_repair_package", ArtifactId: "spooler-b",
            Outcome: "Failed", Error: "timed out", AttemptedAtUtc: "2026-01-01T00:00:03Z" } }
      ]
    };
    expect(repairAttempts(detail).map(item => `${item.artifactId}:${item.outcome}:${item.error ?? ""}`))
      .toEqual(["spooler-a:Completed:", "spooler-b:Failed:timed out"]);
  });
});
