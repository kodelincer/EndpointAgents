import { describe, expect, it } from "vitest";
import { filterActions, groupByArea, issueCounts, parseNumberList, parseTextList, pendingSummary, successRate, suggestId,
  type ActionInfo, type ApprovalRequest } from "./capabilities";

const action = (id: string, area: string, tier: ActionInfo["tier"], name = id): ActionInfo => ({
  id, area, tier, runAs: "System", name, nameFa: "نام", description: `does ${name}`, returns: "",
  timeoutSeconds: 30, defaultMaxPerHour: 3, maxPerHour: 3, enabled: true, parameters: []
});

const actions = [action("dns.flush", "network", "SafeFix", "Clear the DNS cache"),
  action("printer.list", "printers", "Look"), action("reboot.schedule", "desktop", "Disruptive")];

describe("capabilities", () => {
  it("filters by text, area and tier", () => {
    expect(filterActions(actions, "dns", "all", "all").map(item => item.id)).toEqual(["dns.flush"]);
    expect(filterActions(actions, "", "printers", "all").map(item => item.id)).toEqual(["printer.list"]);
    expect(filterActions(actions, "", "all", "Disruptive").map(item => item.id)).toEqual(["reboot.schedule"]);
    expect(filterActions(actions, "نام", "all", "all")).toHaveLength(3);
  });

  it("groups in area order and drops empty areas", () => {
    expect(groupByArea(actions, ["desktop", "domain", "network"]).map(group => group.area))
      .toEqual(["desktop", "network"]);
  });

  it("computes success rate", () => {
    expect(successRate(null)).toBeNull();
    expect(successRate({ actionId: "a", runs: 4, succeeded: 3, failed: 1, blocked: 0 })).toBe(75);
  });

  it("parses lists and suggests ids", () => {
    expect(parseNumberList("443, 8443 x 0")).toEqual([443, 8443]);
    expect(parseTextList(" a, b ,,")).toEqual(["a", "b"]);
    expect(suggestId("HR Portal (new)")).toBe("hr-portal-new");
  });

  it("counts lint issues", () => {
    expect(issueCounts([{ severity: "error", message: "x" }, { severity: "warning", message: "y" },
      { severity: "warning", message: "z" }])).toEqual({ errors: 1, warnings: 2 });
  });

  it("summarises pending approvals", () => {
    const request = (status: ApprovalRequest["status"], createdAtUtc: string): ApprovalRequest => ({
      approvalId: createdAtUtc, kind: "action", itemId: "software.uninstall", title: "Remove software: AnyDesk",
      agentId: "a", argumentsJson: "{}", reason: "", status, createdAtUtc });
    const now = Date.parse("2026-10-20T10:00:00Z");
    expect(pendingSummary([request("Pending", "2026-10-20T09:30:00Z"), request("Pending", "2026-10-20T09:50:00Z"),
      request("Done", "2026-10-20T08:00:00Z")], now)).toEqual({ count: 2, oldestMinutes: 30 });
    expect(pendingSummary([], now)).toEqual({ count: 0, oldestMinutes: null });
  });
});

describe("kms settings input", () => {
  it("accepts a host name or IPv4 address and an optional port", async () => {
    const { kmsInputError } = await import("./capabilities");
    expect(kmsInputError("172.16.0.2", "")).toBeUndefined();
    expect(kmsInputError("kms.corp.local", "1688")).toBeUndefined();
    expect(kmsInputError("", "")).toBeUndefined();
  });
  it("rejects ports in the host, commands and bad ports", async () => {
    const { kmsInputError } = await import("./capabilities");
    expect(kmsInputError("172.16.0.2:1688", "")).toMatch(/host name/);
    expect(kmsInputError("kms & calc", "")).toMatch(/host name/);
    expect(kmsInputError("300.1.1.1", "")).toMatch(/host name/);
    expect(kmsInputError("kms.corp.local", "70000")).toMatch(/port/);
    expect(kmsInputError("", "1688")).toMatch(/before its port/);
  });
});
