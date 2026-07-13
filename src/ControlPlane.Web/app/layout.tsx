import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import { AppShell } from "@/components/app-shell";
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
    <ControlPlaneProvider><AppShell>{children}</AppShell></ControlPlaneProvider>
  </body></html>;
}
