# T3.8 — Reusable `Ag*` components (base-ui-vue + daisyUI)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | Phase 0 (theme, web scaffold) | M | web |

## Goal
Build the reusable component layer from the [design system §6](../../../design-system/README.md#6-component-layer).
Behavior and accessibility come from **base-ui-vue** parts, and the look comes from daisyUI and
Tailwind classes driven by `data-*` state attributes. Feature code imports only `Ag*` components,
never base-ui-vue directly.

## Files
- `src/Agentd.Web/ClientApps/shared/components/ui/AgButton.vue`, `AgCollapsible.vue`, `AgTabs.vue`, `AgTooltip.vue`,
  `AgToggleGroup.vue`, `AgMeter.vue`, `AgModal.vue`, `AgToast.vue` (+ `AgToastHost.vue`), `AgStateBadge.vue` — create.
- `src/Agentd.Web/ClientApps/shared/components/ui/index.ts` — create: barrel export.
- `src/Agentd.Web/ClientApps/shared/components/icons/*.vue` — create: inline SVG icons (clock, check, x-octagon, message-question, …).
- `src/Agentd.Web/package.json` — modify: add `base-ui-vue` (version pinned by the lockfile).
- `web/eslint.config.*` — modify: a `no-restricted-imports` rule that blocks `base-ui-vue` outside `components/ui/`.

## Implementation
1. **Pattern for every component:**
   - a typed `defineProps`, slots for content, and a `class` prop merged **last**;
   - state styling through `data-[open]:`, `data-[active]:`, `data-[panel-open]:` and
     `data-[disabled]:` variants, not JS class toggling.
2. **`AgButton`:**
   - base-ui-vue `Button` with props `variant` (`primary | ghost | outline | error`), `size`
     (`sm | md`) and `loading`;
   - classes `btn btn-{variant} btn-{size}`;
   - `loading` sets `focusable-when-disabled` and shows a daisyUI `loading loading-spinner`.
3. **`AgCollapsible`:** `CollapsibleRoot`, `CollapsibleTrigger` and `CollapsiblePanel`, as in
   [design system §6.3](../../../design-system/README.md#63-pattern-example). The height
   animates with `--collapsible-panel-height`, and there is `motion-reduce:transition-none`.
4. **`AgTabs`:**
   - `TabsRoot`, `TabsList`, `TabsTab`, `TabsIndicator` and `TabsPanel`;
   - props `tabs: { value, label, disabled? }[]` and `v-model`;
   - the indicator is positioned with `--active-tab-left` / `--active-tab-width`;
   - panels use `keep-mounted` only where state must survive a tab switch (the Transcript tab).
5. **`AgTooltip`:**
   - `TooltipProvider` (once, in `App.vue`) › `TooltipRoot`, `TooltipTrigger`, `TooltipPortal`,
     `TooltipPositioner`, `TooltipPopup` and `TooltipArrow`;
   - check that the portal and positioner only set styles through the CSSOM, never a `style` attribute
     in markup, so the CSP holds (see T3.12).
6. **`AgToggleGroup`:** `ToggleGroup` + `Toggle` for the event-type filters, using `data-[pressed]`
   (the attribute name must be verified against the pinned version).
7. **`AgMeter`:** base-ui-vue `Meter` for the concurrency slots, with a `label` and `value/max`.
8. **Not in base-ui-vue:**
   - **`AgModal`** is daisyUI `modal` on a native `<dialog>`, with `showModal()` and `close()`
     through a template ref, and Esc closes it;
   - **`AgToast`** + `AgToastHost` are daisyUI `toast` + `alert`, driven by `ui.toasts`, with auto
     dismiss after 5 s and `aria-live="polite"`.
9. **`AgStateBadge`:** maps `JobState` to a badge class, icon and label, following
   [design system §2.3](../../../design-system/README.md#23-job-state-colors). `Running` gets a
   pulsing dot with `motion-reduce:animate-none`.
10. **Verify the base-ui-vue part names and props** against the pinned version (the docs used are
    the [quick start](https://baseui-vue.com/docs/overview/quick-start) and the component pages). If
    a part is missing or buggy, implement that single component with daisyUI + native elements,
    keeping the same `Ag*` API, and note it in the file header.

## Tests
- vitest + `@vue/test-utils` for each component:
  - renders with a slot;
  - the `class` prop is merged;
  - keyboard: Tabs arrow keys move the focus; Collapsible toggles on Enter and Space; Esc closes the
    modal.
- `AgStateBadge` renders the icon + label for every `JobState`, never color only.
- The ESLint rule fails when a view imports `base-ui-vue`.

## Done when
- [ ] Every component listed exists, is typed, and follows the data-attribute styling pattern.
- [ ] Both themes look right, and the focus ring is visible on every interactive component.
- [ ] Only `components/ui/` imports `base-ui-vue` (enforced by lint).
- [ ] Any deviation from base-ui-vue is documented in the component file.
