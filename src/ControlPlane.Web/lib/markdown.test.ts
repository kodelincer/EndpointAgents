import { describe, expect, it } from "vitest";
import { isSafeHref, parseInline, parseMarkdown, slugify, splitFrontMatter } from "./markdown";

describe("markdown reader", () => {
  it("reads front matter and the first heading", () => {
    const parsed = parseMarkdown("---\ntitle: سلام\nsummary: خلاصه\norder: 3\n---\n\n# عنوان\n\nمتن.");
    expect(parsed.meta).toEqual({ title: "سلام", summary: "خلاصه", order: 3 });
    expect(parsed.title).toBe("عنوان");
    expect(parsed.blocks).toEqual([{ kind: "paragraph", content: [{ kind: "text", text: "متن." }] }]);
  });

  it("works without front matter and with Windows line endings", () => {
    expect(splitFrontMatter("plain\r\ntext").body).toBe("plain\ntext");
    expect(parseMarkdown("## One\r\n\r\ntext\r\n").blocks.map(block => block.kind)).toEqual(["heading", "paragraph"]);
  });

  it("builds anchor ids that keep Persian letters", () => {
    expect(slugify("Roles and sign-in")).toBe("roles-and-sign-in");
    expect(slugify("نقش‌ها و ورود")).toContain("ورود");
    expect(slugify("!!!")).toBe("section");
  });

  it("parses inline code, bold and safe links only", () => {
    expect(parseInline("a `x|y` **b** [c](/docs/) [d](javascript:void) [e](https://example.com)")).toEqual([
      { kind: "text", text: "a " }, { kind: "code", text: "x|y" }, { kind: "text", text: " " },
      { kind: "strong", text: "b" }, { kind: "text", text: " " }, { kind: "link", text: "c", href: "/docs/" },
      { kind: "text", text: " " }, { kind: "text", text: "d" }, { kind: "text", text: " " },
      { kind: "link", text: "e", href: "https://example.com" }]);
    expect(isSafeHref("//evil.example")).toBe(false);
    expect(isSafeHref("http://plain.example")).toBe(false);
    expect(isSafeHref("#top")).toBe(true);
  });

  it("parses tables, keeping pipes inside code", () => {
    const [table] = parseMarkdown("| A | B |\n| --- | --- |\n| `a|b` | two |\n| three | four |").blocks;
    expect(table.kind).toBe("table");
    if (table.kind !== "table") return;
    expect(table.header).toHaveLength(2);
    expect(table.rows).toHaveLength(2);
    expect(table.rows[0][0]).toEqual([{ kind: "code", text: "a|b" }]);
  });

  it("parses lists, quotes and fenced code", () => {
    const blocks = parseMarkdown("1. one\n2. two\n   more\n\n- a\n- b\n\n> note\n> here\n\n```text\n# not a heading\n  indented\n```").blocks;
    expect(blocks[0]).toMatchObject({ kind: "list", ordered: true });
    expect(blocks[0].kind === "list" && blocks[0].items[1]).toEqual([{ kind: "text", text: "two more" }]);
    expect(blocks[1]).toMatchObject({ kind: "list", ordered: false });
    expect(blocks[2]).toEqual({ kind: "quote", content: [{ kind: "text", text: "note here" }] });
    expect(blocks[3]).toEqual({ kind: "code", language: "text", text: "# not a heading\n  indented" });
  });

  it("never turns raw HTML into markup", () => {
    const [paragraph] = parseMarkdown("<script>alert(1)</script> text").blocks;
    expect(paragraph).toEqual({ kind: "paragraph", content: [{ kind: "text", text: "<script>alert(1)</script> text" }] });
  });
});
