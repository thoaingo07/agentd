<script setup lang="ts">
// The agent's turns per job: its text and tool calls (the same cards as the Session view).
import { computed } from 'vue'
import type { AgentEvent, JobSummary } from '../../../shared/api/types'
import EventRow from '../session/EventRow.vue'
import { toRows } from '../session/transcript'
import { sections } from './sections'

const props = defineProps<{ jobs: JobSummary[]; events: AgentEvent[] }>()
const agent = new Set(['text', 'tool', 'turn'])
const groups = computed(() => sections(props.jobs, props.events).map((s) => ({ ...s, rows: toRows(s.events).filter((r) => agent.has(r.kind)) })))
</script>

<template>
  <div class="grid gap-6">
    <section
      v-for="g in groups"
      :key="g.job.id"
      class="grid gap-2"
    >
      <h2 class="flex items-center gap-2 text-base font-semibold">
        {{ g.label }}
        <RouterLink
          :to="{ name: 'job', params: { id: g.job.id } }"
          class="link text-xs font-normal text-muted"
        >
          full transcript →
        </RouterLink>
      </h2>
      <EventRow
        v-for="row in g.rows"
        :key="row.key"
        :row="row"
      />
      <p
        v-if="!g.rows.length"
        class="text-sm text-muted"
      >
        No agent activity loaded.
      </p>
    </section>
  </div>
</template>
