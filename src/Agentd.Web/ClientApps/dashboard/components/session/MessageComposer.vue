<script setup lang="ts">
import { computed, ref } from 'vue'
import { AgButton } from '../../../shared/components/ui'
import { useJobsStore } from '../../stores/jobs'

const props = defineProps<{ jobId: number; state: string }>()
const emit = defineEmits<{ sent: [text: string] }>()
const text = ref('')
const busy = ref(false)
const jobs = useJobsStore()
// As in the job's chat thread: in review a message starts a fix round; once done it asks the job's agent (talk only).
const hints: Record<string, string> = {
  WaitingForHuman: 'The agent is waiting for you.',
  Running: 'Delivered at the agent’s next step.',
  InReview: 'Starts a fix round on the PR: the agent addresses it and pushes.',
  Done: 'Ask the job’s agent about the finished work: it answers under Conversation, and nothing is pushed.',
}
const enabled = computed(() => props.state in hints)
const hint = computed(() => hints[props.state] ?? (['Failed', 'Cancelled'].includes(props.state) ? 'Job finished.' : 'This job isn’t taking messages.'))

async function submit(): Promise<void> {
  const message = text.value.trim()
  if (!enabled.value || !message || busy.value) return
  busy.value = true
  const outcome = await jobs.message(props.jobId, message)
  busy.value = false
  if (outcome === null) return
  // Only a reply or a next turn shows up in the transcript (as DeveloperReplied), replacing the pending bubble.
  if (outcome === 'resumed' || outcome === 'queued') emit('sent', message)
  text.value = ''
}
</script>

<template>
  <form
    class="grid gap-1"
    @submit.prevent="submit"
  >
    <label
      for="composer"
      class="sr-only"
    >Message to the agent</label>
    <div class="flex items-end gap-2">
      <textarea
        id="composer"
        v-model="text"
        class="textarea textarea-bordered min-h-12 flex-1"
        rows="2"
        maxlength="8000"
        :disabled="!enabled"
        placeholder="Message the agent (Ctrl+Enter to send)"
        @keydown.ctrl.enter.prevent="submit"
        @keydown.meta.enter.prevent="submit"
      />
      <AgButton
        type="submit"
        :loading="busy"
        :disabled="!enabled || !text.trim()"
      >
        Send
      </AgButton>
    </div>
    <p class="text-xs text-muted">
      {{ hint }}<template v-if="state === 'WaitingForHuman' || state === 'Running'">
        It's also posted to the job's chat thread.
      </template>
    </p>
  </form>
</template>
