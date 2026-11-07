import { describe, expect, it } from "vitest";
import { compact, dayTotal, niceTicks, percent, shortDay } from "./overview";

describe("overview helpers", () => {
  it("makes clean axis ticks that reach the data", () => {
    expect(niceTicks(0)).toEqual([0, 1]);
    expect(niceTicks(7)).toEqual([0, 2, 4, 6, 8]);
    expect(niceTicks(40)).toEqual([0, 10, 20, 30, 40]);
    expect(niceTicks(3).at(-1)).toBeGreaterThanOrEqual(3);
  });
  it("gives no percent when there is nothing to divide", () => {
    expect(percent(1, 0)).toBeNull();
    expect(percent(2, 3)).toBe(67);
  });
  it("adds up only the named series of a day", () => {
    expect(dayTotal({ date: "2026-11-04", values: { worked: 3, failed: 1, other: 9 } }, ["worked", "failed"])).toBe(4);
  });
  it("keeps the day of a date key and shortens big numbers", () => {
    expect(shortDay("2026-11-04")).toBe("4 Nov");
    expect(compact(1284)).toBe("1,284");
    expect(compact(12_900)).toBe("12.9K");
  });
});
