# EndpointAgents Control Plane Design

## Theme

Light-first operations console. Neutral surfaces carry dense data; blue is reserved for selection and action, while green, amber, and red communicate operational state.

## Color

Use OKLCH tokens: canvas `oklch(0.975 0.004 255)`, surface `oklch(1 0 0)`, ink `oklch(0.22 0.025 255)`, muted ink `oklch(0.48 0.025 255)`, line `oklch(0.89 0.012 255)`, action `oklch(0.53 0.18 255)`, success `oklch(0.55 0.13 155)`, warning `oklch(0.67 0.14 75)`, danger `oklch(0.55 0.19 25)`.

## Typography

Use Geist Sans for interface text and Geist Mono for JSON, identifiers, timestamps, and tabular values. Maintain a compact hierarchy and avoid display typography.

## Layout

A route-ink sidebar on the left carries navigation as a metro line (groups are lines, pages are stations, the current page a white stop), matching the sign-in screen. A quiet translucent top bar holds the page title and live state. Content sits on white 12px cards with soft shadows over a cool canvas; stats are separate tiles; tabs are segmented controls. Mix low-chroma colours with white in `srgb`, not `oklch` (oklch turns them pink). On the Endpoints page the agent registry owns the flexible main pane and the activity trace a fixed rail; drawers provide command review and trace inspection without losing list context.

## Components

Controls use 8-12px radii, defined borders, strong focus rings, and consistent hover, active, disabled, loading, and error states. Loading uses skeletons. Status always combines text or icons with color.

## Motion

Use 150-220ms ease-out transitions only for state changes, menus, and drawers. Respect `prefers-reduced-motion`.

## EndpointAgents Tray — WebView2

- **World:** a city metro map, used only where it carries meaning. The navigation rail is a line in deep line blue (`#17327a`; dark `#0f1f4f`) whose stations are the sections, the current one a white stop. A repair run in the chat is a run of stops (filled for passed, ringed with a soft ping for the current one, red for a failure, green terminus when done) that folds to one line when finished. The status page draws the route from this PC to the helpdesk (service, local channel, server, functions); a broken stop shows where help is blocked, with a dashed track after it. Everything else is a plain, familiar messenger.
- **Platform:** a frameless window whose title bar is part of the page (rail blue, status pill, Windows caption buttons at the physical left). Follows Windows light/dark through `prefers-color-scheme`; tokens live on `:root` in `ui/src/styles.css`. Review with `EndpointAgents.TrayWeb.exe --ui-preview [--dark|--light] [--section chat|status|logs] [--size WxH] [--snapshot png]`.
- **Type:** Vazirmatn Variable for everything (bundled, works offline), 14px base, 1.65–1.75 line height for Persian, Persian digits through `Intl` `fa-IR`. Monospace only for file names and hashes. Never use `·` as a separator next to Persian digits (it reads as zero); use `،`.
- **Language and direction:** root `dir="rtl"`; each message and preview uses `dir="auto"`; identifiers, paths and machine names sit in `<bdi class="ltr">`.
- **Helpdesk:** Telegram-like. Right: searchable history grouped by day with filters (all, open, resolved, closed), round avatars coloured by state, a "تازه" badge on conversations that changed while unseen. Centre: chat header with live state, tinted wall, 14px bubbles with one tucked corner (mine on the right in pale blue, the assistant on the left in white), day chips, the run line, and action cards that stay amber until answered, with "انجامش بده" / "نه، لازم نیست" buttons (typing بله/نه still works). Pill composer, round send button, Enter sends, Shift+Enter breaks, 200 characters with a counter near the limit. Left: details pane (≥1180px). Below 920px the history becomes an overlay. Closed conversations replace the composer with a "start a new conversation" bar; starting a new one while one is open asks first.
- **Consent:** service requests (IBSng install, Office/Windows repair, Windows Hello sample) open a modal sheet in the window; Escape and the focused default mean "no".
- **Status & Control:** verdict panel tinted by health, the route, a facts grid, and service controls kept separate; stopping the service confirms inline.
- **Reports:** search, level select, 1h/6h/24h/7d range (6h default), refresh every five seconds while visible, sticky table header, details pane, copy selected or all, save as text or JSON.
- **Accessibility:** visible focus rings, `aria-current` on the rail and selected conversation, live regions for status and chat, non-colour cues on every state, reduced motion removes the ping, blink and rise animations. Minimum window 760×640; close hides to the notification area.
