import { describe, expect, it } from "vitest";
import { isRtl, isTerminalStage, parseYesNo, progressLabel } from "./format";
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
  });
});
