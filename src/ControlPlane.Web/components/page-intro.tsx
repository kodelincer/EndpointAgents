import { PAGES, type PageInfo, type PageRoute } from "@/lib/pages";

const LABELS = {
  en: { about: "About", purpose: "What it's for", who: "Who uses it", actions: "What you can do" },
  fa: { about: "دربارهٔ", purpose: "این صفحه برای چیست", who: "چه کسی از آن استفاده می‌کند", actions: "اینجا چه کاری می‌توانید بکنید" }
};

/** The short "what is this page for, who uses it, what can I do" box under each page heading. */
export function PageIntro({ route, language = "en" }: { route: PageRoute; language?: "en" | "fa" }) {
  const info: PageInfo = PAGES[route];
  const fa = language === "fa" && info.fa;
  const page = fa ? info.fa! : info;
  const t = LABELS[fa ? "fa" : "en"];
  return <section className="page-intro" aria-label={`${t.about} ${page.name}`}>
    <div className="page-intro-item">
      <span className="page-intro-label">{t.purpose}</span>
      <p>{page.purpose}</p>
    </div>
    <div className="page-intro-item">
      <span className="page-intro-label">{t.who}</span>
      <p>{page.who}</p>
    </div>
    <div className="page-intro-item">
      <span className="page-intro-label">{t.actions}</span>
      <ul>{page.actions.map(action => <li key={action}>{action}</li>)}</ul>
    </div>
  </section>;
}

/** The page name in the chosen language (falls back to English). */
export function localName(route: PageRoute, language: "en" | "fa") {
  const info: PageInfo = PAGES[route];
  return language === "fa" && info.fa ? info.fa.name : info.name;
}
