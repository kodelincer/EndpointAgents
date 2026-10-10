import type { HelpdeskConversationDetail, HelpdeskConversationSummary } from "./types";

// Escalated conversations need a person, so they come first.
const statusOrder = { Escalated: 0, New: 1, Open: 2, Resolved: 3 } as const;

export function mergeHelpdeskConversations(current: HelpdeskConversationSummary[],
  incoming: HelpdeskConversationSummary[]) {
  void current; // The REST snapshot is authoritative; live changes only trigger its refresh.
  const byId = new Map<string, HelpdeskConversationSummary>();
  incoming.forEach(item => byId.set(item.conversationId, item));
  return [...byId.values()].sort((a, b) => statusOrder[a.status] - statusOrder[b.status]
    || b.updatedAtUtc.localeCompare(a.updatedAtUtc)).slice(0, 200);
}

export function mergeHelpdeskDetail(current: HelpdeskConversationDetail | undefined,
  incoming: HelpdeskConversationDetail) {
  if (!current || current.conversation.conversationId !== incoming.conversation.conversationId)
    return incoming;
  const messages = new Map(current.messages.map(item => [item.messageId, item]));
  incoming.messages.forEach(item => messages.set(item.messageId, item));
  const trace = new Map(current.trace.map(item => [item.eventId, item]));
  incoming.trace.forEach(item => trace.set(item.eventId, item));
  return {
    ...incoming,
    messages: [...messages.values()].sort((a, b) => a.sequence - b.sequence),
    trace: [...trace.values()].sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc))
  };
}

export type RepairAttemptView = {
  artifactId: string; tool: string; outcome: string; error?: string | null; attemptedAtUtc: string;
};

/** Repairs the agent ran in this conversation, oldest first, from its timeline. */
export function repairAttempts(detail: HelpdeskConversationDetail): RepairAttemptView[] {
  return detail.trace
    .filter(item => item.eventType === "agent.repair_attempt")
    .map(item => {
      const payload = item.payload as Record<string, unknown>;
      return {
        artifactId: String(payload.ArtifactId ?? ""), tool: String(payload.ToolName ?? ""),
        outcome: String(payload.Outcome ?? item.outcome),
        error: typeof payload.Error === "string" ? payload.Error : null,
        attemptedAtUtc: String(payload.AttemptedAtUtc ?? item.timestampUtc)
      };
    });
}
