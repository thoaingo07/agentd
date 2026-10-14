<script setup lang="ts">
// The handle on a pane's right edge: drag it (or use the arrow keys when it has focus) to resize the pane; double-click
// gives back the default width. Wide screens only, where the panes sit side by side.
const width = defineModel<number>({ required: true })
const props = defineProps<{ min: number; max: number; initial: number; label: string }>()

let start: { x: number; width: number } | null = null

function down(e: PointerEvent): void {
  start = { x: e.clientX, width: width.value }
  ;(e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId)
  e.preventDefault()
}

function move(e: PointerEvent): void {
  if (start) width.value = Math.min(props.max, Math.max(props.min, start.width + e.clientX - start.x))
}

function up(): void {
  start = null
}

function key(e: KeyboardEvent): void {
  const step = e.shiftKey ? 64 : 16
  const next = e.key === 'ArrowLeft' ? width.value - step : e.key === 'ArrowRight' ? width.value + step
    : e.key === 'Home' ? props.min : e.key === 'End' ? props.max : null
  if (next === null) return
  e.preventDefault()
  width.value = Math.min(props.max, Math.max(props.min, next))
}
</script>

<template>
  <div
    role="separator"
    aria-orientation="vertical"
    tabindex="0"
    :aria-label="label"
    :aria-valuenow="width"
    :aria-valuemin="min"
    :aria-valuemax="max"
    title="Drag to resize · double-click for the default width"
    class="group absolute inset-y-0 -right-1.5 z-10 hidden w-3 cursor-col-resize touch-none outline-none lg:block"
    data-testid="pane-resizer"
    @pointerdown="down"
    @pointermove="move"
    @pointerup="up"
    @pointercancel="up"
    @keydown="key"
    @dblclick="width = initial"
  >
    <span
      class="mx-auto block h-full w-0.5 rounded-full bg-transparent transition-colors group-hover:bg-primary/60 group-focus-visible:bg-primary group-active:bg-primary"
      aria-hidden="true"
    />
  </div>
</template>
