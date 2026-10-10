<script setup lang="ts">
import { computed, ref } from 'vue'
import type { AgentEvent, JobSummary } from '../../../shared/api/types'
import EventList from '../session/EventList.vue'
import MessageComposer from '../session/MessageComposer.vue'
import { useEventsStore } from '../../stores/events'
import { useJobsStore } from '../../stores/jobs'

// One run's full transcript: the agent's text and tool calls, lifecycle steps and messages, live while it runs,
// with the composer to message the agent.
const props = defineProps<{ job: JobSummary }>()
const events = useEventsStore()
const jobs = useJobsStore()
const pendingReplies = ref<string[]>([])
/** An option of the question the job waits on: sent like typing it (plan approval, close-out, hand-off, the agent's own). */
async function answer(option: string): Promise<void> {
  if (await jobs.message(props.job.id, option) === 'resumed') pendingReplies.value.push(option)
}
const eventWindow = computed(() => events.windows.get(props.job.id))
/** The agent's replies already in the transcript replace the optimistic ones. */
const shownReplies = computed(() => {
  const arrived = new Set((eventWindow.value?.events ?? []).filter((e: AgentEvent) => e.type === 'DeveloperReplied').map((e) => String((e.payload as Record<string, unknown>).reply ?? '')))
  return pendingReplies.value.filter((r) => !arrived.has(r))
})
</script>

<template>
  <div class="grid gap-3">
    <EventList
      v-if="eventWindow"
      :events="eventWindow.events"
      :has-more="eventWindow.hasMore"
      :load-earlier="() => events.loadEarlier(job.id)"
      :waiting="job.state === 'WaitingForHuman'"
      @answer="answer"
    />
    <p
      v-else
      class="text-sm text-muted"
    >
      Loading the transcript…
    </p>
    <div
      v-for="r in shownReplies"
      :key="r"
      class="chat chat-end opacity-60"
    >
      <div class="chat-header text-xs text-muted">
        you · sending
      </div>
      <div class="chat-bubble bg-primary/15 text-base-content">
        {{ r }}
      </div>
    </div>
    <MessageComposer
      :job-id="job.id"
      :state="job.state"
      @sent="pendingReplies.push($event)"
    />
  </div>
</template>
