import { describe, expect, it } from "vitest";
import { defaultsFor, formatBytes, groupProblems, isUnencrypted, kindClass, kindLabel, pcFacts, problemQuery, protocolsFor, type ProblemRow } from "./problems";

const row = (fingerprint: string, extra: Partial<ProblemRow> = {}): ProblemRow => ({
  fingerprint, kind: "Crash", source: "Service", title: "boom", status: "Open",
  firstSeenUtc: "2026-11-04T08:00:00Z", lastSeenUtc: "2026-11-04T08:00:00Z", count: 1, pcs: 1,
  versions: ["0.3.0"], windows: [{ label: "Windows 10 22H2", pcs: 1 }], models: [{ label: "Dell OptiPlex 7080", pcs: 1 }],
  onlyOn: [], ...extra
});

describe("problemQuery", () => {
  it("leaves out empty filters and encodes the rest", () => {
    expect(problemQuery({})).toBe("");
    expect(problemQuery({ status: "", kind: "Crash", os: "Windows 10 22H2" })).toBe("?kind=Crash&os=Windows+10+22H2");
  });
});

describe("groupProblems", () => {
  it("keeps one section when not grouping, and none for an empty list", () => {
    expect(groupProblems([row("a"), row("b")], "none")).toEqual([{ title: "", rows: [row("a"), row("b")] }]);
    expect(groupProblems([], "none")).toEqual([]);
  });

  it("shows a problem under each version it was seen on", () => {
    const groups = groupProblems([row("a", { versions: ["0.3.0", "0.4.0"] }), row("b")], "version");
    expect(groups.map(group => [group.title, group.rows.length])).toEqual([["0.3.0", 2], ["0.4.0", 1]]);
  });

  it("groups by Windows, model and kind, and says when a value is not known", () => {
    expect(groupProblems([row("a")], "windows")[0].title).toBe("Windows 10 22H2");
    expect(groupProblems([row("a")], "model")[0].title).toBe("Dell OptiPlex 7080");
    expect(groupProblems([row("a", { kind: "InstallFailed" })], "kind")[0].title).toBe("Install failed");
    expect(groupProblems([row("a", { models: [] })], "model")[0].title).toBe("Not known");
  });
});

describe("log server helpers", () => {
  it("offers only the protocols of the type", () => {
    expect(protocolsFor("Splunk")).toEqual(["https", "http"]);
    expect(protocolsFor("Syslog")).toEqual(["udp", "tcp", "tls"]);
  });

  it("starts each type on its usual port", () => {
    expect(defaultsFor("Splunk")).toEqual({ port: 8088, protocol: "https" });
    expect(defaultsFor("Graylog")).toEqual({ port: 12201, protocol: "http" });
    expect(defaultsFor("Syslog")).toEqual({ port: 6514, protocol: "tls" });
  });

  it("knows which protocols cross the network unencrypted", () => {
    expect(["http", "udp", "tcp"].every(protocol => isUnencrypted({ protocol }))).toBe(true);
    expect(["https", "tls"].some(protocol => isUnencrypted({ protocol }))).toBe(false);
  });
});

describe("labels", () => {
  it("names every kind and falls back to the raw kind", () => {
    expect(kindLabel("UserReported")).toBe("User: did not fix it");
    expect(kindLabel("Something")).toBe("Something");
    expect(kindClass("Crash")).toBe("escalated");
    expect(kindClass("LoggedError")).toBe("open");
  });

  it("formats sizes and PC facts", () => {
    expect(formatBytes(500)).toBe("500 B");
    expect(formatBytes(2048)).toBe("2 KB");
    expect(formatBytes(5 * 1024 * 1024)).toBe("5.0 MB");
    expect(pcFacts({ agentId: "a", machineName: "PC", online: true,
      facts: { osName: "Windows 11", osVersion: "24H2", manufacturer: "HP", model: "ProBook" } })).toBe("Windows 11 24H2 · HP ProBook");
    expect(pcFacts({ agentId: "a", machineName: "PC", online: true })).toBe("");
  });
});
