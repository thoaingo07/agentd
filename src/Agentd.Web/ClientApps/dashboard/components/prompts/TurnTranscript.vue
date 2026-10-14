<script setup lang="ts">
// One turn's transcript on the Prompts page: the agent's text, tool calls with their results, questions and state,
// drawn with the work item Transcript's own rows. Read once, when the turn comes into view (or right away without
// IntersectionObserver, as in tests).
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ApiError, get } from '../../../shared/api/http'
import type { AgentEvent } from '../../../shared/api/types'
import EventRow from '../session/EventRow.vue'
import { toRows } from '../session/transcript'

const props = defineProps<{ promptId: number }>()
const events = ref<AgentEvent[] | null>(null)
const error = ref<string | null>(null)
const root = ref<HTMLElement | null>(null)
const rows = computed(() => toRows(events.value ?? []))
let observer: IntersectionObserver | undefined

async function load(): Promise<void> {
  if (events.value) return
  try {
    events.value = await get<AgentEvent[]>(`/api/prompts/${props.promptId}/transcript`)
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'The transcript couldn\'t be read.'
  }
}

onMounted(() => {
  if (typeof IntersectionObserver === 'undefined' || !root.value) {
    void load()
    return
  }

  observer = new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) {
      observer?.disconnect()
      void load()
    }
  }, { rootMargin: '400px 0px' })
  observer.observe(root.value)
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div
    ref="root"
    class="ag-transcript"
    data-testid="turn-transcript"
  >
    <p
      v-if="error"
      class="ag-meta text-error"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!events"
      class="ag-meta"
    >
      Loading the transcript…
    </p>
    <p
      v-else-if="!rows.length"
      class="ag-meta"
    >
      No transcript kept for this turn.
    </p>
    <EventRow
      v-for="row in rows"
      :key="row.key"
      :row="row"
    />
  </div>
</template>
