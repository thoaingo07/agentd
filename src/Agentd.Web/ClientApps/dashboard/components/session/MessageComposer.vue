<script setup lang="ts">
import { computed, ref } from 'vue'
import { ApiError, send } from '../../../shared/api/http'
import { AgButton } from '../../../shared/components/ui'
import { useUiStore } from '../../stores/ui'

const props = defineProps<{ jobId: number; state: string }>()
const emit = defineEmits<{ sent: [text: string] }>()
const text = ref('')
const busy = ref(false)
const ui = useUiStore()
const enabled = computed(() => props.state === 'WaitingForHuman' || props.state === 'Running')
const hint = computed(() =>
  props.state === 'WaitingForHuman' ? 'The agent is waiting for you.' : props.state === 'Running'
        ? 'Delivered at the agent’s next step.'
        : ['Done', 'Failed', 'Cancelled'].includes(props.state)
          ? 'Job finished.'
          : 'This job isn’t taking messages.',
)

async function submit(): Promise<void> {
  const message = text.value.trim()
  if (!enabled.value || !message || busy.value) return
  busy.value = true
  try {
    await send('POST', `/api/jobs/${props.jobId}/messages`, { text: message })
    emit('sent', message)
    text.value = ''
  } catch (err) {
    ui.toast(err instanceof ApiError ? err.message : 'The message wasn’t sent.', 'error')
  } finally {
    busy.value = false
  }
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
      {{ hint }}<template v-if="enabled">
        It's also posted to the job's chat thread.
      </template>
    </p>
  </form>
</template>
