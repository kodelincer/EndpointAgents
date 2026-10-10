import type { BatPackage, PackageStatus, Playbook, PlaybookChoice, PlaybookStep } from "./types";

export type StatusTone = "ready" | "warning" | "blocked";

const statusText: Record<PackageStatus, { label: string; tone: StatusTone }> = {
  Ready: { label: "Ready", tone: "ready" },
  ReadyWithWarnings: { label: "Ready with lint warnings", tone: "warning" },
  HashMismatch: { label: "Hash mismatch", tone: "blocked" },
  InvalidPlaybook: { label: "Invalid playbook", tone: "blocked" },
  LintRejected: { label: "Rejected by lint", tone: "blocked" },
  Invalid: { label: "Invalid package", tone: "blocked" }
};

export function packageStatus(status: PackageStatus) {
  return statusText[status] ?? { label: status, tone: "blocked" as StatusTone };
}

/** Endpoints can download only Ready packages (with or without lint warnings). */
export function isPublished(item: BatPackage) {
  return item.status === "Ready" || item.status === "ReadyWithWarnings";
}

function answer(choice: PlaybookChoice) {
  return choice.submit === "key"
    ? `press the key "${choice.send}"`
    : `type "${choice.send}" and press Enter`;
}

/** One plain-language sentence per playbook step. */
export function describeStep(step: PlaybookStep, index: number) {
  const choices = [step, ...(step.alternatives ?? [])]
    .map(choice => `when the screen shows "${choice.expect}", ${answer(choice)}`);
  const extras = [
    step.optional ? "skipped if this screen does not appear" : undefined,
    (step.maxRepeats ?? 1) > 1 ? `repeated up to ${step.maxRepeats} times` : undefined,
    `waits up to ${step.timeoutSeconds} s`
  ].filter(Boolean);
  return `${index + 1}. ${choices.join("; or ")} (${extras.join(", ")}).`;
}

/** How the endpoint starts the file: which program, which fixed arguments, and whether it gets input. */
export function describeRun(item: BatPackage) {
  const playbook = item.playbook;
  const program = item.fileType === "exe"
    ? `Runs the EXE directly${item.exeSubsystem ? ` (${item.exeSubsystem === "GUI" ? "windowed" : "console"} program)` : ""}`
    : "Runs the BAT through cmd.exe";
  const args = playbook?.arguments?.length
    ? ` with the fixed arguments ${playbook.arguments.map(argument => `"${argument}"`).join(" ")}`
    : " without arguments";
  const input = playbook && playbook.steps.length === 0
    ? "No input: nothing is typed, input is closed at start, and the run waits for the file to finish."
    : `Answers ${playbook?.steps.length ?? 0} screen${playbook?.steps.length === 1 ? "" : "s"} as listed below.`;
  return [`${program}${args}, hidden, as the endpoint service.`, input];
}

/** How the endpoint decides that a run succeeded. */
export function finishRules(playbook: Playbook, defaultGraceSeconds: number) {
  const codes = playbook.successExitCodes?.length ? playbook.successExitCodes : [0];
  return [
    `Exit code must be ${codes.join(" or ")}.`,
    playbook.restartExitCodes?.length
      ? `Exit code ${playbook.restartExitCodes.join(" or ")} means it worked and the PC needs a restart.`
      : undefined,
    playbook.timeoutSeconds
      ? `Stops as failed after ${playbook.timeoutSeconds} s in total.` : undefined,
    playbook.successMarker ? `Output must contain "${playbook.successMarker}".` : undefined,
    playbook.failureMarkers?.length
      ? `Stops as failed if the output shows ${playbook.failureMarkers.map(marker => `"${marker}"`).join(", ")}.`
      : undefined,
    `A window left open for ${playbook.guiDialogGraceSeconds ?? defaultGraceSeconds} s stops the run.`
  ].filter((rule): rule is string => Boolean(rule));
}

export function packageCompatibility(item: BatPackage) {
  if (item.office) {
    const office = item.office;
    return [
      `Office ${office.minOfficeVersion} to < ${office.maxOfficeVersionExclusive}`,
      office.officeProductReleaseIds?.length
        ? `products ${office.officeProductReleaseIds.join(", ")}` : "all products",
      office.minWindowsVersion || office.maxWindowsVersionExclusive
        ? `Windows ${office.minWindowsVersion ?? "any"} to < ${office.maxWindowsVersionExclusive ?? "latest"}`
        : undefined
    ].filter(Boolean).join(" · ");
  }
  if (item.windows) {
    const windows = item.windows;
    return [
      `Windows ${windows.minWindowsVersion} to < ${windows.maxWindowsVersionExclusive}`,
      windows.editionIds?.length ? `editions ${windows.editionIds.join(", ")}` : "all editions"
    ].join(" · ");
  }
  if (item.kind === "general") {
    const range = item.compatibility;
    return range?.minWindowsVersion || range?.maxWindowsVersionExclusive
      ? `Windows ${range.minWindowsVersion ?? "any"} to < ${range.maxWindowsVersionExclusive ?? "latest"}`
      : "Any Windows version";
  }
  return "No compatibility declared";
}
