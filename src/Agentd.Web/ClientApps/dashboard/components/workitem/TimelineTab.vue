<script setup lang="ts">
// Every lifecycle step and message, one section per job. The agent's output is in the run's Transcript.
import { computed } from 'vue'
import type { AgentEvent, JobState, JobSummary } from '../../../shared/api/types'
import { AgStateBadge } from '../../../shared/components/ui'
import { clock } from '../../../shared/utils/format'
import EventRow from '../session/EventRow.vue'
import { toRows } from '../session/transcript'
import { sections } from './sections'

const props = defineProps<{ jobs: JobSummary[]; events: AgentEvent[]; hasMore: boolean }>()
const emit = defineEmits<{ more: [] }>()
const steps = new Set(['state', 'question', 'reply', 'error', 'turn'])
const groups = computed(() => sections(props.jobs, props.events).map((s) => ({ ...s, rows: toRows(s.events).filter((r) => steps.has(r.kind)) })))
</script>

<template>
  <div class="grid gap-6">
    <section
      v-for="g in groups"
      :key="g.job.id"
      class="grid gap-2"
      :aria-label="g.label"
    >
      <h2 class="flex flex-wrap items-center gap-2 text-base font-semibold">
        {{ g.label }}
        <AgStateBadge :state="g.job.state as JobState" />
        <RouterLink
          :to="{ name: 'work-item', params: { id: g.job.workItemId }, query: { run: String(g.job.id) } }"
          title="Show this run's transcript"
          class="link text-xs font-normal text-muted"
        >
          job #{{ g.job.id }} · {{ clock(g.job.startedAt) }}
        </RouterLink>
      </h2>
      <div class="grid gap-3 border-l-2 border-base-300 pl-4">
        <EventRow
          v-for="row in g.rows"
          :key="row.key"
          :row="row"
        />
        <p
          v-if="!g.rows.length"
          class="text-sm text-muted"
        >
          No steps loaded yet.
        </p>
      </div>
    </section>
    <button
      v-if="hasMore"
      type="button"
      class="btn btn-ghost btn-sm justify-self-center"
      @click="emit('more')"
    >
      Load more
    </button>
  </div>
</template>
