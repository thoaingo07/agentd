# agentd — Design System

The visual language for the agentd Web UI: a calm, dense operations console built around the
**Vue green**. It has one light theme and one dark theme, and every color pair meets WCAG AA.

- **Styling engine:** Tailwind CSS 4 + **daisyUI 5**, using the custom themes `agentd` and `agentd-dark`
- **Interactive primitives:** **base-ui-vue** (unstyled, accessible). It provides behavior and
  a11y; daisyUI and Tailwind provide the look.
- **Rule:** no other UI or styling libraries.

Related: [UI spec](../ui/README.md) · [Architecture §3.9](../architect/README.md)

---

## 1. Principles

1. **Status at a glance.** Job state is the most important information, so it always has a color,
   an icon *and* a label. Color is never the only signal.
2. **Green means healthy and active.** The brand green marks progress, success and primary actions.
   Amber is reserved for *"a human is needed"*, the one state that should pull the eye.
3. **Dense but readable.** This is a tool you watch all day. Compact tables, monospace for
   anything machine-generated, and generous line height inside transcripts.
4. **Quiet chrome, loud content.** Neutral surfaces with subtle borders, so the transcript and diffs
   stand out.
5. **Accessible by default.** base-ui-vue handles focus and ARIA, and the tokens below guarantee contrast.

---

## 2. Color

### 2.1 Brand palette

| Token | Hex | Use |
|---|---|---|
| `green-500` (brand) | `#42b883` | primary buttons, active tab indicator, running state, focus ring |
| `green-600` | `#33a06f` | primary hover |
| `green-700` (brand ink) | `#247a53` | **green text on light backgrounds** (5.27:1 on white) |
| `navy-700` | `#35495e` | secondary, sidebar in light mode |
| `navy-800` | `#213547` | body text in light mode (12.6:1 on white) |
| `navy-900` | `#1b2733` | text on green; dark surface |
| `navy-950` | `#131c25` | dark background |

> `#42b883` with white text is only 2.5:1, so **text on the green must be navy `#1b2733`**
> (6.08:1). For green *text* on a white background, use `#247a53`.

### 2.2 Semantic tokens (daisyUI)

| daisyUI token | Light `agentd` | Content (contrast) | Dark `agentd-dark` | Content (contrast) |
|---|---|---|---|---|
| `primary` | `#42b883` | `#1b2733` (6.08) | `#42b883` | `#131c25` (6.9) |
| `secondary` | `#35495e` | `#ffffff` (9.27) | `#8fa3b5` | `#131c25` |
| `accent` | `#6f52c2` | `#ffffff` (5.73) | `#a893e8` | `#131c25` (6.56) |
| `neutral` | `#213547` | `#ffffff` (12.6) | `#243241` | `#e3ebe7` (10.8) |
| `base-100` | `#ffffff` | `base-content` `#213547` | `#131c25` | `#e3ebe7` (14.2) |
| `base-200` | `#f6f8f7` | | `#1b2733` | `#e3ebe7` (12.5) |
| `base-300` | `#e2e8e5` | | `#243241` | |
| `info` | `#2a6fab` | `#ffffff` (5.3) | `#6fb3ea` | `#131c25` (7.63) |
| `success` | `#247a53` | `#ffffff` (5.27) | `#42b883` | `#131c25` (6.9) |
| `warning` | `#e8a33d` | `#1b2733` (7.03) | `#f0b85e` | `#131c25` (9.6) |
| `error` | `#c93a3f` | `#ffffff` (5.05) | `#f07a7e` | `#131c25` (6.38) |

Extra tokens not covered by daisyUI:

| Token | Light | Dark | Use |
|---|---|---|---|
| `--color-muted` | `#5b6b7c` (5.47) | `#8fa3b5` | timestamps, secondary labels |
| `--color-brand-ink` | `#247a53` | `#42b883` | green links and inline highlights |

### 2.3 Job state colors

