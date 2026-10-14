<script setup lang="ts">
// A file at the review's commit, read-only, with line numbers: the search's file and a changed file's View file.
// `highlight`: lines to mark (a search hit, the lines a change added); `focus`: the line scrolled into view.
import { computed, nextTick, ref, watch } from 'vue'

const props = defineProps<{ text: string; truncated: boolean; binary: boolean; highlight: ReadonlySet<number>; focus: number | null }>()
const list = ref<HTMLElement | null>(null)
const lines = computed(() => props.text.split('\n'))

watch(() => [props.text, props.focus] as const, async ([, focus]) => {
  await nextTick()
  if (focus) list.value?.querySelector<HTMLElement>(`[data-line="${focus}"]`)?.scrollIntoView?.({ block: 'center' })
}, { immediate: true })
</script>

<template>
  <p
    v-if="binary"
    class="ag-meta"
  >
    A binary file: not shown.
  </p>
  <div
    v-else
    ref="list"
    class="ag-code"
    data-testid="commit-file"
  >
    <div
      v-for="(line, i) in lines"
      :key="i"
      class="ag-code-line"
      :class="{ 'is-hit': highlight.has(i + 1) }"
      :data-line="i + 1"
    >
      <span class="text-right text-muted select-none">{{ i + 1 }}</span><span>{{ line }}</span>
    </div>
    <p
      v-if="truncated"
      class="ag-meta px-2 py-1"
    >
      … the rest of this file is too big to show here.
    </p>
  </div>
</template>
