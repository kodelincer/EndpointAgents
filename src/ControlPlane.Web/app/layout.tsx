import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
// Vazirmatn (free, OFL) is bundled from npm, so Persian text never depends on reaching Google.
import "@fontsource-variable/vazirmatn";
import { AppShell } from "@/components/app-shell";
import { AuthGate } from "@/components/auth-gate";
import { ControlPlaneProvider } from "@/lib/control-plane-context";
import "./globals.css";

const geist = Geist({ variable: "--font-geist", subsets: ["latin"] });
const mono = Geist_Mono({ variable: "--font-mono", subsets: ["latin"] });

export const metadata: Metadata = {
  title: "EndpointAgents Control Plane",
  description: "Live endpoint connection and RPC operations dashboard"
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="en"><body className={`${geist.variable} ${mono.variable}`}>
    <AuthGate><ControlPlaneProvider><AppShell>{children}</AppShell></ControlPlaneProvider></AuthGate>
  </body></html>;
}