| State | daisyUI class | Icon | Notes |
|---|---|---|---|
| `Queued` | `badge-ghost` | clock | |
| `Preparing` | `badge-info badge-soft` | folder-plus | |
| `Running` | `badge-primary` | pulsing dot | the dot animates; respect `prefers-reduced-motion` |
| `WaitingForHuman` | `badge-warning` | message-question | **the only attention color**; also a counter in the nav |
| `Publishing` | `badge-accent badge-soft` | git-pull-request | |
| `Done` | `badge-success` | check | |
| `Failed` | `badge-error` | x-octagon | |
| `Cancelled` | `badge-ghost` + muted text | slash | |

### 2.4 Event type colors (session trace)

| Event | Treatment |
|---|---|
| `assistant.text` | plain transcript text on `base-100`, with a small green "Claude" label |
| `tool.call` / `tool.result` | collapsible card on `base-200`: tool name in mono, one-line summary; expanding shows the full input and output |
| `ask_developer` | `chat-start` bubble with `bg-warning/15` and a `border-warning` left border |
| `message.inbound` / UI message | `chat-end` bubble, `bg-primary/15`, with a small provider label (Discord · Telegram · Web) |
| `job.state_changed` | full-width `divider` with a state badge and time |
| `turn.result` | small muted line: turns · tokens · cost |
| `job.error` | `alert alert-error alert-soft` |

---

## 3. Typography

System font stacks, with no web font dependency:

| Token | Stack |
|---|---|
| `--font-sans` | `ui-sans-serif, system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif` |
| `--font-mono` | `ui-monospace, "JetBrains Mono", "Cascadia Code", "SF Mono", Menlo, Consolas, monospace` |

| Role | Class | Size / line height |
|---|---|---|
| Page title | `text-xl font-semibold` | 20 / 28 |
| Section title | `text-base font-semibold` | 16 / 24 |
| Body / table | `text-sm` | 14 / 20 |
| Transcript text | `text-sm leading-6` | 14 / 24 |
| Meta (time, cost) | `text-xs text-muted` | 12 / 16 |
| Code, IDs, branches, tool names | `font-mono text-[13px]` | 13 / 20 |

Numbers in tables use `tabular-nums`.

---

## 4. Space, shape, elevation

- **Spacing:** Tailwind's 4px scale. The common steps are `1` (4px), `2` (8px), `3` (12px), `4` (16px)
  and `6` (24px). The page gutter is `4` on mobile and `6` on desktop.
- **Radius:** `--radius-field: 0.5rem` (buttons, inputs), `--radius-box: 0.75rem` (cards, modals),
  `--radius-selector: 1rem` (badges, toggles).
- **Borders:** 1px `base-300`. Most separation comes from borders rather than shadows.
- **Elevation:** `shadow-sm` only on floating elements (tooltip, dropdown, modal). `--depth: 1`, `--noise: 0`.
- **Focus:** `outline-2 outline-offset-2 outline-primary` on `:focus-visible`. It is never removed.

---

## 5. Theme implementation

`web/src/styles/app.css`:

