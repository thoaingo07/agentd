<script setup lang="ts">
import { ref } from 'vue'
import type { JobSummary } from '../../../shared/api/types'
import { AgButton, AgModal } from '../../../shared/components/ui'
import { useJobsStore } from '../../stores/jobs'

// Retry / Resume / Pause / Hand-off / Cancel for one run, with a confirmation for Retry and Cancel.
const props = defineProps<{ job: JobSummary }>()
const jobs = useJobsStore()
const confirming = ref<'cancel' | 'retry' | null>(null)
const final = () => ['Done', 'Failed', 'Cancelled'].includes(props.job.state)

async function act(): Promise<void> {
  const action = confirming.value
  confirming.value = null
  if (action === 'cancel') await jobs.cancel(props.job.id)
  if (action === 'retry') await jobs.retry(props.job.id)
  await jobs.refresh(props.job.id).catch(() => {})
}
</script>

<template>
  <div class="flex flex-wrap items-center gap-2">
    <AgButton
      v-if="job.state === 'Failed' || job.state === 'Cancelled'"
      size="sm"
      variant="outline"
      :loading="jobs.pending.has(job.id)"
      @click="confirming = 'retry'"
    >
      Retry
    </AgButton>
    <AgButton
      v-if="job.state === 'Paused'"
      size="sm"
      :loading="jobs.pending.has(job.id)"
      @click="jobs.resume(job.id)"
    >
      Resume
    </AgButton>
    <AgButton
      v-else-if="['Queued', 'Running', 'WaitingForHuman'].includes(job.state)"
      size="sm"
      variant="outline"
      :loading="jobs.pending.has(job.id)"
      @click="jobs.pause(job.id)"
    >
      Pause
    </AgButton>
    <AgButton
      v-if="(job.state === 'InReview' || job.state === 'Done') && job.prUrl && job.handoff === 'None'"
      size="sm"
      variant="outline"
      :loading="jobs.pending.has(job.id)"
      @click="jobs.handoff(job.id)"
    >
      Hand-off
    </AgButton>
    <AgButton
      v-if="!final()"
      size="sm"
      variant="error"
      :loading="jobs.pending.has(job.id)"
      @click="confirming = 'cancel'"
    >
      Cancel
    </AgButton>
    <AgModal
      :open="confirming !== null"
      :title="confirming === 'retry' ? 'Retry this job?' : 'Cancel this job?'"
      @update:open="(v: boolean) => !v && (confirming = null)"
    >
      {{ confirming === 'retry' ? 'The job is queued again as a new attempt.' : 'The agent stops and the job ends as Cancelled.' }}
      <template #actions>
        <AgButton
          variant="ghost"
          @click="confirming = null"
        >
          Back
        </AgButton>
        <AgButton
          :variant="confirming === 'retry' ? 'primary' : 'error'"
          @click="act"
        >
          {{ confirming === 'retry' ? 'Retry' : 'Cancel job' }}
        </AgButton>
      </template>
    </AgModal>
  </div>
</template>
