import { describe, expect, it } from "vitest";
import { readdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import { PAGES, pageName } from "./pages";

describe("page names and intros", () => {
  it("covers every dashboard route", () => {
    const app = join(__dirname, "..", "app");
    const routes = readdirSync(app, { withFileTypes: true })
      .filter(entry => entry.isDirectory() && existsSync(join(app, entry.name, "page.tsx")))
      .map(entry => `/${entry.name}/`);
    for (const route of ["/", ...routes]) expect(PAGES, route).toHaveProperty([route]);
  });

  it("gives every page a unique name, a purpose, who uses it and something to do", () => {
    const names = Object.values(PAGES).map(page => page.name);
    expect(new Set(names).size).toBe(names.length);
    for (const page of Object.values(PAGES)) {
      expect(page.purpose.length).toBeGreaterThan(20);
      expect(page.who.length).toBeGreaterThan(3);
      expect(page.actions.length).toBeGreaterThan(0);
    }
  });

  it("uses the clearer names", () => {
    expect(pageName("/agents/")).toBe("Online PCs");
    expect(pageName("/devices/")).toBe("PC inventory");
    expect(pageName("/operators/")).toBe("Dashboard users");
  });

  it("has Persian text for the pages with a Persian view", () => {
    expect(PAGES["/capabilities/"].fa?.name).toBeTruthy();
    expect(PAGES["/docs/"].fa?.actions.length).toBeGreaterThan(0);
  });
});
