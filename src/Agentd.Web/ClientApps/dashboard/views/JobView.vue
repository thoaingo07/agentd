<script setup lang="ts">
// Minimal until the Session view (T3.10): state, links and actions for one job.
import { onMounted } from 'vue'
import type { JobState } from '../../shared/api/types'
import { AgStateBadge } from '../../shared/components/ui'
import { useJobsStore } from '../stores/jobs'

const props = defineProps<{ id: number }>()
const jobs = useJobsStore()
onMounted(() => void jobs.refresh(props.id).catch(() => {}))
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-3">
    <RouterLink
      to="/"
      class="text-sm text-muted"
    >
      ← Dashboard
    </RouterLink>
    <template v-if="jobs.byId.get(id)">
      <h1 class="text-xl font-semibold">
        {{ jobs.byId.get(id)!.title }}
      </h1>
      <p class="flex items-center gap-2 text-sm">
        <AgStateBadge :state="jobs.byId.get(id)!.state as JobState" />
        <span class="font-mono text-muted">job #{{ id }} · WI-{{ jobs.byId.get(id)!.workItemId }}</span>
      </p>
      <p class="text-sm text-muted">
        The live transcript arrives with the Session view.
      </p>
    </template>
    <p
      v-else
      class="text-sm text-muted"
    >
      Loading job #{{ id }}…
    </p>
  </section>
</template>
