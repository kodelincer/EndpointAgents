# Graph Report - .  (2026-07-22)

## Corpus Check
- 284 files · ~306,779 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 3462 nodes · 8333 edges · 150 communities (142 shown, 8 thin omitted)
- Extraction: 97% EXTRACTED · 3% INFERRED · 0% AMBIGUOUS · INFERRED: 226 edges (avg confidence: 0.73)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- Live Browser Core
- Style Checks
- Web UI Pages
- Svelte Params
- Visual Contrast
- Design System
- Editing Lifecycle
- Screenshot Library
- CSP Detection
- Hook Library
- .NET Namespaces
- Manual Edit Commit
- Chat UI
- Detection Config
- Configure Bar
- Browser Script Assembly
- Svelte Styling
- Edit Hooks
- .NET Projects
- Manual Apply
- Dashboard API
- NPM Packages
- Admin Hooks
- Anti-Patterns
- C# Dispatch
- Typography Checks
- Live Insert
- WinForms Controls
- URL Detection
- CSS Cascade
- Manual Context
- Design Parser
- CLI Main
- Live Accept
- WinForms Graphics
- Hub & Agents
- Artifact Verify
- Copy Edit Agent
- API Tests
- Office Install Source
- Network Status
- Office Remediation
- Design Header
- File Artifacts
- TypeScript Types
- Manual Edit Evidence
- JSON Task
- Completion Ack
- Process Reconciliation
- Service Controller
- Exception Handling
- Browser Colors
- Manual Apply State
- Ibsng Quarantine
- Network/IP
- Insert UI
- Named Pipe IPC
- Color Checks
- Manual Discard
- Graphify References
- Context Build
- Background Service
- Office Volume Activation
- Visual Contrast Results
- Budget Clamp
- Health Check
- Agent Hub Normalize
- Ibsng Diagnose
- C# Async Task
- Office Activation
- Project Root
- Live Server
- RAS Connection
- Tray IPC Tests
- Platform Extract
- Ibsng UI Snapshot
- Registry Office
- Target Selection
- Pin Elements
- Agent Runtime State
- Context Source
- Color Blend
- Application Context
- Win32 DllImport
- Launch Settings
- Ibsng Inspector
- File Provider
- Browser Design Config
- Static Element
- Event Validation
- File Repository
- Support Request
- Critique Storage
- Svelte Text Map
- Pin Management
- Tray Icon
- Office Inventory
- Generated Regex
- Live Browser DOM
- Session Store
- Command Catalog
- Win32 GUID
- Endpoint Log
- Tray Program
- Pattern Discovery
- Motion Check
- Palette Build
- Event Log Test
- Startup Filter
- Loopback Guard
- C# List
- Office Activation Contracts
- C# Task
- Build Logs
- Workspace Pattern
- Inline Ignores
- Hook Config
- Ignore Value Display
- YAML/JSON Reader
- C# Enum
- Monorepo Roots
- Launch Settings
- Border Colors
- Section Kickers
- Registry Collection
- Agent Identity
- Concept Skills
- Retry Policy
- Win32 Marshal
- Hero Eyebrow Check
- Generated Header
- Office Inventory Test
- Detect Candidates
- Win32 DLL
- IPC Protocol
- Frontend Skills
- Next Config
- Next Env Types
- PostCSS Config
- Find Skills

## God Nodes (most connected - your core abstractions)
1. `SupportForm` - 59 edges
2. `el()` - 55 edges
3. `runHook()` - 36 edges
4. `setLiveState()` - 29 edges
5. `detectHtml()` - 28 edges
6. `initGlobalBar()` - 28 edges
7. `main()` - 27 edges
8. `ApiTests` - 27 edges
9. `collectBrowserFindings()` - 26 edges
10. `buildInsertConfigureRow()` - 26 edges

## Surprising Connections (you probably didn't know these)
- `createManualApplyController()` --indirect_call--> `event()`  [INFERRED]
  .agents/skills/impeccable/scripts/live/manual-apply.mjs → src/ControlPlane.Web/lib/activity.test.ts
- `bindEditBadgeProxy()` --indirect_call--> `event()`  [INFERRED]
  .agents/skills/impeccable/scripts/live-browser.js → src/ControlPlane.Web/lib/activity.test.ts
- `Project Root CLAUDE.md` --references--> `Graphify Skill`  [EXTRACTED]
  CLAUDE.md → .claude/skills/graphify/SKILL.md
- `latestCritique()` --indirect_call--> `v()`  [INFERRED]
  .agents/skills/impeccable/scripts/context-signals.mjs → .agents/skills/impeccable/scripts/modern-screenshot.umd.js
- `collectVisualContrastReasons()` --indirect_call--> `x()`  [INFERRED]
  .agents/skills/impeccable/scripts/detector/browser/injected/index.mjs → .agents/skills/impeccable/scripts/modern-screenshot.umd.js

## Import Cycles
- None detected.

## Communities (150 total, 8 thin omitted)

### Community 0 - "Live Browser Core"
Cohesion: 0.03
Nodes (135): acceptedDomAlreadyClean(), applyGlobalBarLabelState(), applyPlaceholderSizingStyles(), applySvelteComponentVariantStyle(), averageRgb01(), bufferToBase64(), buildCollapsible(), buildColorModels() (+127 more)

