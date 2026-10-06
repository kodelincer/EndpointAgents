import { FileRepository } from "@/components/file-repository";
import { PackageCatalog } from "@/components/package-catalog";

export default function FilesPage() {
  return <>
    <PackageCatalog />
    <FileRepository />
  </>;
}
