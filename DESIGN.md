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
