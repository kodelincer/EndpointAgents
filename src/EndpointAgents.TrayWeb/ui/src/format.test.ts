import { describe, expect, it } from "vitest";
import { isRtl, isTerminalStage, parseYesNo, progressLabel, queueLabel } from "./format";
import type { ProgressEvent } from "./types";

const event = (stage: ProgressEvent["stage"], extra: Partial<ProgressEvent> = {}): ProgressEvent => ({
  eventId: "e", conversationId: "c", runId: "r", sequence: 1, stage, activity: "None", outcome: "None",
  createdAtUtc: new Date().toISOString(), ...extra,
});

describe("parseYesNo", () => {
  it("reads the same answers as the service", () => {
    expect(parseYesNo("بله")).toBe(true);
    expect(parseYesNo(" Yes! ")).toBe(true);
    expect(parseYesNo("انجام بده.")).toBe(true);
    expect(parseYesNo("نه")).toBe(false);
    expect(parseYesNo("خیر؟")).toBe(false);
  });

  it("normalizes Arabic letters and zero-width joiners", () => {
    expect(parseYesNo("تايید")).toBe(true);
    expect(parseYesNo("انجام‌نده")).toBe(false);
  });

  it("refuses anything else", () => {
    expect(parseYesNo("شاید")).toBeNull();
    expect(parseYesNo("yes but later")).toBeNull();
  });
});

describe("isRtl", () => {
  it("uses the first strong character", () => {
    expect(isRtl("Office فعال نیست")).toBe(false);
    expect(isRtl("۱۲ آفیس")).toBe(true);
    expect(isRtl("سلام")).toBe(true);
  });
});

describe("progress", () => {
  it("names repair stations by activity and outcome", () => {
    expect(progressLabel(event("ActionCompleted", { activity: "OfficeActivationRepair", outcome: "Failure" })))
      .toBe("تعمیر آفیس ناموفق بود");
    expect(progressLabel(event("EndpointCheckStarted", { activity: "WindowsActivationStatus" })))
      .toBe("بررسی فعال‌سازی ویندوز");
  });

  it("ends a run only on a terminal stage", () => {
    expect(isTerminalStage(event("AwaitingApproval"))).toBe(false);
    expect(isTerminalStage(event("ActionRejected"))).toBe(true);
    expect(isTerminalStage(event("Cancelled"))).toBe(true);
    expect(progressLabel(event("Cancelled"))).toBe("درخواست متوقف شد");
  });
});

import { faceFor } from "./RobotFace";
import type { TrayStatus } from "./types";

const status = (health: TrayStatus["health"], service: TrayStatus["service"], runtime = true): TrayStatus => ({
  health, service, label: "", detail: "",
  runtime: runtime ? ({ connectionStatus: "Connected" } as TrayStatus["runtime"]) : undefined,
});

describe("faceFor", () => {
  it("is happy only when ready", () => {
    expect(faceFor(status("Ready", "Running"))).toBe("happy");
  });
  it("is sad when the service is up but the server is not", () => {
    expect(faceFor(status("Warning", "Running"))).toBe("sad");
    expect(faceFor(status("Error", "Running"))).toBe("sad");
  });
  it("is angry when the background service is unavailable", () => {
    expect(faceFor(status("Stopped", "Stopped", false))).toBe("angry");
    expect(faceFor(status("Error", "NotInstalled", false))).toBe("angry");
    expect(faceFor(status("Error", "Running", false))).toBe("angry");
  });
  it("sleeps when paused and waits while checking", () => {
    expect(faceFor(status("Paused", "Running"))).toBe("sleepy");
    expect(faceFor(status("Checking", "StartPending", false))).toBe("neutral");
  });
});
describe("queueLabel", () => {
  it("names the place in line only while waiting", () => {
    expect(queueLabel(null)).toBe("");
    expect(queueLabel(0)).toBe("");
    expect(queueLabel(1)).toBe("نوبت بعدی با شماست");
    expect(queueLabel(3)).toBe("۲ نفر جلوتر از شما در صف هستند");
  });
});
