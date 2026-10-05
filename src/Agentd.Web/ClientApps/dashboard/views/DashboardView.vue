<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { JobSummary } from '../../shared/api/types'
import { AgButton, AgModal, AgToggleGroup } from '../../shared/components/ui'
import JobTable from '../components/JobTable.vue'
import RunWorkItemModal from '../components/RunWorkItemModal.vue'
import StatsBar from '../components/StatsBar.vue'
import { useConfigStore } from '../stores/config'
import { useJobsStore } from '../stores/jobs'

export type DashboardFilter = 'all' | 'running' | 'waiting' | 'queued' | 'failed'
const filters: { value: DashboardFilter; label: string; states: string[] }[] = [
  { value: 'all', label: 'All', states: [] },
  { value: 'running', label: 'Running', states: ['Running', 'Preparing'] },
  { value: 'waiting', label: 'Waiting', states: ['WaitingForHuman'] },
  { value: 'queued', label: 'Queued', states: ['Queued'] },
  { value: 'failed', label: 'Failed', states: ['Failed'] },
]

const jobs = useJobsStore()
void useConfigStore().load().catch(() => {})
const route = useRoute()
const router = useRouter()
const search = ref('')
const running = ref(false)
const confirming = ref<JobSummary | null>(null)

/** Single choice, kept in ?filter= so the waiting badge can link here. */
const filter = computed<DashboardFilter>({
  get: () => (filters.some((f) => f.value === route.query.filter) ? (route.query.filter as DashboardFilter) : 'all'),
  set: (value) => void router.replace({ query: { ...route.query, filter: value === 'all' ? undefined : value } }),
})
const filterModel = computed<string[]>({
  get: () => [filter.value],
  set: (values) => (filter.value = (values.filter((v) => v !== filter.value).at(-1) ?? 'all') as DashboardFilter),
})

const visible = computed(() => {
  const states = filters.find((f) => f.value === filter.value)!.states
  const q = search.value.trim().toLowerCase().replace(/^wi-/, '')
  return jobs.active.filter(
    (j) => (states.length === 0 || states.includes(j.state)) && (q === '' || j.title.toLowerCase().includes(q) || String(j.workItemId).startsWith(q)),
  )
})

async function confirmCancel(): Promise<void> {
  const job = confirming.value
  confirming.value = null
  if (job) await jobs.cancel(job.id)
}
</script>

<template>
  <section class="mx-auto grid max-w-6xl gap-4">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h1 class="text-xl font-semibold">
        Dashboard
      </h1>
      <AgButton
        size="sm"
        @click="running = true"
      >
        Run work item…
      </AgButton>
    </div>
    <StatsBar />
    <div class="flex flex-wrap items-center gap-2">
      <AgToggleGroup
        v-model="filterModel"
        label="Show"
        :options="filters"
      />
      <input
        v-model="search"
        type="search"
        class="input input-sm input-bordered w-full sm:ml-auto sm:w-64"
        placeholder="Search title or WI-id"
        aria-label="Search jobs"
      >
    </div>
    <JobTable
      v-if="visible.length"
      :jobs="visible"
      :pending="jobs.pending"
      @cancel="confirming = $event"
      @retry="jobs.retry($event.id)"
    />
    <div
      v-else
      class="card border border-base-300 bg-base-200"
    >
      <div class="card-body items-center text-center">
        <p class="font-medium">
          {{ jobs.active.length ? 'No jobs match this filter.' : 'No active jobs.' }}
        </p>
        <p
          v-if="!jobs.active.length"
          class="text-sm text-muted"
        >
          Tag a work item with <code class="font-mono">ai-workflow</code> to start one, or run it here.
        </p>
      </div>
    </div>
    <RunWorkItemModal v-model:open="running" />
    <AgModal
      :open="confirming !== null"
      title="Cancel this job?"
      @update:open="(v: boolean) => !v && (confirming = null)"
    >
      The agent stops and the job ends as Cancelled. Work item WI-{{ confirming?.workItemId }}: {{ confirming?.title }}
      <template #actions>
        <AgButton
          variant="ghost"
          @click="confirming = null"
        >
          Keep running
        </AgButton>
        <AgButton
          variant="error"
          @click="confirmCancel"
        >
          Cancel job
        </AgButton>
      </template>
    </AgModal>
  </section>
</template>
