<script setup lang="ts">
// The transcript: follow mode (stick to the bottom), "N new events ↓" when scrolled up, and
// "Load earlier" that keeps the reader's place.
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { AgentEvent } from '../../../shared/api/types'
import { AgToggleGroup } from '../../../shared/components/ui'
import type { EventCategory } from '../../stores/ui'
import EventRow from './EventRow.vue'
import { categoryOf, toRows } from './transcript'

const props = defineProps<{ events: AgentEvent[]; hasMore: boolean; loadEarlier: () => Promise<void> }>()
const categories = defineModel<string[]>('categories', { default: () => ['text', 'tools', 'messages', 'state', 'errors'] })
const follow = defineModel<boolean>('follow', { default: true })

const scroller = ref<HTMLElement | null>(null)
const unseen = ref(0)
const loading = ref(false)
const rows = computed(() => toRows(props.events).filter((r) => categories.value.includes(categoryOf[r.kind] as EventCategory)))
const reducedMotion = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

const filterOptions = [
  { value: 'text', label: 'Text' },
  { value: 'tools', label: 'Tools' },
  { value: 'messages', label: 'Messages' },
  { value: 'state', label: 'State' },
  { value: 'errors', label: 'Errors' },
]

function toBottom(): void {
  const el = scroller.value
  if (!el) return
  if (reducedMotion || typeof el.scrollTo !== 'function') el.scrollTop = el.scrollHeight
  else el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' })
}

/** Scrolling more than 80 px up from the bottom stops following. */
function onScroll(): void {
  const el = scroller.value!
  const fromBottom = el.scrollHeight - el.scrollTop - el.clientHeight
  if (fromBottom > 80) follow.value = false
  else if (!follow.value) resume()
}

function resume(): void {
  follow.value = true
  unseen.value = 0
  void nextTick(toBottom)
}

watch(
  () => props.events.length,
  async (now, before) => {
    if (follow.value) {
      await nextTick()
      toBottom()
    } else if (now > before) {
      unseen.value += now - before
    }
  },
)

/** Prepending changes scrollHeight; shift scrollTop by the same amount so the view doesn't jump. */
async function earlier(): Promise<void> {
  const el = scroller.value!
  const before = el.scrollHeight
  loading.value = true
  try {
    await props.loadEarlier()
    await nextTick()
    el.scrollTop += el.scrollHeight - before
  } finally {
    loading.value = false
  }
}

onMounted(() => void nextTick(toBottom))
defineExpose({ resume, onScroll, earlier, unseen })
</script>

<template>
  <div class="grid gap-2">
    <AgToggleGroup
      v-model="categories"
      label="Show"
      :options="filterOptions"
    />
    <div class="relative">
      <div
        ref="scroller"
        class="grid h-[60vh] content-start gap-3 overflow-y-auto rounded-box border border-base-300 p-3"
        tabindex="0"
        aria-label="Transcript"
        @scroll="onScroll"
        @keydown.end.prevent="resume"
      >
        <button
          v-if="hasMore"
          type="button"
          class="btn btn-ghost btn-xs justify-self-center"
          :disabled="loading"
          @click="earlier"
        >
          Load earlier
        </button>
        <EventRow
          v-for="row in rows"
          :key="row.key"
          :row="row"
          :data-seq="row.key"
        />
        <p
          v-if="!rows.length"
          class="text-center text-sm text-muted"
        >
          Nothing here yet.
        </p>
      </div>
      <button
        v-if="!follow && unseen > 0"
        type="button"
        class="btn btn-primary btn-sm absolute bottom-3 left-1/2 -translate-x-1/2 shadow-sm"
        @click="resume"
      >
        {{ unseen }} new event{{ unseen === 1 ? '' : 's' }} ↓
      </button>
    </div>
  </div>
</template>
