<script setup lang="ts">
import { computed } from 'vue'
import { AgMeter } from '../../shared/components/ui'
import { useConfigStore } from '../stores/config'
import { useJobsStore } from '../stores/jobs'

const jobs = useJobsStore()
const config = useConfigStore()
const max = computed(() => config.config?.maxConcurrent ?? 0)
const running = computed(() => (jobs.stats.Running ?? 0) + (jobs.stats.Preparing ?? 0))
const tiles = computed(() => [
  { label: 'Running', value: max.value ? `${running.value}/${max.value}` : String(running.value) },
  { label: 'Waiting for you', value: String(jobs.stats.WaitingForHuman ?? 0), warn: (jobs.stats.WaitingForHuman ?? 0) > 0 },
  { label: 'Queued', value: String(jobs.stats.Queued ?? 0) },
  { label: 'In review', value: String((jobs.stats.InReview ?? 0) + (jobs.stats.Publishing ?? 0)) },
])
</script>

<template>
  <div class="grid gap-3 sm:grid-cols-[1fr_auto] sm:items-end">
    <div class="stats stats-vertical w-full border border-base-300 sm:stats-horizontal">
      <div
        v-for="t in tiles"
        :key="t.label"
        class="stat px-4 py-3"
      >
        <div class="stat-title text-xs">
          {{ t.label }}
        </div>
        <div
          class="stat-value text-2xl tabular-nums"
          :class="t.warn ? 'text-warning' : ''"
        >
          {{ t.value }}
        </div>
      </div>
    </div>
    <AgMeter
      v-if="max"
      class="w-full sm:w-48"
      label="Agent slots"
      :value="Math.min(running, max)"
      :max="max"
    />
  </div>
</template>