### Community 1 - "Style Checks"
Cohesion: 0.05
Nodes (98): borderColorsFromStyle(), borderWidthsFromStyle(), checkClippedOverflow(), checkColors(), checkCreamPalette(), checkElementAIPaletteDOM(), checkElementClippedOverflow(), checkElementClippedOverflowDOM() (+90 more)

### Community 2 - "Web UI Pages"
Cohesion: 0.05
Nodes (60): bindEditBadgeProxy(), proxyMouseEvent(), geist, metadata, mono, AppShell(), statusLabel(), ControlPlaneDashboard() (+52 more)

### Community 3 - "Svelte Params"
Cohesion: 0.06
Nodes (78): applyOriginalAttrsToSvelteAnchor(), applyParamDefaults(), applyParamValue(), applyPlaceholderDimensions(), applySavedSessionMeta(), buildCyclingRow(), buildInsertPlaceholderSnapshotFromDom(), buildParamsPanel() (+70 more)

### Community 4 - "Visual Contrast"
Cohesion: 0.06
Nodes (68): addBrowserFindings(), addVisualContrastFindings(), addVisualContrastResult(), analyzeVisualContrast(), analyzeVisualContrastCandidate(), blendRgba(), browserColorsClose(), browserDesignSystemConfig() (+60 more)

### Community 5 - "Design System"
Cohesion: 0.09
Nodes (55): addColorObject(), addDesignColor(), addRoundedScale(), addRoundedToken(), addSidecarColors(), addSidecarRadii(), addTypographyFonts(), addTypographySizes() (+47 more)

### Community 6 - "Editing Lifecycle"
Cohesion: 0.13
Nodes (54): abortSvelteComponentInjection(), cancelEditing(), cancelEditingToPicking(), cancelInsertConfigure(), cleanup(), cleanupAcceptedSession(), clearAnnotations(), clearInsertPicking() (+46 more)

### Community 7 - "Screenshot Library"
Cohesion: 0.09
Nodes (52): ae(), be(), bt(), Ce(), Ct(), de(), dt(), _e() (+44 more)

### Community 8 - "CSP Detection"
Cohesion: 0.07
Nodes (50): detectCsp(), INLINE_HEADER_SIGNALS, LAYOUT_EXTS, MONOREPO_HELPER_SIGNALS, NUXT_ROUTE_RULES_SIGNALS, NUXT_SECURITY_SIGNALS, SCAN_EXTS, SKIP_DIRS (+42 more)

### Community 9 - "Hook Library"
Cohesion: 0.07
Nodes (51): ACK_EXTS, applyConfigSource(), applyDetectorConfigSource(), applyPatchText(), clampByte(), CO_SCAN_STYLE_NAMES, coLocatedStylesheets(), colorIgnoreKey() (+43 more)

### Community 10 - ".NET Namespaces"
Cohesion: 0.10
Nodes (16): EndpointAgents.Platform.Windows.Ibsng, EndpointAgents.Shared.LocalIpc, EndpointAgents.Shared.Support, EndpointAgents.Tray, EndpointAgents.Shared.Office, EndpointAgents.Service, EndpointAgents.Shared.Rpc, EndpointAgents.Shared.Ibsng (+8 more)

### Community 11 - "Manual Edit Commit"
Cohesion: 0.10
Nodes (50): allEntryIds(), argVal(), buildRepairBatch(), candidatesForEntry(), changedFilesSinceSnapshot(), clearAppliedEntries(), collectApplyOwnedFiles(), collectRollbackFiles() (+42 more)

### Community 12 - "Chat UI"
Cohesion: 0.08
Nodes (50): armPageChatForTyping(), attachSteerFocusDebug(), attachSteerFocusGuard(), clearSteerAwaitTimer(), clearSteerFocusRecoverTimer(), collapsePageChat(), expandPageChat(), finishVoiceSession() (+42 more)

### Community 13 - "Detection Config"
Cohesion: 0.10
Nodes (47): applyDetectionConfigSource(), clampByte(), cleanIgnoreValueDisplay(), cloneDetectionConfig(), cloneRawDetectionConfig(), colorIgnoreKey(), DEFAULT_DETECTION_CONFIG, DETECTOR_CONFIG_KEYS (+39 more)

### Community 14 - "Configure Bar"
Cohesion: 0.08
Nodes (47): actionLabel(), applyConfigureBarChrome(), bindConfigureCountPillTooltip(), bindConfigureInlineControlHover(), bindConfigureModifierPillHover(), buildConfigureActionControl(), buildConfigureCountControl(), buildConfigureRow() (+39 more)

### Community 15 - "Browser Script Assembly"
Cohesion: 0.09
Nodes (43): assembleLiveBrowserScript(), assertLiveBrowserScriptParts(), LIVE_BROWSER_SCRIPT_PARTS, readLiveBrowserScriptParts(), resolveLiveBrowserScriptParts(), acknowledgePendingEvent(), activeSessionSummaries(), agentPollingConnected() (+35 more)

### Community 16 - "Svelte Styling"
Cohesion: 0.10
Nodes (44): applyLegacyDeferredAcceptsOnStartup(), appendCssToSvelteStyle(), appendSanitizedCssRule(), applyDeferredSvelteComponentAccepts(), bakeParamValuesInCss(), buildInsertVariantStub(), buildPropContract(), buildPropsScript() (+36 more)

