import { readdirSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { guideDirectory, loadGuide } from "./guide";
import type { Block, Inline } from "./markdown";

const inlines = (block: Block): Inline[] => {
  switch (block.kind) {
    case "paragraph": case "quote": return block.content;
    case "list": return block.items.flat();
    case "table": return [...block.header.flat(), ...block.rows.flat(2)];
    default: return [];
  }
};

describe("the guide in docs/guide", () => {
  const topics = loadGuide();

  it("has the same topics in Persian and English", () => {
    const names = (language: string) => readdirSync(path.join(guideDirectory(), language)).sort();
    expect(names("fa")).toEqual(names("en"));
    expect(topics.map(topic => topic.slug)).toEqual(["overview", "agent", "helpdesk", "actions", "packages", "security"]);
  });

  it("gives every topic a title, a summary and sections in both languages", () => {
    for (const topic of topics) for (const language of ["fa", "en"] as const) {
      const text = topic[language];
      expect(text.title, `${topic.slug}/${language} title`).not.toBe("");
      expect(text.title, `${topic.slug}/${language} title`).not.toBe(topic.slug);
      expect(text.summary, `${topic.slug}/${language} summary`).not.toBe("");
      expect(text.blocks.filter(block => block.kind === "heading").length, `${topic.slug}/${language} sections`).toBeGreaterThan(1);
    }
  });

  it("covers the security section's key subjects", () => {
    const security = topics.find(topic => topic.slug === "security")!;
    const headings = (language: "fa" | "en") => security[language].blocks
      .flatMap(block => block.kind === "heading" ? [block.text] : []);
    expect(headings("en")).toEqual(expect.arrayContaining([
      "Signing and hash pinning", "Roles and sign-in", "Masking data sent to the cloud LLM", "Accepted risks",
      "What to harden in production"]));
    expect(headings("fa")).toHaveLength(headings("en").length);
  });

  it("links only to topics that exist", () => {
    const slugs = new Set(topics.map(topic => topic.slug));
    for (const topic of topics) for (const language of ["fa", "en"] as const)
      for (const part of topic[language].blocks.flatMap(inlines))
        if (part.kind === "link" && part.href.startsWith("/docs/"))
          expect(slugs.has(part.href.split("/")[2]), `${topic.slug}/${language} -> ${part.href}`).toBe(true);
  });
});
