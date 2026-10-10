<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError, get } from '../../../shared/api/http'
import type { Diff } from '../../../shared/api/types'
import DiffView from '../session/DiffView.vue'
import { useEventsStore } from '../../stores/events'

/** Loaded when shown, then refreshed (2 s debounce) after the agent edits files. */
const diffRefreshMs = 2000
const props = defineProps<{ jobId: number }>()
const events = useEventsStore()
const diff = ref<Diff | null>(null)
const error = ref<string | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined

async function load(): Promise<void> {
  try {
    diff.value = await get<Diff>(`/api/jobs/${props.jobId}/diff`)
    error.value = null
  } catch (err) {
    error.value = err instanceof ApiError && err.status === 404 ? 'No branch yet: the diff appears once the agent starts working.' : 'Could not load the diff.'
  }
}

/** Finished Edit / MultiEdit / Write calls in the run's transcript. */
const edits = computed(() => {
  const calls = new Map<string, string>()
  let count = 0
  for (const e of events.windows.get(props.jobId)?.events ?? []) {
    const p = e.payload as Record<string, unknown>
    if (e.type === 'agent.tool_call') calls.set(String(p.id), String(p.name))
    else if (e.type === 'agent.tool_result' && ['Edit', 'MultiEdit', 'Write'].includes(calls.get(String(p.toolUseId)) ?? '')) count++
  }
  return count
})

watch(() => props.jobId, () => {
  diff.value = null
  error.value = null
  void load()
}, { immediate: true })
watch(edits, (now, before) => {
  if (now <= before) return
  clearTimeout(timer)
  timer = setTimeout(() => void load(), diffRefreshMs)
})
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <DiffView
    v-if="diff"
    :diff="diff"
  />
  <p
    v-else-if="error"
    class="text-sm text-muted"
  >
    {{ error }}
  </p>
  <p
    v-else
    class="text-sm text-muted"
  >
    Loading the diff…
  </p>
</template>