### Community 17 - "Edit Hooks"
Cohesion: 0.10
Nodes (43): allow(), bumpCursorDenial(), cursorBlockMessage(), deny(), detectProposedHtml(), done(), escapeRegExp(), findingSignature() (+35 more)

### Community 18 - ".NET Projects"
Cohesion: 0.06
Nodes (33): ControlPlane.Web, Microsoft.AspNetCore.Mvc.Testing (10.0.9), Microsoft.AspNetCore.OpenApi (10.0.9), Microsoft.AspNetCore.SignalR.Client (10.0.9), Microsoft.Extensions.Hosting (10.0.9), Microsoft.Extensions.Logging (10.0.9), Microsoft.Extensions.Logging.EventLog (10.0.9), Microsoft.OpenApi (2.7.5) (+25 more)

### Community 19 - "Manual Apply"
Cohesion: 0.10
Nodes (36): addOpToManualApplyChunk(), APPLY_EVENT_HARD_TIMEOUT_MS, APPLY_EVENT_SOFT_DEADLINE_MS, buildManualApplyAgentAction(), clearManualApplyTransaction(), collectManualApplyFiles(), compactManualApplyBatch(), compactManualApplyCandidates() (+28 more)

### Community 20 - "Dashboard API"
Cohesion: 0.07
Nodes (25): ControlPlane.Api.Dashboard, Hub, ActivityStore, int, IReadOnlyList, object, Queue, DashboardActivity (+17 more)

### Community 21 - "NPM Packages"
Cohesion: 0.05
Nodes (40): eslint, eslint-config-next, jsdom, lucide-react, @microsoft/signalr, next, react, react-dom (+32 more)

### Community 22 - "Admin Hooks"
Cohesion: 0.14
Nodes (39): ACTIONS, addIgnoreFile(), addIgnoreRule(), addIgnoreValue(), DETECTOR_CONFIG_KEYS, detectorSection(), fileHasImpeccableHookMarker(), HOOK_MANIFEST_TARGETS (+31 more)

### Community 23 - "Anti-Patterns"
Cohesion: 0.09
Nodes (34): checkBorders(), checkClippedOverflow(), checkElementBorders(), checkElementBordersDOM(), checkElementClippedOverflow(), checkElementClippedOverflowDOM(), checkElementOversizedH1(), checkElementOversizedH1DOM() (+26 more)

### Community 24 - "C# Dispatch"
Cohesion: 0.07
Nodes (24): IReadOnlyDictionary, CancellationToken, ILogger, IReadOnlyCollection, Task, CommandDispatcher, CommandExecutionContext, CancellationToken (+16 more)

### Community 25 - "Typography Checks"
Cohesion: 0.08
Nodes (31): checkElementItalicSerif(), checkItalicSerif(), checkPageTypography(), resolveSerif(), CSS_IN_JS_EXTENSIONS, extractCSSinJS(), firstOverusedGoogleFont(), isNeutralBorderColor() (+23 more)

### Community 26 - "Live Insert"
Cohesion: 0.13
Nodes (35): argVal(), buildInsertWrapperLines(), computeInsertLine(), INSERT_POSITIONS, insertCli(), isInsertPosition(), resolveElementMatch(), buildSvelteComponentCssAuthoring() (+27 more)

### Community 27 - "WinForms Controls"
Cohesion: 0.08
Nodes (22): Button, ComboBox, Form, FormClosingEventArgs, Label, ListView, PictureBox, bool (+14 more)

### Community 28 - "URL Detection"
Cohesion: 0.14
Nodes (31): detectUrl(), runVisualContrastFallback(), serializeDesignSystemForBrowser(), detectText(), extFromFilePath(), runRegexMatchers(), runTextContentAnalyzers(), shouldRunPageAnalyzers() (+23 more)

### Community 29 - "CSS Cascade"
Cohesion: 0.10
Nodes (27): applyStaticDeclaration(), buildBorderOverrideMap(), collectStaticCssRules(), compareStaticPriority(), cssPropToCamel(), expandStaticBoxValues(), expandStaticDeclaration(), extractStaticColor() (+19 more)

### Community 30 - "Manual Context"
Cohesion: 0.07
Nodes (37): addManualContextText(), applyEditing(), buildLocatorForLeaf(), canRestoreManualEditElement(), collectManualContextPieces(), contextElementForManualEdit(), copyEditContainerContext(), copyEditLeafContext() (+29 more)

### Community 31 - "Design Parser"
Cohesion: 0.15
Nodes (33): buildColor(), CANONICAL_SECTIONS, collectBullets(), collectColorValues(), collectParagraphs(), detectFormat(), extractColors(), extractComponents() (+25 more)

### Community 32 - "CLI Main"
Cohesion: 0.15
Nodes (28): confirm(), detectCli(), formatFindings(), formatFindingSummary(), handleStdin(), printUsage(), createBrowserDetector(), extractStyleBlocks() (+20 more)

### Community 33 - "Live Accept"
Cohesion: 0.14
Nodes (32): acceptCli(), argVal(), buildCarbonizeReplacement(), decodeHtmlAttr(), deindentContent(), detectCommentSyntax(), escapeRegExp(), expandReplaceRange() (+24 more)

### Community 34 - "WinForms Graphics"
Cohesion: 0.10
Nodes (19): Control, EventArgs, GraphicsPath, MessageLayout, PaintEventArgs, Panel, Rectangle, RichTextBox (+11 more)

