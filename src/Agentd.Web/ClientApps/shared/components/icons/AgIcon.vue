<script setup lang="ts">
// Inline SVG icons (no icon font or CDN: CSP). Paths follow the Lucide set (ISC license), 24×24, stroke-based.
import { computed } from 'vue'

export type IconName =
  | 'clock'
  | 'folder-plus'
  | 'message-question'
  | 'git-pull-request'
  | 'check'
  | 'x-octagon'
  | 'slash'
  | 'chevron-down'
  | 'x'
  | 'eye'
  | 'pause'

const paths: Record<IconName, string[]> = {
  clock: ['M12 6v6l4 2', 'M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z'],
  'folder-plus': ['M12 10v6', 'M9 13h6', 'M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'],
  'message-question': ['M7.9 20A9 9 0 1 0 4 16.1L2 22Z', 'M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3', 'M12 17h.01'],
  'git-pull-request': ['M18 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6z', 'M6 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6z', 'M13 6h3a2 2 0 0 1 2 2v7', 'M6 9v12'],
  check: ['M20 6 9 17l-5-5'],
  'x-octagon': ['M2.59 8.62 8.62 2.59A2 2 0 0 1 10.03 2h3.94a2 2 0 0 1 1.41.59l6.03 6.03a2 2 0 0 1 .59 1.41v3.94a2 2 0 0 1-.59 1.41l-6.03 6.03a2 2 0 0 1-1.41.59h-3.94a2 2 0 0 1-1.41-.59l-6.03-6.03A2 2 0 0 1 2 13.97v-3.94a2 2 0 0 1 .59-1.41Z', 'm15 9-6 6', 'm9 9 6 6'],
  slash: ['M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z', 'm4.9 4.9 14.2 14.2'],
  'chevron-down': ['m6 9 6 6 6-6'],
  x: ['M18 6 6 18', 'm6 6 12 12'],
  pause: ['M10 4H6v16h4z', 'M18 4h-4v16h4z'],
  eye: ['M2.06 12.35a1 1 0 0 1 0-.7 10.75 10.75 0 0 1 19.88 0 1 1 0 0 1 0 .7 10.75 10.75 0 0 1-19.88 0', 'M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z'],
}

const props = withDefaults(defineProps<{ name: IconName; size?: number; label?: string }>(), { size: 16, label: undefined })
const d = computed(() => paths[props.name])
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    :role="label ? 'img' : undefined"
    :aria-label="label"
    :aria-hidden="label ? undefined : 'true'"
    focusable="false"
  >
    <path
      v-for="p in d"
      :key="p"
      :d="p"
    />
  </svg>
</template>
