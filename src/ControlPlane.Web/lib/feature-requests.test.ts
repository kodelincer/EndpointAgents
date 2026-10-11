import { describe, expect, it } from "vitest";
import { featureRequestCounts } from "./feature-requests";
import type { FeatureRequest } from "./types";

const request = (status: FeatureRequest["status"]): FeatureRequest => ({
  requestId: status, conversationId: "c", agentId: "a", userName: "u", machineName: "pc",
  summary: "Install a scanner driver", userWords: "scanner", status,
  createdAtUtc: "2026-10-11T00:00:00Z", updatedAtUtc: "2026-10-11T00:00:00Z"
});

describe("feature requests", () => {
  it("counts requests per status", () => {
    expect(featureRequestCounts([request("New"), request("New"), request("Done")]))
      .toEqual({ New: 2, Planned: 0, Done: 1, Declined: 0 });
  });
});
