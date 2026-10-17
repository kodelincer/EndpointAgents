import { GuideView } from "@/components/guide";
import { loadGuide } from "@/lib/guide";

export const dynamic = "force-static";

export default function DocsIndex() {
  const nav = loadGuide().map(({ slug, fa, en }) => ({
    slug, fa: { title: fa.title, summary: fa.summary }, en: { title: en.title, summary: en.summary }
  }));
  return <GuideView nav={nav} />;
}
