<script setup lang="ts">
import type { JobDetail, JobSummary } from '../../../shared/api/types'

// One run's bookkeeping: attempts, plan, fix rounds, hand-off, the last error.
defineProps<{ job: JobSummary; detail?: JobDetail }>()
</script>

<template>
  <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
    <dt class="text-muted">
      Job
    </dt><dd class="font-mono">
      #{{ job.id }}
    </dd>
    <dt class="text-muted">
      Attempt / resumes
    </dt><dd>{{ detail?.attempt ?? '—' }} / {{ detail?.resumeCount ?? '—' }}</dd>
    <dt class="text-muted">
      Plan
    </dt><dd>{{ job.planStatus }}<span v-if="detail?.estimate"> · ~{{ detail.estimate.minutes }} min, ~{{ detail.estimate.usagePercent }}% of the 5h window</span></dd>
    <dt class="text-muted">
      Review fix rounds
    </dt><dd>{{ job.fixRounds }}</dd>
    <dt class="text-muted">
      Hand-off
    </dt><dd>{{ job.handoff }}</dd>
    <dt class="text-muted">
      Unread messages
    </dt><dd>{{ detail?.pendingMessages ?? 0 }}</dd>
    <dt class="text-muted">
      Last activity
    </dt><dd>{{ detail?.lastActivity ?? '—' }}</dd>
    <template v-if="job.lastError">
      <dt class="text-muted">
        Last error
      </dt><dd class="text-error">
        {{ job.lastError }}
      </dd>
    </template>
  </dl>
</template>
