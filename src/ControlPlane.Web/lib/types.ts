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
export type OfficeBugfixCompatibility = {
  minOfficeVersion: string; maxOfficeVersionExclusive: string;
  officeProductReleaseIds: string[]; minWindowsVersion?: string | null;
  maxWindowsVersionExclusive?: string | null;
};
export type WindowsBugfixCompatibility = {
  minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds: string[];
};
export type EndpointFile = {
  artifactId: string; fileName: string; sizeBytes: number; lastModifiedUtc: string;
  sha256: string; downloadPath: string;
  category: string; displayName?: string | null; description?: string | null; kind?: string | null;
  supportedProductFamilies?: string[] | null; minOfficeVersion?: string | null; maxOfficeVersion?: string | null;
    maxOfficeVersionExclusive?: string | null;
    officeProductReleaseIds?: string[] | null; minWindowsVersion?: string | null;
    maxWindowsVersionExclusive?: string | null;
    editionIds?: string[] | null;
    additionalOfficeCompatibility?: OfficeBugfixCompatibility[] | null;
    additionalWindowsCompatibility?: WindowsBugfixCompatibility[] | null;
  activationStates?: string[] | null; errorCodes?: string[] | null; requiresInteractiveUser?: boolean;
  requiresOfficeAppsClosed?: boolean; requiresRestart?: boolean;
};
export type FileInventory = {
  files: EndpointFile[];
  issues: { source: string; message: string }[];
  repository: {
    expectedPattern: string; officeManifestPath: string;
      officeBugfixManifestPath: string; windowsBugfixFilePath: string;
      windowsBugfixManifestPath?: string;
    windowsHelloSampleFilePath: string; protectedAclRequired: boolean;
    downloadMethod: string;
  };
};

export type PlaybookChoice = { expect: string; send: string; submit: string };
export type PlaybookStep = PlaybookChoice & {
  timeoutSeconds: number; alternatives?: PlaybookChoice[] | null;
  optional?: boolean; maxRepeats?: number;
};
export type Playbook = {
  version: number; steps: PlaybookStep[]; successExitCodes?: number[] | null;
  successMarker?: string | null; failureMarkers?: string[] | null;
  guiDialogGraceSeconds?: number | null; arguments?: string[] | null;
  timeoutSeconds?: number | null; restartExitCodes?: number[] | null;
};
export type PackageStatus = "Ready" | "ReadyWithWarnings" | "HashMismatch"
  | "InvalidPlaybook" | "LintRejected" | "Invalid";
export type BatPackage = {
  id: string; folder: string; status: PackageStatus; reasons: string[]; warnings: string[];
  kind?: "office" | "windows" | "general" | null; displayName?: string | null; description?: string | null;
  fileType?: "bat" | "exe" | string | null; symptoms?: string[] | null;
  compatibility?: { minWindowsVersion?: string | null; maxWindowsVersionExclusive?: string | null } | null;
  signer?: string | null; exeSubsystem?: "Console" | "GUI" | string | null;
  batFileName?: string | null; pinnedBatSha256?: string | null; actualBatSha256?: string | null;
  batSizeBytes?: number | null; pinnedPlaybookSha256?: string | null;
  computedPlaybookSha256?: string | null; playbook?: Playbook | null;
  supportFiles: { fileName: string; sha256: string }[];
  office?: {
    minOfficeVersion: string; maxOfficeVersionExclusive: string;
    officeProductReleaseIds?: string[] | null; minWindowsVersion?: string | null;
    maxWindowsVersionExclusive?: string | null;
  } | null;
  windows?: {
    minWindowsVersion: string; maxWindowsVersionExclusive: string; editionIds?: string[] | null;
  } | null;
  review?: { reviewedBy?: string | null; reviewedOn?: string | null;
    source?: string | null; notes?: string | null } | null;
};
export type PackageInventory = {
  packagesPath: string; protectedAclRequired: boolean;
  limits: {
    maxSteps: number; maxResponses: number; maxResponseCharacters: number;
    minStepTimeoutSeconds: number; maxStepTimeoutSeconds: number;
    defaultGuiDialogGraceSeconds: number; maxRunMinutes: number; maxOutputBytes: number;
    defaultRunTimeoutSeconds?: number; minRunTimeoutSeconds?: number;
    maxRunTimeoutSeconds?: number; maxArguments?: number;
  };
  packages: BatPackage[];
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

export type HelpdeskConversationSummary = {
  conversationId: string; agentId: string; userName: string; sessionId: number;
  createdAtUtc: string; updatedAtUtc: string; lastSequence: number;
  status: "New" | "Open" | "Resolved"; responderMode: "Hermes" | "Operator";
  unreadCount: number; preview: string; textDirection: "auto" | "ltr" | "rtl";
  machineName: string; version: string; isOnline: boolean;
};
export type ChatMessage = {
  messageId: string; conversationId: string; sequence: number; role: 0 | 1 | 2 | 3;
  body: string; textDirection: "auto" | "ltr" | "rtl"; state: number;
  createdAtUtc: string; runId?: string | null;
};
export type HelpdeskTraceEvent = {
  eventId: string; conversationId: string; sequence?: number | null;
  timestampUtc: string; direction: "AgentToControlPlane" | "ControlPlaneToAgent";
  eventType: string; outcome: string; payload: unknown;
};
export type HelpdeskConversationDetail = {
  conversation: HelpdeskConversationSummary; messages: ChatMessage[];
  actions: unknown[]; progressEvents: unknown[]; trace: HelpdeskTraceEvent[];
};
