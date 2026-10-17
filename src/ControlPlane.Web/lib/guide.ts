import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { parseMarkdown, type Block } from "./markdown";

export const GUIDE_LANGUAGES = ["fa", "en"] as const;
export type GuideLanguage = (typeof GUIDE_LANGUAGES)[number];

export type GuideText = { title: string; summary: string; blocks: Block[] };
export type GuideTopic = { slug: string; order: number } & Record<GuideLanguage, GuideText>;

/** The Markdown lives in docs/guide at the repository root, next to the other documentation. */
export function guideDirectory(): string {
  return process.env.GUIDE_DIR ?? path.resolve(process.cwd(), "..", "..", "docs", "guide");
}

function readText(directory: string, language: GuideLanguage, slug: string): GuideText & { order: number } {
  const file = path.join(directory, language, `${slug}.md`);
  const parsed = parseMarkdown(readFileSync(file, "utf8"));
  return {
    title: parsed.meta.title ?? parsed.title ?? slug,
    summary: parsed.meta.summary ?? "",
    blocks: parsed.blocks,
    order: parsed.meta.order ?? 100
  };
}

/** Every topic that exists in all languages, in the order set by its front matter. */
export function loadGuide(directory = guideDirectory()): GuideTopic[] {
  const slugs = readdirSync(path.join(directory, "fa"))
    .filter(name => name.endsWith(".md")).map(name => name.slice(0, -3))
    .filter(slug => GUIDE_LANGUAGES.every(language => {
      try { readFileSync(path.join(directory, language, `${slug}.md`)); return true; } catch { return false; }
    }));
  return slugs.map(slug => {
    const fa = readText(directory, "fa", slug);
    const en = readText(directory, "en", slug);
    return { slug, order: fa.order, fa, en };
  }).sort((a, b) => a.order - b.order || a.slug.localeCompare(b.slug));
}
