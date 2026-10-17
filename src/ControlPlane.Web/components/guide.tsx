"use client";

import { ArrowLeft, ArrowRight, BookMarked, Languages } from "lucide-react";
import Link from "next/link";
import { Fragment, useEffect, useState } from "react";
import type { Block, Inline } from "@/lib/markdown";

export type GuideLanguage = "fa" | "en";
export type GuideNavItem = { slug: string; fa: { title: string; summary: string }; en: { title: string; summary: string } };
export type GuideArticleData = { slug: string; fa: { title: string; summary: string; blocks: Block[] };
  en: { title: string; summary: string; blocks: Block[] } };

const storageKey = "guide-language";

/** Persian first; the reader's last choice is remembered in this browser only. */
function useGuideLanguage(): [GuideLanguage, (language: GuideLanguage) => void] {
  const [language, setLanguage] = useState<GuideLanguage>("fa");
  useEffect(() => {
    const id = window.setTimeout(() => {
      try { if (window.localStorage.getItem(storageKey) === "en") setLanguage("en"); } catch { /* storage blocked */ }
    }, 0);
    return () => window.clearTimeout(id);
  }, []);
  const choose = (next: GuideLanguage) => {
    setLanguage(next);
    try { window.localStorage.setItem(storageKey, next); } catch { /* storage blocked */ }
  };
  return [language, choose];
}

const text = {
  fa: { heading: "راهنما", subtitle: "سیستم چطور کار می‌کند و چطور با آن کار کنیم.", topics: "مبحث‌ها", onThisPage: "در این صفحه",
    previous: "قبلی", next: "بعدی", switchTo: "English", all: "همهٔ مبحث‌ها", read: "خواندن" },
  en: { heading: "Documentation", subtitle: "How the system works and how to work with it.", topics: "Topics", onThisPage: "On this page",
    previous: "Previous", next: "Next", switchTo: "فارسی", all: "All topics", read: "Read" }
} as const;

function Inlines({ content }: { content: Inline[] }) {
  return <>{content.map((part, index) => {
    switch (part.kind) {
      case "code": return <code key={index} dir="ltr">{part.text}</code>;
      case "strong": return <strong key={index}>{part.text}</strong>;
      case "link": return part.href.startsWith("/")
        ? <Link key={index} href={part.href}>{part.text}</Link>
        : <a key={index} href={part.href} {...(part.href.startsWith("#") ? {} : { target: "_blank", rel: "noopener noreferrer" })}>{part.text}</a>;
      default: return <Fragment key={index}>{part.text}</Fragment>;
    }
  })}</>;
}

/** Renders parsed Markdown blocks as React elements (nothing is ever injected as HTML). */
export function Blocks({ blocks }: { blocks: Block[] }) {
  return <>{blocks.map((block, index) => {
    switch (block.kind) {
      case "heading":
        return block.level === 2 ? <h2 key={index} id={block.id}>{block.text}</h2> : <h3 key={index} id={block.id}>{block.text}</h3>;
      case "paragraph": return <p key={index}><Inlines content={block.content} /></p>;
      case "quote": return <blockquote key={index}><Inlines content={block.content} /></blockquote>;
      case "code": return <pre key={index} dir="ltr" tabIndex={0}><code>{block.text}</code></pre>;
      case "list": {
        const items = block.items.map((item, itemIndex) => <li key={itemIndex}><Inlines content={item} /></li>);
        return block.ordered ? <ol key={index}>{items}</ol> : <ul key={index}>{items}</ul>;
      }
      case "table":
        return <div key={index} className="guide-table"><table>
          <thead><tr>{block.header.map((cell, cellIndex) => <th key={cellIndex}><Inlines content={cell} /></th>)}</tr></thead>
          <tbody>{block.rows.map((row, rowIndex) => <tr key={rowIndex}>
            {row.map((cell, cellIndex) => <td key={cellIndex}><Inlines content={cell} /></td>)}</tr>)}</tbody>
        </table></div>;
    }
  })}</>;
}

/** The guide: a topic list on one side, the article (or an index) on the other. */
export function GuideView({ nav, article }: { nav: GuideNavItem[]; article?: GuideArticleData }) {
  const [language, setLanguage] = useGuideLanguage();
  const fa = language === "fa";
  const t = text[language];
  const index = article ? nav.findIndex(item => item.slug === article.slug) : -1;
  const previous = index > 0 ? nav[index - 1] : undefined;
  const following = index >= 0 && index < nav.length - 1 ? nav[index + 1] : undefined;
  const content = article?.[language];
  const sections = content?.blocks.filter(block => block.kind === "heading" && block.level === 2) ?? [];
  const Back = fa ? ArrowRight : ArrowLeft;
  const Forward = fa ? ArrowLeft : ArrowRight;

  return <div className="file-page guide-page" dir={fa ? "rtl" : "ltr"} lang={language}>
    <section className="file-heading" aria-labelledby="guide-title">
      <div><div className="file-heading-mark"><BookMarked size={17} /></div><div>
        <h1 id="guide-title">{t.heading}</h1><p>{t.subtitle}</p></div></div>
      <button className="secondary-button" onClick={() => setLanguage(fa ? "en" : "fa")}>
        <Languages size={15} />{t.switchTo}</button>
    </section>

    <div className="guide-layout">
      <nav className="guide-nav" aria-label={t.topics}>
        <Link className={!article ? "active" : ""} href="/docs/">{t.all}</Link>
        {nav.map(item => <Fragment key={item.slug}>
          <Link className={article?.slug === item.slug ? "active" : ""} href={`/docs/${item.slug}/`}
            aria-current={article?.slug === item.slug ? "page" : undefined}>{item[language].title}</Link>
          {article?.slug === item.slug && sections.length > 0 && <ul className="guide-sections" aria-label={t.onThisPage}>
            {sections.map(section => section.kind === "heading" &&
              <li key={section.id}><a href={`#${section.id}`}>{section.text}</a></li>)}</ul>}
        </Fragment>)}
      </nav>

      {article && content ? <article className="guide-article">
        <header><h2 className="guide-title">{content.title}</h2><p>{content.summary}</p></header>
        <Blocks blocks={content.blocks} />
        <footer className="guide-pager">
          {previous ? <Link href={`/docs/${previous.slug}/`}><Back size={15} /><span><small>{t.previous}</small>{previous[language].title}</span></Link> : <span />}
          {following ? <Link href={`/docs/${following.slug}/`}><span><small>{t.next}</small>{following[language].title}</span><Forward size={15} /></Link> : <span />}
        </footer>
      </article> : <div className="guide-index">
        {nav.map(item => <Link key={item.slug} className="guide-card" href={`/docs/${item.slug}/`}>
          <strong>{item[language].title}</strong><span>{item[language].summary}</span><small>{t.read}</small></Link>)}
      </div>}
    </div>
  </div>;
}