### Community 35 - "Hub & Agents"
Cohesion: 0.10
Nodes (17): EndpointAgents.Shared.Agents, ControlPlane.Api.Support, ControlPlane.Api.Hubs, ControlPlane.Api.Agents, AgentRegistry, OnlineAgent, OnlineAgentDto, CancellationToken (+9 more)

### Community 36 - "Artifact Verify"
Cohesion: 0.09
Nodes (19): HttpResponseMessage, CancellationToken, Task, IbsngArtifactInspection, IbsngArtifactVerifier, IbsngAtomicInstaller, CancellationToken, ConcurrentDictionary (+11 more)

### Community 37 - "Copy Edit Agent"
Cohesion: 0.14
Nodes (31): applyMockWrites(), buildCopyEditBatchPrompt(), checkFrameworkSourceSyntax(), chooseCopyEditAgent(), COMMAND_AUTH_CACHE, commandAuthed(), commandExists(), compactBatchForPrompt() (+23 more)

### Community 38 - "API Tests"
Cohesion: 0.12
Nodes (17): IClassFixture, ApiTests, Command, Event, FileInventory, FileItem, FileRepository, MaintenanceResult (+9 more)

### Community 39 - "Office Install Source"
Cohesion: 0.12
Nodes (18): IReadOnlyCollection, IReadOnlyList, RegistryView, IOfficeInstallationSource, Action, CancellationToken, Func, List (+10 more)

### Community 40 - "Network Status"
Cohesion: 0.16
Nodes (13): InternetStatus, NetworkInterface, PhysicalAddress, DiagnosticWarning, CancellationToken, Exception, Func, ILogger (+5 more)

### Community 41 - "Office Remediation"
Cohesion: 0.11
Nodes (16): CancellationToken, Task, OfficeRemediationLauncher, CancellationToken, IReadOnlyList, ProcessResult, Task, TimeSpan (+8 more)

### Community 42 - "Design Header"
Cohesion: 0.13
Nodes (28): barPaletteForTheme(), brandMarkSvg(), buildDesignHeader(), buildSteerProcessingDots(), designPanelCss(), detectPageTheme(), ensureAgentPollTooltip(), fetchDesignSystem() (+20 more)

### Community 43 - "File Artifacts"
Cohesion: 0.15
Nodes (12): long, FileArtifact, FileArtifactRepository, OfficeArtifactCatalogSnapshot, OfficeFileArtifact, CancellationToken, GeneratedRegex, HashSet (+4 more)

