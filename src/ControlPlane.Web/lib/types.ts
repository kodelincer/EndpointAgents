export type Agent = { agentId: string; hostname: string; machineName: string; userName: string; version: string; connectedAtUtc: string };
export type CommandDefinition = { method: string; name: string; description: string; defaultParams: unknown };
export type RpcRequest = { jsonRpc: "2.0"; id: string; method: string; params: unknown };
export type RpcResponse = { jsonRpc: string; id: string; result: unknown; error?: { code: number; message: string; data?: unknown } | null };
export type Activity = {
  eventId: string; type: "agent.connected" | "agent.disconnected" | "command.started" | "command.completed";
  timestampUtc: string; agentId: string; status: "online" | "offline" | "pending" | "success" | "error";
  requestId?: string; request?: RpcRequest; response?: RpcResponse; durationMs?: number; agent?: Agent;
};
export type ConnectionState = "connecting" | "live" | "reconnecting" | "offline";
export type EndpointFile = {
  artifactId: string; fileName: string; sizeBytes: number; lastModifiedUtc: string;
  sha256: string; downloadPath: string;
  category: string; displayName?: string | null; description?: string | null; kind?: string | null;
  supportedProductFamilies?: string[] | null; minOfficeVersion?: string | null; maxOfficeVersion?: string | null;
  maxOfficeVersionExclusive?: string | null;
  activationStates?: string[] | null; errorCodes?: string[] | null; requiresInteractiveUser?: boolean;
  requiresOfficeAppsClosed?: boolean; requiresRestart?: boolean;
};
export type FileInventory = {
  files: EndpointFile[];
  issues: { source: string; message: string }[];
  repository: {
    expectedPattern: string; officeManifestPath: string;
    officeBugfixManifestPath: string; downloadMethod: string;
  };
};

export type ApiSystemStatus = {
  version: string; machineName: string; startedAtUtc: string; uptimeSeconds: number;
  maintenanceMode: boolean; connectedAgents: number; retainedActivity: number;
};
export type MaintenanceModeResult = { maintenanceMode: boolean };

export type AgentRuntimeSnapshot = {
  agentId: string; machineName: string; version: string; connectionStatus: number; functionMode: number;
  startedAtUtc: string; lastConnectedAtUtc?: string | null; lastCommandAtUtc?: string | null;
  lastCommandMethod?: string | null; lastCommandSucceeded?: boolean | null; activeCommands: number;
  lastProblemCode?: string | null; lastProblemMessage?: string | null; lastProblemAtUtc?: string | null;
};
export type EndpointLogExcerpt = {
  timestampUtc: string; level: string; source: string; eventId?: number | null; message: string;
};
export type IbsngDiagnosticCheck = {
  name: string; status: "Pass" | "Fail" | "Warning" | "NotApplicable"; summary: string;
  detail?: string | null;
};
export type IbsngSupportEvidence = {
  generatedAtUtc: string; serverHost: string; protocol: number; connectionState: number;
  summary: string; checks: IbsngDiagnosticCheck[]; sanitizedLogLines: string[];
};
export type SupportRequest = {
  requestId: string; clientRequestId: string; receivedAtUtc: string; subject: string;
  messageText: string; textDirection: "auto" | "ltr" | "rtl"; agentId: string;
  machineName: string; userName: string; sessionId: number; runtime: AgentRuntimeSnapshot;
  logExcerpts: EndpointLogExcerpt[]; ibsngDiagnostics?: IbsngSupportEvidence | null;
};
