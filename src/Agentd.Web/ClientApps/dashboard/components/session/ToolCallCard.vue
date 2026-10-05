<script setup lang="ts">
import { computed, ref } from 'vue'
import { get } from '../../../shared/api/http'
import type { AgentEvent } from '../../../shared/api/types'
import { AgCollapsible } from '../../../shared/components/ui'
import { toolSummary } from './transcript'

const props = defineProps<{ call: AgentEvent; result?: AgentEvent }>()
const maxLines = 200

const head = computed(() => toolSummary(props.call))
const resultPayload = ref<Record<string, unknown> | null>(null)
const payload = computed(() => resultPayload.value ?? ((props.result?.payload ?? {}) as Record<string, unknown>))
const failed = computed(() => payload.value.isError === true)
const truncated = computed(() => payload.value.truncated === true)
const output = computed(() => (typeof payload.value.content === 'string' ? payload.value.content : ''))
const lines = computed(() => output.value.split('\n'))
const showAll = ref(false)
const visibleOutput = computed(() => (showAll.value ? output.value : lines.value.slice(0, maxLines).join('\n')))
const input = computed(() => {
  try {
    return JSON.stringify(JSON.parse(String((props.call.payload as Record<string, unknown>).inputJson ?? '{}')), null, 2)
  } catch {
    return String((props.call.payload as Record<string, unknown>).inputJson ?? '')
  }
})

/** Big results are trimmed in the live stream; fetch the full event once. */
async function expand(): Promise<void> {
  showAll.value = true
  if (truncated.value && props.result?.jobId != null) {
    const full = await get<AgentEvent>(`/api/jobs/${props.result.jobId}/events/${props.result.seq}`)
    resultPayload.value = full.payload as Record<string, unknown>
  }
}
</script>

<template>
  <AgCollapsible
    :default-open="failed"
    :data-failed="failed || undefined"
  >
    <template #title>
      <span class="font-mono text-[13px]">{{ head.name }}</span>
      <span class="min-w-0 flex-1 truncate font-mono text-[13px] text-muted">{{ head.summary }}</span>
      <span
        v-if="!result"
        class="loading loading-dots loading-xs"
        aria-label="running"
      />
      <span
        v-else-if="failed"
        class="badge badge-error badge-sm"
      >✗ failed</span>
      <span
        v-else
        class="text-success"
        aria-label="succeeded"
      >✓</span>
    </template>
    <div class="grid gap-2">
      <pre class="max-h-60 overflow-auto rounded bg-base-100 p-2 font-mono text-[12px]">{{ input }}</pre>
      <template v-if="result">
        <pre
          class="max-h-96 overflow-auto rounded p-2 font-mono text-[12px]"
          :class="failed ? 'bg-error/10' : 'bg-base-100'"
        >{{ visibleOutput }}</pre>
        <button
          v-if="(lines.length > maxLines || truncated) && !showAll"
          type="button"
          class="btn btn-ghost btn-xs justify-self-start"
          @click="expand"
        >
          Show all{{ truncated ? '' : ` (${lines.length} lines)` }}
        </button>
      </template>
    </div>
  </AgCollapsible>
</template>
