import { describe, expect, it } from "vitest";
import { commandGroup } from "./commands";

describe("commandGroup", () => {
  it("places endpoint commands in their operator-facing submenus", () => {
    expect(commandGroup("endpoint.get_info")).toBe("Endpoint");
    expect(commandGroup("software.office_inventory")).toBe("Office");
    expect(commandGroup("system.windows_activation_status")).toBe("Windows");
    expect(commandGroup("system.windows_hello_sample_run")).toBe("Windows");
    expect(commandGroup("network.ibsng_diagnose")).toBe("IBSng");
  });
});
