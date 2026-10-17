/**
 * A deliberately small Markdown reader for the in-dashboard guide. It understands only what
 * docs/guide uses (front matter, headings, paragraphs, flat lists, tables, fenced code, quotes
 * and the inline forms `code`, **bold** and [links](url)) and produces plain data, so the page
 * never injects HTML.
 */

export type Inline =
  | { kind: "text"; text: string }
  | { kind: "code"; text: string }
  | { kind: "strong"; text: string }
  | { kind: "link"; text: string; href: string };

export type Block =
  | { kind: "heading"; level: 2 | 3; text: string; id: string }
  | { kind: "paragraph"; content: Inline[] }
  | { kind: "list"; ordered: boolean; items: Inline[][] }
  | { kind: "table"; header: Inline[][]; rows: Inline[][][] }
  | { kind: "code"; language: string; text: string }
  | { kind: "quote"; content: Inline[] };

export type GuideMeta = { title: string; summary: string; order: number };

export type ParsedMarkdown = { meta: Partial<GuideMeta>; title?: string; blocks: Block[] };

/** Splits `---` front matter (simple `key: value` lines) from the body. */
export function splitFrontMatter(source: string): { meta: Record<string, string>; body: string } {
  const text = source.replace(/^﻿/, "").replace(/\r\n?/g, "\n");
  const match = /^---\n([\s\S]*?)\n---\n?/.exec(text);
  if (!match) return { meta: {}, body: text };
  const meta: Record<string, string> = {};
  for (const line of match[1].split("\n")) {
    const separator = line.indexOf(":");
    if (separator > 0) meta[line.slice(0, separator).trim()] = line.slice(separator + 1).trim();
  }
  return { meta, body: text.slice(match[0].length) };
}

/** An anchor id that keeps Persian letters and drops punctuation. */
export function slugify(text: string): string {
  const slug = text.toLowerCase().trim()
    .replace(/[`*]/g, "")
    .replace(/[^\p{L}\p{N}\s-]/gu, "")
    .replace(/\s+/g, "-");
  return slug || "section";
}

/** Only relative paths, in-page anchors and https links are kept; anything else stays plain text. */
export function isSafeHref(href: string): boolean {
  return /^(\/(?!\/)|#|https:\/\/)/.test(href);
}

export function parseInline(source: string): Inline[] {
  const result: Inline[] = [];
  const pattern = /`([^`]+)`|\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)\s]+)\)/g;
  let last = 0;
  let match: RegExpExecArray | null;
  const pushText = (text: string) => { if (text) result.push({ kind: "text", text }); };
  while ((match = pattern.exec(source)) !== null) {
    pushText(source.slice(last, match.index));
    if (match[1] !== undefined) result.push({ kind: "code", text: match[1] });
    else if (match[2] !== undefined) result.push({ kind: "strong", text: match[2] });
    else if (isSafeHref(match[4])) result.push({ kind: "link", text: match[3], href: match[4] });
    else pushText(match[3]);
    last = match.index + match[0].length;
  }
  pushText(source.slice(last));
  return result;
}

/** Splits a table row on `|`, ignoring pipes inside backticks. */
function splitRow(line: string): string[] {
  const cells: string[] = [];
  let current = "";
  let inCode = false;
  for (const char of line.trim().replace(/^\|/, "").replace(/\|$/, "")) {
    if (char === "`") inCode = !inCode;
    if (char === "|" && !inCode) { cells.push(current.trim()); current = ""; }
    else current += char;
  }
  cells.push(current.trim());
  return cells;
}

const isTableSeparator = (line: string) => /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/.test(line);
const listMarker = /^(\s*)(?:([-*])|(\d+)\.)\s+(.*)$/;

export function parseMarkdown(source: string): ParsedMarkdown {
  const { meta, body } = splitFrontMatter(source);
  const lines = body.split("\n");
  const blocks: Block[] = [];
  let title: string | undefined;
  let index = 0;

  while (index < lines.length) {
    const line = lines[index];
    if (line.trim() === "") { index++; continue; }

    const fence = /^```\s*([\w+-]*)\s*$/.exec(line);
    if (fence) {
      const code: string[] = [];
      index++;
      while (index < lines.length && !/^```\s*$/.test(lines[index])) code.push(lines[index++]);
      index++;
      blocks.push({ kind: "code", language: fence[1], text: code.join("\n") });
      continue;
    }

    const heading = /^(#{1,3})\s+(.*?)\s*#*\s*$/.exec(line);
    if (heading) {
      const level = heading[1].length;
      const text = heading[2];
      if (level === 1) title ??= text;
      else blocks.push({ kind: "heading", level: level as 2 | 3, text, id: slugify(text) });
      index++;
      continue;
    }

    if (line.trimStart().startsWith("|") && index + 1 < lines.length && isTableSeparator(lines[index + 1])) {
      const header = splitRow(line).map(parseInline);
      index += 2;
      const rows: Inline[][][] = [];
      while (index < lines.length && lines[index].trimStart().startsWith("|"))
        rows.push(splitRow(lines[index++]).map(parseInline));
      blocks.push({ kind: "table", header, rows });
      continue;
    }

    if (line.startsWith(">")) {
      const quote: string[] = [];
      while (index < lines.length && lines[index].startsWith(">")) quote.push(lines[index++].replace(/^>\s?/, ""));
      blocks.push({ kind: "quote", content: parseInline(quote.join(" ")) });
      continue;
    }

    const first = listMarker.exec(line);
    if (first) {
      const ordered = first[3] !== undefined;
      const items: string[] = [];
      while (index < lines.length) {
        const item = listMarker.exec(lines[index]);
        if (item && (item[3] !== undefined) === ordered) { items.push(item[4]); index++; continue; }
        // A deeper-indented, non-blank line continues the previous item.
        if (items.length > 0 && /^\s{2,}\S/.test(lines[index]) && !item) { items[items.length - 1] += " " + lines[index].trim(); index++; continue; }
        break;
      }
      blocks.push({ kind: "list", ordered, items: items.map(parseInline) });
      continue;
    }

    const paragraph: string[] = [];
    while (index < lines.length && lines[index].trim() !== "" && !/^(```|#{1,3}\s|>)/.test(lines[index])
      && !listMarker.test(lines[index]) && !lines[index].trimStart().startsWith("|"))
      paragraph.push(lines[index++].trim());
    if (paragraph.length === 0) { index++; continue; }
    blocks.push({ kind: "paragraph", content: parseInline(paragraph.join(" ")) });
  }

  const order = Number(meta.order);
  return {
    meta: { title: meta.title, summary: meta.summary, order: Number.isFinite(order) ? order : undefined },
    title, blocks
  };
}
