import { describe, expect, it } from "vitest";
import { outcomeLine, skillDraftHref } from "./lessons";

const step = (actionId: string, name: string) => ({ actionId, name, status: "Done", summary: "" });

describe("lessons", () => {
  it("says what solved the chat", () => {
    expect(outcomeLine({ fixedBy: "spooler.restart_and_clear",
      tried: [step("printer.list", "List printers"), step("spooler.restart_and_clear", "Restart the print spooler")] }))
      .toBe("Fixed by Restart the print spooler after 1 other step");
    expect(outcomeLine({ fixedBy: null, tried: [] })).toBe("Solved in the chat, nothing ran on the PC");
    expect(outcomeLine({ fixedBy: null, tried: [step("printer.list", "List printers")] })).toBe("Solved without a fix after 1 check");
  });

  it("opens a reviewed skill draft from the chat", () =>
    expect(skillDraftHref("c 1")).toBe("/skills/?conversation=c%201"));
});
