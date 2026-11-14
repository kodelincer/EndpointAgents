/**
 * One place for every dashboard page's name and its short "about this page" text.
 * The sidebar, the top bar and each page heading read the name from here, so a rename is one edit.
 * Routes (the keys) never change: links, bookmarks and docs keep working.
 */
export type PageInfo = {
  /** Short name shown in the sidebar, the top bar and the page heading. */
  name: string;
  /** One sentence: what the page is for. */
  purpose: string;
  /** Who normally works on this page. */
  who: string;
  /** The main things you can do here, one short line each. */
  actions: string[];
  /** Persian text, only for pages that have a Persian view (Capabilities (agent actions), User manual). */
  fa?: Omit<PageInfo, "fa">;
};

export const PAGES = {
  "/": {
    name: "Overview",
    purpose: "A quick picture of how the PCs, the support agent and the helpdesk are doing over the last days.",
    who: "Everyone. Admins can also switch maintenance mode on or off.",
    actions: ["Pick 7, 14, 30 or 90 days", "Click any tile or card to open its page", "Check that the service is running and live updates are connected"]
  },
  "/agents/": {
    name: "Online Endpoints",
    purpose: "The PCs that are connected to the control plane right now, with what they are doing.",
    who: "Everyone reads. Admins can send commands.",
    actions: ["Search by PC name, host or user", "Open a PC to see its details and recent activity", "Send a catalogued command (the user still approves anything that changes their PC)"]
  },
  "/devices/": {
    name: "Endpoint (PC) inventory",
    purpose: "Saved facts about every PC (Windows, hardware, disks, programs, drivers), even when it is offline, and how old each fact is.",
    who: "Everyone. Helpful before you answer a chat about a PC.",
    actions: ["Find a PC by name", "See when each fact was last read", "Use Read now to ask one PC again"]
  },
  "/health/": {
    name: "Automatic checks",
    purpose: "Daily or weekly checks that run on every online PC by themselves and fix common problems before users call. No AI is used.",
    who: "Everyone reads. Admins create and change checks.",
    actions: ["Add a check by choosing from fixed lists", "See which PCs passed, failed or were fixed", "A new check only reports during its first week"]
  },
  "/problems/": {
    name: "Agent errors & problems",
    purpose: "Faults in our own agent software on the PCs (not the users' problems), grouped by kind, plus PCs that went quiet or stayed on an old version.",
    who: "Reviewers and admins. Admins can also collect logs from a PC.",
    actions: ["See how often each error happens and on which PCs", "Mark an error fixed in a version (it reopens if it comes back)", "Use Get logs to collect logs from one PC"]
  },
  "/support/": {
    name: "HelpDesk (user chats)",
    purpose: "Every conversation between users and the support agent, live, including the ones the agent passed to a person.",
    who: "Helpdesk members and above.",
    actions: ["Answer chats waiting for the helpdesk first", "Take over a chat from the agent", "Read the messages and the checks the agent ran"]
  },
  "/approvals/": {
    name: "Approvals",
    purpose: "Steps the support agent wants to run that need a person to say yes first, such as removing software.",
    who: "Helpdesk members and above.",
    actions: ["Approve and run a step on the user's PC", "Decline it with a note the user will see in their chat"]
  },
  "/feature-requests/": {
    name: "Feature requests",
    purpose: "Things users asked for that the support agent cannot do yet. Each was saved only after the user agreed.",
    who: "Reviewers and admins.",
    actions: ["Read what users asked for", "Mark a request New, Planned, Done or Declined"]
  },
  "/lessons/": {
    name: "Lessons from chats",
    purpose: "Every solved chat leaves a short note: what the user said, what was tried and what fixed it. Written without AI.",
    who: "Everyone reads. Reviewers decide what to do with each lesson.",
    actions: ["See which fixes solved the most chats", "Start an agent skill draft from a lesson", "Keep a lesson for later or drop it"]
  },
  "/skills/": {
    name: "Agent Skills",
    purpose: "Step-by-step instructions that teach the support agent how to handle a kind of problem, using only existing agent actions.",
    who: "Helpdesk members write drafts. Reviewers publish them.",
    actions: ["Write and check a draft", "Submit it for review, publish it or retire it", "Roll back to an earlier version"]
  },
  "/capabilities/": {
    name: "Capabilities (agent actions)",
    purpose: "The full list of things the support agent is allowed to do on a user's PC, built from the live catalogue.",
    who: "Everyone reads. Admins switch actions on or off. Reviewers can undo a recent fix.",
    actions: ["See every action, its risk level and its limits", "Read how to write a skill", "Check recent runs and undo one if needed"],
    fa: {
      name: "Capabilities (کارهای عامل)",
      purpose: "فهرست کامل کارهایی که عامل پشتیبانی اجازه دارد روی کامپیوتر کاربر انجام دهد؛ از فهرست زنده ساخته می‌شود.",
      who: "همه می‌خوانند. مدیر کارها را روشن یا خاموش می‌کند. کارشناس ارشد می‌تواند یک تعمیر اخیر را برگرداند.",
      actions: ["دیدن هر کار، سطح خطر و سقف آن", "خواندن روش نوشتن مهارت", "دیدن اجراهای اخیر و برگرداندن در صورت نیاز"]
    }
  },
  "/environment/": {
    name: "Company resources",
    purpose: "The company's servers, web apps, printers, shared folders and VPNs the agent may test, plus approved software, drivers, company standards and known outages.",
    who: "Everyone can report an outage. Admins edit everything else.",
    actions: ["Add or edit servers, printers, shares and VPNs", "Set company standards such as proxy and time zone", "Report an outage so the agent tells users about it"]
  },
  "/files/": {
    name: "Packages and files",
    purpose: "Reviewed repair and install packages the agent can run on PCs, and the approved files PCs can download.",
    who: "Everyone reads. Admins decide which install packages need no approval.",
    actions: ["Check each package is Ready, and see exactly what to fix when it is not", "See which symptoms each package fixes", "Let an install package run without a helpdesk approval"]
  },
  "/settings/": {
    name: "Organisation settings",
    purpose: "Company-wide settings that some agent actions use: the KMS activation server, the time servers, the AI request queue and the log server.",
    who: "Admins.",
    actions: ["Set and test the KMS server", "Set the time (NTP) servers", "Connect a log server such as Splunk, Graylog or Syslog"]
  },
  "/operators/": {
    name: "Dashboard users",
    purpose: "The people who can sign in to this dashboard and what their role lets them do.",
    who: "Admins.",
    actions: ["Create a local account or add a domain user", "Change a role: Member, Reviewer or Admin", "Disable an account or reset a password"]
  },
  "/agent-updates/": {
    name: "Agent updates",
    purpose: "Choose which version of the endpoint agent the PCs run. PCs update themselves when nobody is chatting, and go back by themselves if a new version fails. No AI is involved.",
    who: "Admins.",
    actions: ["Build and sign a version with scripts\\new-agent-update.ps1 -Sign", "Try it on a few pilot PCs", "Set it for everyone when it works", "See which PCs updated, failed or went back"]
  },
  "/docs/": {
    name: "User manual",
    purpose: "How the whole system works and how to use each dashboard page, in Persian and English.",
    who: "Everyone.",
    actions: ["Read a topic", "Switch between Persian and English"],
    fa: {
      name: "راهنمای کاربر",
      purpose: "سیستم چطور کار می‌کند و هر صفحهٔ داشبورد را چطور به کار ببریم، به فارسی و انگلیسی.",
      who: "همه.",
      actions: ["خواندن یک مبحث", "جابه‌جایی بین فارسی و انگلیسی"]
    }
  }
} satisfies Record<string, PageInfo>;

export type PageRoute = keyof typeof PAGES;

/** The page name for a route, e.g. pageName("/agents/") is "Online Endpoints". */
export const pageName = (route: PageRoute) => PAGES[route].name;
