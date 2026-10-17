import { notFound } from "next/navigation";
import { GuideView } from "@/components/guide";
import { loadGuide } from "@/lib/guide";

export const dynamic = "force-static";
export const dynamicParams = false;

export function generateStaticParams() {
  return loadGuide().map(topic => ({ slug: topic.slug }));
}

export default async function DocsTopic({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;
  const topics = loadGuide();
  const topic = topics.find(item => item.slug === slug);
  if (!topic) notFound();
  const nav = topics.map(({ slug: id, fa, en }) => ({
    slug: id, fa: { title: fa.title, summary: fa.summary }, en: { title: en.title, summary: en.summary }
  }));
  return <GuideView nav={nav} article={{ slug: topic.slug, fa: topic.fa, en: topic.en }} />;
}