### Community 44 - "TypeScript Types"
Cohesion: 0.07
Nodes (27): dom, dom.iterable, esnext, .next/dev/types/**/*.ts, next-env.d.ts, .next/types/**/*.ts, node_modules, **/*.ts (+19 more)

### Community 45 - "Manual Edit Evidence"
Cohesion: 0.16
Nodes (26): analyzeSourceHint(), buildCandidatesForOp(), buildContextHintsByRef(), buildManualEditEvidence(), collectSearchFiles(), countOps(), decodeBasicHtml(), escapeRegExp() (+18 more)

### Community 46 - "JSON Task"
Cohesion: 0.12
Nodes (14): CancellationToken, JsonElement, Task, IbsngLaunchCommandHandler, CancellationToken, Task, ITrayCommandClient, IOfficeUserSessionSource (+6 more)

### Community 47 - "Completion Ack"
Cohesion: 0.18
Nodes (24): completionAckForAcceptResult(), completionTypeForAcceptResult(), augmentEventWithAcceptHandling(), buildAcceptScriptArgs(), buildPollReplyPayload(), EVENT_TYPES_NEEDING_AGENT_REPLY, fetchNextEvent(), fetchServerStatus() (+16 more)

### Community 48 - "Process Reconciliation"
Cohesion: 0.21
Nodes (13): DateTime, ProcessCandidate, ProcessReconciliation, TrayIpcRequest, TrayIpcResponse, CancellationToken, IEnumerable, IReadOnlyList (+5 more)

### Community 49 - "Service Controller"
Cohesion: 0.14
Nodes (13): ServiceController, ServiceControllerStatus, Fact, InlineData, Theory, AgentRuntimeStateTests, DateTimeOffset, TrayStatusResolver (+5 more)

### Community 50 - "Exception Handling"
Cohesion: 0.11
Nodes (14): Exception, CancellationToken, GeneratedRegex, JsonElement, Regex, string, Task, OfficeRemediationCommandHandler (+6 more)

### Community 51 - "Browser Colors"
Cohesion: 0.11
Nodes (25): browserColorsClose(), browserHasDirectText(), browserRadiusTokens(), browserSampleText(), checkCreamPalette(), checkElementDesignSystemDOM(), checkElementQuality(), checkElementQualityDOM() (+17 more)

### Community 52 - "Manual Apply State"
Cohesion: 0.18
Nodes (25): clearStoredManualApplyState(), fetchPendingCount(), handleManualEditActivity(), hidePendingApplyDock(), manualApplyLoadingText(), manualApplyStateKey(), manualEditEventForCurrentPage(), numberOrNull() (+17 more)

### Community 53 - "Ibsng Quarantine"
Cohesion: 0.14
Nodes (11): IbsngArtifactQuarantine, CancellationToken, Task, IbsngDesktopCandidate, IbsngDesktopInventory, IbsngDesktopReconciler, IbsngDesktopReconciliation, IbsngOptions (+3 more)

### Community 54 - "Network/IP"
Cohesion: 0.26
Nodes (10): CancellationToken, ILogger, IPAddress, IReadOnlyList, List, Task, IbsngDiagnosticService, IbsngDiagnosticCheck (+2 more)

### Community 55 - "Insert UI"
Cohesion: 0.11
Nodes (10): canCreateInsert(), clampPlaceholderSize(), computeInsertPosition(), groupSiblingRows(), hitSiblingInsertGap(), horizontalOverlap(), insertCreateDisabledReason(), insertLineCoords() (+2 more)

### Community 56 - "Named Pipe IPC"
Cohesion: 0.24
Nodes (9): CancellationToken, DateTimeOffset, NamedPipeServerStream, object, StreamReader, Task, ServiceIpcServer, ServiceIpcRequest (+1 more)

### Community 57 - "Color Checks"
Cohesion: 0.19
Nodes (23): checkColors(), checkElementAIPaletteDOM(), checkElementColors(), checkElementColorsDOM(), checkElementGlow(), checkElementGlowDOM(), checkElementIconTile(), checkElementIconTileDOM() (+15 more)

### Community 58 - "Manual Discard"
Cohesion: 0.19
Nodes (19): args, cwd, pageUrlFilter, remaining, compactManualLogText(), summarizeManualApplyFailures(), summarizeManualDiagnostics(), summarizeManualLogFile() (+11 more)

### Community 59 - "Graphify References"
Cohesion: 0.11
Nodes (23): Claude Code Instructions, Add Watch Reference, Exports Reference, Extraction Spec, GitHub Merge Reference, Hooks Reference, Query Reference, Transcribe Reference (+15 more)

### Community 60 - "Context Build"
Cohesion: 0.16
Nodes (20): buildMissingTargetDirective(), buildResolvedContextDirective(), buildTargetSelectionDirective(), buildUpdateDirective(), cli(), compareSemver(), computeUpdateDirective(), DESIGN_NAMES (+12 more)

### Community 61 - "Background Service"
Cohesion: 0.18
Nodes (12): BackgroundService, HubConnection, Exception, CancellationToken, DateTimeOffset, ILogger, SemaphoreSlim, string (+4 more)

### Community 62 - "Office Volume Activation"
Cohesion: 0.16
Nodes (9): IOfficeVolumeActivationProbe, CancellationToken, Fact, InlineData, Task, Theory, FakeCatalogClient, FakeVolumeProbe (+1 more)

### Community 63 - "Visual Contrast Results"
Cohesion: 0.13
Nodes (21): addBrowserFindings(), addVisualContrastFindings(), addVisualContrastResult(), analyzeVisualContrast(), analyzeVisualContrastCandidate(), checkElementTextOverflowDOM(), classSelector(), clearOverlays() (+13 more)

### Community 64 - "Budget Clamp"
Cohesion: 0.15
Nodes (21): bumpEditCount(), clampGroupedToBudget(), clampToBudget(), dedupeAgainstCache(), depthIsSet(), directiveFooter(), ensureFile(), ensureSession() (+13 more)

### Community 65 - "Health Check"
Cohesion: 0.10
Nodes (14): ControlPlane.Api.System, HealthCheckContext, HealthCheckResult, IHealthCheck, PathString, ControlPlaneRuntimeState, DateTimeOffset, int (+6 more)

### Community 66 - "Agent Hub Normalize"
Cohesion: 0.13
Nodes (14): AgentHub, Exception, Task, CancellationToken, Task, AgentRegistration, string, AgentConnectionStatus (+6 more)

### Community 67 - "Ibsng Diagnose"
Cohesion: 0.14
Nodes (9): JsonElement, IbsngDiagnoseCommandHandler, IReadOnlyCollection, Fact, InlineData, Theory, IbsngConnectionLogParserTests, IbsngConnectionState (+1 more)

### Community 68 - "C# Async Task"
Cohesion: 0.13
Nodes (13): CancellationToken, IReadOnlyList, Task, OfficeActivationCatalogClient, CancellationToken, IReadOnlyList, Task, IOfficeActivationCatalogClient (+5 more)

### Community 69 - "Office Activation"
Cohesion: 0.23
Nodes (8): OfficeActivationProduct, Dictionary, GeneratedRegex, IEnumerable, IReadOnlyList, List, Regex, OfficeActivationOutputParser

### Community 70 - "Project Root"
Cohesion: 0.20
Nodes (18): resolveProjectRoot(), firstExisting(), getDesignSidecarCandidates(), getDesignSidecarPath(), getImpeccableDir(), getLegacyLiveAnnotationsDir(), getLegacyLiveConfigPath(), getLegacyLiveServerPath() (+10 more)

### Community 71 - "Live Server"
Cohesion: 0.21
Nodes (17): isLiveServerPidReachable(), readLiveServerInfo(), completeCli(), completeThroughServer(), parseArgs(), readServerInfo(), collectManualApplyFiles(), manualApplyReplyCommand() (+9 more)

### Community 72 - "RAS Connection"
Cohesion: 0.16
Nodes (12): RasConnectionStatus, RasStatus, DllImport, int, IntPtr, IReadOnlyList, string, NativeMethods (+4 more)

### Community 73 - "Tray IPC Tests"
Cohesion: 0.19
Nodes (10): Fact, Task, TrayIpcTests, CancellationToken, StreamReader, Task, TrayCommandClient, int (+2 more)

### Community 74 - "Platform Extract"
Cohesion: 0.20
Nodes (16): extractPlatform(), extractRegister(), extractSectionValue(), loadContext(), safeRead(), cli(), COMMON_DEV_PORTS, devServerSignals() (+8 more)

### Community 75 - "Ibsng UI Snapshot"
Cohesion: 0.20
Nodes (8): AutomationElement, IbsngDialerUiSnapshot, CancellationToken, int, Process, Task, IbsngDialerUiInspector, WindowRectangle

### Community 76 - "Registry Office"
Cohesion: 0.18
Nodes (8): RegistryKey, IReadOnlyCollection, IReadOnlyList, RegistryView, string, WindowsOfficeInstallationSource, InlineData, Theory

### Community 77 - "Target Selection"
Cohesion: 0.22
Nodes (13): resolveTargetSelection(), parseTargetOptions(), parseTargetPath(), TargetArgError, __dirname, ensureServerRunning(), globToRegex(), liveCli() (+5 more)

### Community 78 - "Pin Elements"
Cohesion: 0.20
Nodes (17): beginEditPin(), buildAnnotationsForCapture(), buildPinElement(), cancelEditingPin(), clampPlaceholderSize(), finalizeEditingPin(), initAnnotOverlay(), localCoords() (+9 more)

### Community 79 - "Agent Runtime State"
Cohesion: 0.19
Nodes (6): bool, DateTimeOffset, int, object, string, AgentRuntimeState

### Community 80 - "Context Source"
Cohesion: 0.14
Nodes (16): contextSourcePath(), contextSourceStatus(), findMonorepoRoot(), firstExisting(), hasGitBoundary(), isCandidateProjectRoot(), isPathInside(), isPathInsideOrEqual() (+8 more)

### Community 81 - "Color Blend"
Cohesion: 0.18
Nodes (16): blendRgba(), clampByte(), firstCssUrl(), getLayerValue(), loadVisualContrastImage(), parseObjectPosition(), parsePositionPair(), parsePositionToken() (+8 more)

### Community 82 - "Application Context"
Cohesion: 0.17
Nodes (11): ApplicationContext, NotifyIcon, SupportFormTab, bool, CancellationTokenSource, int, Task, Timer (+3 more)

### Community 83 - "Win32 DllImport"
Cohesion: 0.30
Nodes (7): EnumWindowsProc, DllImport, IntPtr, MarshalAs, NativeMethods, StringBuilder, WindowRectangle

### Community 84 - "Launch Settings"
Cohesion: 0.13
Nodes (15): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, applicationUrl, commandName (+7 more)

### Community 85 - "Ibsng Inspector"
Cohesion: 0.20
Nodes (10): CancellationToken, IReadOnlyList, Task, IbsngConnectionInspector, IbsngConnectionStateResult, IbsngDiagnosticReport, IbsngRasConnectionSnapshot, IbsngSupportEvidence (+2 more)

### Community 86 - "File Provider"
Cohesion: 0.37
Nodes (6): IFileProvider, IHostEnvironment, FileArtifactRepositoryTests, TestHostEnvironment, Fact, Task

### Community 87 - "Browser Design Config"
Cohesion: 0.16
Nodes (14): browserDesignSystemConfig(), browserFindingsFromMap(), browserPrimaryFont(), checkBrowserDesignSystemSources(), checkElementItalicSerifDOM(), checkHtmlPatterns(), checkPageQualityDOM(), checkPageQualityFromDoc() (+6 more)

### Community 89 - "Event Validation"
Cohesion: 0.26
Nodes (12): FORBIDDEN_MANUAL_EDIT_TEXT_CHARS, INSERT_POSITIONS, isValidId(), isValidVariantId(), validateAnnotationFields(), validateEvent(), validateInsertGenerate(), validateManualEditEvent() (+4 more)

### Community 90 - "File Repository"
Cohesion: 0.18
Nodes (8): ControlPlane.Api.Files, ControlPlane.Api.Tests, FileRepositoryItem, FileRepositoryOptions, FileRepositoryIssue, OfficeArtifactManifest, OfficeArtifactManifestEntry, List

### Community 91 - "Support Request"
Cohesion: 0.20
Nodes (9): SupportRequestStore, Dictionary, int, IReadOnlyList, object, Queue, SupportRequestStoreTests, Fact (+1 more)

### Community 92 - "Critique Storage"
Cohesion: 0.32
Nodes (11): kebab(), listSnapshotsForSlug(), main(), nowFilenameStamp(), parseFrontmatter(), readLatestSnapshot(), readTrend(), serializeFrontmatter() (+3 more)

### Community 93 - "Svelte Text Map"
Cohesion: 0.24
Nodes (13): buildSvelteExpressionTextMap(), buildSveltePropValuesFromLiveElement(), collectTextNodes(), elementMatchesOriginalMarkup(), escapeRegExp(), expressionTextMatcher(), findLiveElementForOriginalMarkup(), findLiveElementFromAnchorSnapshot() (+5 more)

### Community 94 - "Pin Management"
Cohesion: 0.23
Nodes (11): CODEX_HARNESSES, commandPrefixForSkillsDir(), __dirname, findHarnessDirs(), generatePinnedSkill(), HARNESS_DIRS, loadCommandMetadata(), pin() (+3 more)

### Community 95 - "Tray Icon"
Cohesion: 0.19
Nodes (9): IDisposable, Dictionary, DllImport, Icon, IntPtr, MarshalAs, NativeMethods, TrayIconFactory (+1 more)

### Community 96 - "Office Inventory"
Cohesion: 0.22
Nodes (9): OfficeInventory, IReadOnlyList, OfficeProductFamilyDetector, CancellationToken, ProcessResult, Task, TimeSpan, OfficeVolumeActivationProbe (+1 more)

### Community 97 - "Generated Regex"
Cohesion: 0.41
Nodes (5): GeneratedRegex, IEnumerable, int, Regex, IbsngConnectionLogParser

### Community 98 - "Live Browser DOM"
Cohesion: 0.23
Nodes (10): createLiveBrowserDomHelpers(), activeElementDeep(), appendStyleToLiveUiRoot(), appendToLiveUiRoot(), escapeCssIdent(), getLiveUiElementById(), LIVE_CHROME_MOUNT_CONTRACT, LIVE_UI_COMPONENT_IDS (+2 more)

### Community 99 - "Session Store"
Cohesion: 0.27
Nodes (9): applyEvent(), baseSnapshot(), COMPLETED_PHASES, getJournalPath(), getSnapshotPath(), rebuildSnapshotFromJournal(), safeSessionId(), toPendingEvent() (+1 more)

### Community 100 - "Command Catalog"
Cohesion: 0.20
Nodes (7): ControlPlane.Api.Commands, CommandCatalog, CommandDefinition, IReadOnlyList, SensitiveCommandParameterGuard, HashSet, JsonElement

### Community 101 - "Win32 GUID"
Cohesion: 0.26
Nodes (9): Guid, DllImport, IntPtr, string, AuthenticodeTrust, WinTrustData, WinTrustFileInfo, uint (+1 more)

### Community 102 - "Endpoint Log"
Cohesion: 0.27
Nodes (7): EndpointLogExcerpt, CancellationToken, IReadOnlyList, Task, TimeSpan, EndpointEventLogEntry, EndpointEventLogReader

### Community 103 - "Tray Program"
Cohesion: 0.20
Nodes (7): Program, CancellationToken, Task, ElevatedServiceActionRunner, ServiceActionResult, ServiceControlLauncher, STAThread

### Community 104 - "Pattern Discovery"
Cohesion: 0.25
Nodes (11): directChildDirs(), discoverRootsForPattern(), discoverTargetCandidates(), expandSimplePattern(), findTargetExample(), hasFallbackWorkspaceChildren(), isIgnoredWorkspaceDiscoveryDir(), isMonorepoRoot() (+3 more)

### Community 105 - "Motion Check"
Cohesion: 0.22
Nodes (11): checkElementMotion(), checkElementMotionDOM(), checkLayout(), checkMotion(), checkPageLayout(), isCardLike(), isCardLikeDOM(), isCardLikeFromProps() (+3 more)

### Community 106 - "Palette Build"
Cohesion: 0.24
Nodes (7): args, buildWeights(), hashUnit(), pickSeed(), seed, SEEDS, weightedPick()

### Community 107 - "Event Log Test"
Cohesion: 0.24
Nodes (3): EventLogEntryType, Fact, TrayEventLogger

### Community 108 - "Startup Filter"
Cohesion: 0.18
Nodes (8): IApplicationBuilder, IStartupFilter, IWebHostBuilder, ControlPlaneApiFactory, TestConnectionStartupFilter, Action, string, WebApplicationFactory

### Community 109 - "Loopback Guard"
Cohesion: 0.20
Nodes (6): LoopbackRequestGuard, IPAddress, LoopbackRequestGuardTests, Fact, InlineData, Theory

### Community 110 - "C# List"
Cohesion: 0.36
Nodes (6): CancellationToken, IReadOnlyList, List, Task, OfficeActivationService, OfficeActivationProbeResult

### Community 111 - "Office Activation Contracts"
Cohesion: 0.27
Nodes (10): string, OfficeActivationActions, OfficeActivationMechanisms, OfficeActivationReport, OfficeActivationStates, OfficeProductFamilies, OfficeRemediationArtifactKinds, OfficeRemediationCatalog (+2 more)

### Community 112 - "C# Task"
Cohesion: 0.45
Nodes (4): CancellationToken, Task, TimeSpan, ServiceIpcClient

### Community 114 - "Workspace Pattern"
Cohesion: 0.29
Nodes (10): escapeRegExp(), isExcludedByWorkspacePattern(), MONOREPO_FALLBACK_PROJECT_DIRS, nearestProjectLikeRoot(), normalizeWorkspacePattern(), projectRootFromDoubleStarPattern(), projectRootFromWorkspacePattern(), resolveWorkspaceProjectRoot() (+2 more)

### Community 115 - "Inline Ignores"
Cohesion: 0.40
Nodes (9): addRules(), applyInlineIgnores(), getSet(), hasDirectives(), isInlineIgnored(), normalizeRule(), parseInlineIgnores(), parseRuleList() (+1 more)

### Community 116 - "Hook Config"
Cohesion: 0.24
Nodes (9): cloneDefaultConfig(), detectorSection(), hookSection(), readCache(), readConfig(), safeReadJson(), writeAuditLog(), main() (+1 more)

### Community 117 - "Ignore Value Display"
Cohesion: 0.36
Nodes (10): cleanIgnoreValueDisplay(), extractFindingIgnoreValue(), extractFindingIgnoreValueRaw(), extractMotionIgnoreValue(), filterFindings(), formatFindingIgnoreCommand(), isIgnoredFindingValue(), normalizeIgnoreRule() (+2 more)

### Community 118 - "YAML/JSON Reader"
Cohesion: 0.25
Nodes (9): buildSelectorSegment(), generateSelector(), isElementHidden(), isLikelyHashedClass(), postSerializedFindings(), renderBrowserFindings(), scanResultMeta(), serializeFindings() (+1 more)

### Community 119 - "C# Enum"
Cohesion: 0.22
Nodes (3): Enum, DateTimeOffset, Icon

### Community 120 - "Monorepo Roots"
Cohesion: 0.32
Nodes (8): parseYamlFlowList(), readJson(), readLernaWorkspaces(), readPackageWorkspaces(), readPnpmWorkspaces(), readWorkspacePatterns(), stripYamlInlineComment(), unquoteYamlValue()

### Community 121 - "Launch Settings"
Cohesion: 0.25
Nodes (7): commandName, dotnetRunMessages, environmentVariables, DOTNET_ENVIRONMENT, profiles, EndpointAgents.Service, $schema

### Community 122 - "Border Colors"
Cohesion: 0.38
Nodes (7): borderColorsFromStyle(), borderWidthsFromStyle(), checkElementGptBorderShadow(), checkElementGptBorderShadowDOM(), checkGptThinBorderWideShadow(), shadowLayerAlpha(), shadowMaxBlurPx()

### Community 123 - "Section Kickers"
Cohesion: 0.33
Nodes (7): checkRepeatedSectionKickers(), checkRepeatedSectionKickersDOM(), checkRepeatedSectionKickersFromDoc(), cleanInlineText(), collectRepeatedSectionKickerCandidates(), isRepeatedKickerCandidate(), isRepeatedKickerCardContext()

### Community 124 - "Registry Collection"
Cohesion: 0.43
Nodes (4): IReadOnlyCollection, IReadOnlyList, RegistryView, ActivationInventorySource

### Community 125 - "Agent Identity"
Cohesion: 0.33
Nodes (4): ILogger, object, string, AgentIdentityProvider

### Community 126 - "Concept Skills"
Cohesion: 0.40
Nodes (6): Caveman Mode, Token Compression, YAGNI Principle, Caveman README, Caveman Skill, Ponytail Skill

### Community 127 - "Retry Policy"
Cohesion: 0.40
Nodes (4): IRetryPolicy, RetryContext, TimeSpan, JitteredRetryPolicy

### Community 128 - "Win32 Marshal"
Cohesion: 0.53
Nodes (4): DllImport, IntPtr, MarshalAs, NativeMethods

### Community 129 - "Hero Eyebrow Check"
Cohesion: 0.40
Nodes (5): checkElementHeroEyebrow(), checkElementHeroEyebrowDOM(), checkHeroEyebrow(), isAccentColor(), resolveVarRefs()

### Community 130 - "Generated Header"
Cohesion: 0.70
Nodes (4): hasGeneratedHeader(), HEADER_MARKERS, isGeneratedFile(), isGitIgnored()

### Community 132 - "Detect Candidates"
Cohesion: 0.50
Nodes (3): candidates, detectorPath, __dirname

### Community 135 - "Win32 DLL"
Cohesion: 0.50
Nodes (3): DllImport, IntPtr, NativeMethods

### Community 137 - "IPC Protocol"
Cohesion: 0.67
Nodes (3): int, string, ServiceIpcProtocol

## Knowledge Gaps
- **229 isolated node(s):** `COMMON_DEV_PORTS`, `SOURCE_DIRS`, `PRODUCT_NAMES`, `DESIGN_NAMES`, `FALLBACK_DIRS` (+224 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **8 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `el()` connect `Configure Bar` to `Live Browser Core`, `Style Checks`, `Svelte Params`, `Visual Contrast`, `Design System`, `Editing Lifecycle`, `Motion Check`, `Design Header`, `Chat UI`, `Browser Colors`, `YAML/JSON Reader`, `Browser Design Config`, `Typography Checks`, `Section Kickers`, `URL Detection`, `CSS Cascade`, `Visual Contrast Results`?**
  _High betweenness centrality (0.096) - this node is a cross-community bridge._
- **Why does `v()` connect `Screenshot Library` to `Live Browser Core`, `Hero Eyebrow Check`, `Style Checks`, `Svelte Params`, `Platform Extract`, `Design Parser`?**
  _High betweenness centrality (0.045) - this node is a cross-community bridge._
- **Why does `event()` connect `Web UI Pages` to `Manual Apply`?**
  _High betweenness centrality (0.030) - this node is a cross-community bridge._
- **Are the 29 inferred relationships involving `el()` (e.g. with `browserFindingsFromMap()` and `collectVisualContrastCandidates()`) actually correct?**
  _`el()` has 29 INFERRED edges - model-reasoned connections that need verification._
- **What connects `COMMON_DEV_PORTS`, `SOURCE_DIRS`, `PRODUCT_NAMES` to the rest of the system?**
  _229 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Live Browser Core` be split into smaller, more focused modules?**
  _Cohesion score 0.028879720723579815 - nodes in this community are weakly interconnected._
- **Should `Style Checks` be split into smaller, more focused modules?**
  _Cohesion score 0.04554455445544554 - nodes in this community are weakly interconnected._