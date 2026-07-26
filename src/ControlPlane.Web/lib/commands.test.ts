import { describe, expect, it } from "vitest";
import { commandGroup } from "./commands";

describe("commandGroup", () => {
  it("places endpoint commands in their operator-facing submenus", () => {
    expect(commandGroup("endpoint.get_info")).toBe("Endpoint");
    expect(commandGroup("software.office_inventory")).toBe("Office");
    expect(commandGroup("network.ibsng_diagnose")).toBe("IBSng");
  });
});
