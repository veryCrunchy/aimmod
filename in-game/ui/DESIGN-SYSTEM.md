# Shared interface reference

Use the existing Roboto face and dark green surfaces. Load shared-components.css after workspace-theme.css. Existing class names remain supported.

## Foundations

Spacing follows 4, 8, 12, 16, 24 and 32 pixels. Use pad-sm/md/lg for 8/16/24 pixel padding and stack-sm/md/lg for vertical sibling spacing. Use flex layouts and sibling margins.

Type: text-title (24/32), text-heading (18/26), text-body (14/22), text-caption (12/18). text-muted uses readable secondary copy. text-accent, text-warning and text-danger provide semantic colours; always include a text label with status.

Surfaces: surface-canvas #0b1110, surface-panel #151f1c, surface-raised #1d2b26. Text #eef5f1, secondary #a7bab0, mint #27e4a1. Focus #8bf5ce. These are literal class tokens, not custom properties.

## Components

- Buttons: button with primary, secondary, ghost (quiet remains supported), or danger. Default height 36px; small 30px; large 42px. Use native disabled and a clear verb.
- Cards: card, card-header; existing panel remains supported.
- Choice controls: existing segmented with button children and the selected primary class. Set aria-pressed or radio semantics and keep selection state in sync.
- Tabs: tabs with button children, active and aria-selected. Implement arrow navigation when using tab roles.
- States: state with loading or error, heading, explanation and an action where useful. Use role=status for loading and role=alert for actionable failure. Existing empty remains supported.
- Chips: chip or badge, optionally success or warning. Put them in a flex row.
- Switches: existing switch, on and knob. Use a button with role=switch, aria-checked and aria-label.
- Inputs: input, an explicit label, and existing AimModFormat.field when a hint is needed. Never rely on hint text as the label.
- Tooltips: tooltip-wrap containing tooltip with role=tooltip; connect the trigger with aria-describedby. Essential instructions must also be visible on the page.

All interactive controls receive a visible mint focus ring. Use native buttons for actions. Avoid overriding the shared focus style.

## Runtime and ownership

The shell owns navigation, page heading, account chip, scenario context and global notifications. Pages own contextual actions within their toolbar. Keep existing IDs and class contracts intact.

Use ES5 functions and XMLHttpRequest. Layout uses flex and explicit margins; avoid grid, custom properties, calculated dimensions and sticky positioning. Keep assets within the existing serving path. Run node --test from this directory before handing off changes.

## Shell contracts

Keep page-title, scenario-contextbar, account-shortcut, lifecycle-banner and replay-status IDs. The account button contains account-avatar and account-label. The header inbox uses notice-trigger and notice-inbox. Its count covers undismissed information; repairs remain visible in lifecycle-banner. Recording uses recording-status as a quiet text line.

Lifecycle GET supplies dismissedNotices. POST lifecycle/dismiss accepts a single key, update:<version> or game:<build>; the service persists it independently of the browser port. Existing lifecycle/action dismiss handles completed installation results. Repair prompts cannot be dismissed. Release notes and install controls remain in Settings. A failed dismissal keeps the item visible and offers a retry message.

Navigation keeps native Tab/Enter behavior and supports Up/Down/Home/End focus movement. Escape closes the inbox before handling deeper page navigation. At 720px height the navigation compacts to 30px rows; the footer and page body remain independently reachable.