```css
@import "tailwindcss";

@plugin "daisyui" {
  themes: false;               /* only our themes */
}

@plugin "daisyui/theme" {
  name: "agentd";
  default: true;
  color-scheme: light;
  --color-base-100: #ffffff;
  --color-base-200: #f6f8f7;
  --color-base-300: #e2e8e5;
  --color-base-content: #213547;
  --color-primary: #42b883;
  --color-primary-content: #1b2733;
  --color-secondary: #35495e;
  --color-secondary-content: #ffffff;
  --color-accent: #6f52c2;
  --color-accent-content: #ffffff;
  --color-neutral: #213547;
  --color-neutral-content: #ffffff;
  --color-info: #2a6fab;
  --color-info-content: #ffffff;
  --color-success: #247a53;
  --color-success-content: #ffffff;
  --color-warning: #e8a33d;
  --color-warning-content: #1b2733;
  --color-error: #c93a3f;
  --color-error-content: #ffffff;
  --radius-selector: 1rem;
  --radius-field: 0.5rem;
  --radius-box: 0.75rem;
  --size-selector: 0.25rem;
  --size-field: 0.25rem;
  --border: 1px;
  --depth: 1;
  --noise: 0;
}

@plugin "daisyui/theme" {
  name: "agentd-dark";
  prefersdark: true;
  color-scheme: dark;
  --color-base-100: #131c25;
  --color-base-200: #1b2733;
  --color-base-300: #243241;
  --color-base-content: #e3ebe7;
  --color-primary: #42b883;
  --color-primary-content: #131c25;
  --color-secondary: #8fa3b5;
  --color-secondary-content: #131c25;
  --color-accent: #a893e8;
  --color-accent-content: #131c25;
  --color-neutral: #243241;
  --color-neutral-content: #e3ebe7;
  --color-info: #6fb3ea;
  --color-info-content: #131c25;
  --color-success: #42b883;
  --color-success-content: #131c25;
  --color-warning: #f0b85e;
  --color-warning-content: #131c25;
  --color-error: #f07a7e;
  --color-error-content: #131c25;
  --radius-selector: 1rem;
  --radius-field: 0.5rem;
  --radius-box: 0.75rem;
  --size-selector: 0.25rem;
  --size-field: 0.25rem;
  --border: 1px;
  --depth: 1;
  --noise: 0;
}

@theme {
  --font-sans: ui-sans-serif, system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  --font-mono: ui-monospace, "JetBrains Mono", "Cascadia Code", "SF Mono", Menlo, Consolas, monospace;
  --color-muted: #5b6b7c;
  --color-brand-ink: #247a53;
}

[data-theme="agentd-dark"] {
  --color-muted: #8fa3b5;
  --color-brand-ink: #42b883;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="agentd"]) {
    --color-muted: #8fa3b5;
    --color-brand-ink: #42b883;
  }
}
```

