<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, send } from '../../shared/api/http'
import type { RunAccepted } from '../../shared/api/types'
import { AgButton, AgModal } from '../../shared/components/ui'
import { useUiStore } from '../stores/ui'

const open = defineModel<boolean>('open', { default: false })
const value = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const router = useRouter()
const ui = useUiStore()

/** A positive whole number, nothing else. */
const id = computed(() => (/^[1-9]\d{0,9}$/.test(value.value.trim()) ? Number(value.value.trim()) : null))

async function run(): Promise<void> {
  if (id.value === null) {
    error.value = 'Enter a work item number, like 5613.'
    return
  }
  busy.value = true
  error.value = null
  try {
    const accepted = await send<RunAccepted>('POST', `/api/workitems/${id.value}/run`)
    ui.toast(`Queued job #${accepted.jobId} for work item #${id.value}.`, 'success')
    open.value = false
    value.value = ''
    await router.push({ name: 'job', params: { id: accepted.jobId } })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Could not start the work item.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <AgModal
    v-model:open="open"
    title="Run work item"
  >
    <form
      id="run-work-item"
      class="grid gap-2"
      novalidate
      @submit.prevent="run"
    >
      <label
        class="text-sm"
        for="wi-id"
      >Azure DevOps work item</label>
      <input
        id="wi-id"
        v-model="value"
        class="input input-bordered w-full font-mono"
        inputmode="numeric"
        placeholder="5613"
        autocomplete="off"
        :aria-invalid="error ? 'true' : undefined"
        aria-describedby="wi-error"
      >
      <p
        id="wi-error"
        class="min-h-5 text-xs text-error"
        role="alert"
      >
        {{ error }}
      </p>
    </form>
    <template #actions>
      <AgButton
        variant="ghost"
        @click="open = false"
      >
        Close
      </AgButton>
      <AgButton
        type="submit"
        form="run-work-item"
        :loading="busy"
      >
        Run
      </AgButton>
    </template>
  </AgModal>
</template>
