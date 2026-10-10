# EndpointAgents Control Plane Design

## Theme

Light-first operations console. Neutral surfaces carry dense data; blue is reserved for selection and action, while green, amber, and red communicate operational state.

## Color

Use OKLCH tokens: canvas `oklch(0.975 0.004 255)`, surface `oklch(1 0 0)`, ink `oklch(0.22 0.025 255)`, muted ink `oklch(0.48 0.025 255)`, line `oklch(0.89 0.012 255)`, action `oklch(0.53 0.18 255)`, success `oklch(0.55 0.13 155)`, warning `oklch(0.67 0.14 75)`, danger `oklch(0.55 0.19 25)`.

## Typography

Use Geist Sans for interface text and Geist Mono for JSON, identifiers, timestamps, and tabular values. Maintain a compact hierarchy and avoid display typography.

## Layout

A quiet status header sits above a two-pane workspace: the agent registry owns the flexible main pane and the activity trace occupies a fixed rail. Drawers provide command review and trace inspection without losing list context.

## Components

Controls use 8-12px radii, defined borders, strong focus rings, and consistent hover, active, disabled, loading, and error states. Loading uses skeletons. Status always combines text or icons with color.

## Motion

Use 150-220ms ease-out transitions only for state changes, menus, and drawers. Respect `prefers-reduced-motion`.

## EndpointAgents Tray — WinUI 3

- **Character and platform:** A calm, native Fluent operational console that follows system light, dark, and high-contrast modes. Use Mica on supported Windows 11 systems with a transparent root; fall back to a solid system surface when Mica is unavailable. The `.impeccable/mocks/tray-winui-*` mocks predate the messenger layout. Run a Debug build with `--ui-preview` (optionally `--dark`, `--narrow`, `--snapshot <png>`) to review it with sample data.
- **Language and navigation:** The root is Persian RTL, while each conversation message derives its own direction. Isolate identifiers, paths, hashes, and timestamps as LTR. A compact icon rail (`NavigationView` in `LeftCompact`, physically right in RTL) exposes Conversations, Status & Control, and Reports; connection health is a coloured dot plus text in the title bar.
- **Helpdesk (messenger layout):** Telegram-like three columns. Right: searchable conversation history with status filters, a pencil "new conversation" button, and round status-coloured avatars. Centre: chat header (avatar, title, live state), a soft gradient chat wallpaper, 14px bubbles with one tucked corner (outgoing right in light green/blue, incoming left in white/dark with the author name), progress as a centred collapsible service card, and a pill composer with a round send button. Left: a collapsible details pane (conversation status and times, this PC's connection, short help) that hides below ~1040px; the history list becomes an overlay below 720px. Closed conversations replace the composer with a "start a new conversation" bar. Enter sends, Shift+Enter inserts a newline, and input is limited to 200 characters. Sensitive actions use native approval dialogs.
- **Status & Control:** Present related service, connection, capability, activity, and problem facts as a grouped readout; keep service actions separate so operational state is never mistaken for an action.
- **Reports:** Use labeled filters with 1h, 6h, 24h, and 7d ranges (6h by default). Refresh manually and every five seconds without reloading other sections. Use a virtualized `ListView` with persistent headers, Event ID, and a persistent details area; preserve selection across refreshes. Copy the selected item, or copy all visible results when nothing is selected.
- **System and accessibility:** Use restrained neutral and blue system colors, native Segoe UI/system typography, 12px radii, accessible names, live announcements, non-color status cues, and system focus visuals. The minimum window size is 760×640. Minimize sends the window to the taskbar; close hides the window while the app keeps running in the notification area; restoring returns to the previous section. Visible WinForms controls are prohibited: WinForms is limited to the `NotifyIcon` adapter.