Theme switching: the `ui` store sets `document.documentElement.dataset.theme` to `agentd`,
`agentd-dark`, or removes it for **System**. The choice is saved in `localStorage`. To avoid a
flash of the wrong theme, `public/theme-init.js` applies the saved choice before first paint. It is
an **external** script, because the CSP forbids inline scripts ([Security §3](../security/README.md#3-content-security-policy)).

---

## 6. Component layer

### 6.1 Which tool to use

| Need | Use |
|---|---|
| Pure visuals (badge, card, table, stat, alert, chat bubble, divider, skeleton, loading, kbd, mockup-code) | **daisyUI classes** on plain elements |
| Interactive behavior with state, keyboard handling and ARIA (collapsible, tabs, tooltip, switch, toggle group, scroll area, progress, meter, form fields) | **base-ui-vue** part, styled with daisyUI/Tailwind classes |
| Dialogs, dropdowns, toasts (not in base-ui-vue) | daisyUI `modal` on native `<dialog>`, `dropdown` with the Popover API, and `toast` driven by the `ui` store |

### 6.2 Reusable components (`web/src/components/ui/`)

Every reusable component is an `Ag*` wrapper. It composes base-ui-vue parts (see the
[base-ui-vue quick start](https://baseui-vue.com/docs/overview/quick-start)) and applies the
design tokens. Feature code imports only `Ag*` components, never base-ui-vue directly. That keeps
the styling decisions in one folder.

| Component | Built from | Styling hooks |
|---|---|---|
| `AgButton` | `Button` (`as`, `disabled`, `focusable-when-disabled`) | `btn btn-{variant} btn-{size}`; `data-disabled` |
| `AgCollapsible` | `CollapsibleRoot` / `CollapsibleTrigger` / `CollapsiblePanel` | `data-panel-open` on trigger (rotate chevron); `data-open`, `data-starting-style`, `data-ending-style`, `--collapsible-panel-height` on panel |
| `AgTabs` | `TabsRoot` / `TabsList` / `TabsTab` / `TabsIndicator` / `TabsPanel` | `data-active` on tab; indicator positioned with `--active-tab-left` / `--active-tab-width` |
| `AgTooltip` | `TooltipProvider` › `TooltipRoot` / `TooltipTrigger` / `TooltipPortal` / `TooltipPositioner` / `TooltipPopup` / `TooltipArrow` | `data-open`, `data-side`, `data-starting-style` |
| `AgSwitch` | `Switch` | `data-checked` |
| `AgToggleGroup` | `ToggleGroup` + `Toggle` | `data-pressed` (event-type filters) |
| `AgScrollArea` | `ScrollArea` | transcript and log panes |
| `AgProgress` / `AgMeter` | `Progress` / `Meter` | concurrency slots, max-turns usage |
| `AgField` / `AgInput` | `Field` + `Input` | settings form, message composer |
| `AgStateBadge` | plain daisyUI `badge` | the state map in §2.3 |
| `AgModal` / `AgToast` | daisyUI + native `<dialog>` | confirm cancel/retry; action feedback |

### 6.3 Pattern example

```vue
<!-- web/src/components/ui/AgCollapsible.vue -->
<script setup lang="ts">
import { CollapsiblePanel, CollapsibleRoot, CollapsibleTrigger } from 'base-ui-vue'

defineProps<{ title: string; defaultOpen?: boolean }>()
</script>

<template>
  <CollapsibleRoot :default-open="defaultOpen" class="rounded-box border border-base-300 bg-base-200">
    <CollapsibleTrigger
      class="group flex w-full items-center gap-2 px-3 py-2 text-left font-mono text-[13px]
             focus-visible:outline-2 focus-visible:outline-primary"
    >
      <span class="transition-transform group-data-[panel-open]:rotate-90">▸</span>
      <slot name="title">{{ title }}</slot>
    </CollapsibleTrigger>
    <CollapsiblePanel
      class="h-[var(--collapsible-panel-height)] overflow-hidden border-t border-base-300
             transition-[height] duration-150 ease-out
             data-[starting-style]:h-0 data-[ending-style]:h-0
             motion-reduce:transition-none"
    >
      <div class="p-3"><slot /></div>
    </CollapsiblePanel>
  </CollapsibleRoot>
</template>
```

```vue
<!-- web/src/components/ui/AgTabs.vue (tab strip part) -->
<TabsList class="relative flex gap-1 border-b border-base-300">
  <TabsTab
    v-for="t in tabs" :key="t.value" :value="t.value"
    class="px-3 py-2 text-sm text-muted data-[active]:text-base-content data-[disabled]:opacity-50"
  >{{ t.label }}</TabsTab>
  <TabsIndicator
    class="absolute bottom-0 h-0.5 bg-primary transition-all
           left-[var(--active-tab-left)] w-[var(--active-tab-width)]"
  />
</TabsList>
```

Conventions:

- Style state with **`data-*` variants** (`data-[open]:`, `data-[active]:`) rather than JS class toggling.
- The `class` prop may be a function of state (`(state) => ...`), but use it only when a data attribute
  cannot express the style.
- Every `Ag*` component accepts `class` and merges it last, so callers can make local adjustments.
- Keep props minimal and typed; expose slots for content.

---

## 7. Motion

- Durations: 150ms (micro), 200ms (panels). Easing: `ease-out`.
- The running pulse is `animate-pulse` on a 6px dot only.
- Every transition includes `motion-reduce:transition-none` (and `motion-reduce:animate-none`).
- New transcript events fade in over 150ms. Auto-scroll stops when the user scrolls up (see the UI spec).

## 8. Iconography

Inline SVG icons (Lucide-style, 1.5px stroke, 16px or 20px), copied into `web/src/components/icons/` as
tiny SFCs. There is no icon library dependency. Icons are always paired with a text label or
`aria-label`.

## 9. Accessibility checklist

- Every text/background pair in §2 is AA (≥ 4.5:1) or better.
- State is never communicated by color alone (badge = icon + label).
- All interactive elements are keyboard reachable. base-ui-vue provides roving focus for tabs and
  toggle groups.
- Live transcript region: `aria-live="polite"` for new *questions* only, not for every event,
  to avoid screen-reader flooding.
- Respect `prefers-reduced-motion` and `prefers-color-scheme`.
