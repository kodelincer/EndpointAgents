import { PAGES, type PageInfo, type PageRoute } from "@/lib/pages";

const LABELS = {
  en: { about: "About", who: "Who uses it:", actions: "Here you can" },
  fa: { about: "دربارهٔ", who: "چه کسی:", actions: "اینجا می‌توانید" }
};

/** Joins the action lines into one sentence: "a; b; c." with each line starting in lower case. */
export function actionSentence(actions: string[], language: "en" | "fa" = "en") {
  const lines = actions.map(line => language === "en" ? line.charAt(0).toLowerCase() + line.slice(1) : line);
  return language === "fa" ? `${lines.join("، ")}.` : `${lines.join("; ")}.`;
}

/** A short paragraph under each page heading: what the page is for, who uses it and what you can do. */
export function PageIntro({ route, language = "en" }: { route: PageRoute; language?: "en" | "fa" }) {
  const info: PageInfo = PAGES[route];
  const fa = language === "fa" && info.fa;
  const page = fa ? info.fa! : info;
  const lang = fa ? "fa" : "en";
  const t = LABELS[lang];
  return <p className="page-intro" aria-label={`${t.about} ${page.name}`}>
    {page.purpose} <strong>{t.who}</strong> {page.who} <strong>{t.actions}:</strong> {actionSentence(page.actions, lang)}
  </p>;
}

/** The page name in the chosen language (falls back to English). */
export function localName(route: PageRoute, language: "en" | "fa") {
  const info: PageInfo = PAGES[route];
  return language === "fa" && info.fa ? info.fa.name : info.name;
}
