<script setup lang="ts">
import { ref } from 'vue'
import { ApiError, send } from '../shared/api/http'
import { AgButton } from '../shared/components/ui'

// The local page's frame: what it is, and Send (the kept and edited findings plus your comments, for your agent).
const sent = ref<string | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)

async function submit(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    sent.value = (await send<{ path: string }>('POST', '/api/reviews/1/send')).path
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="min-h-screen bg-base-100 text-base-content">
    <header class="navbar gap-3 border-b border-base-300 px-4">
      <span class="font-semibold">agentd review</span>
      <span class="text-sm text-muted">on your machine, with your claude</span>
      <span class="ml-auto" />
      <span
        v-if="sent"
        class="text-sm"
        role="status"
      >✅ Saved to <code>{{ sent }}</code>. Tell your agent "fix what's in {{ sent }}". You can close this tab.</span>
      <AgButton
        v-else
        :loading="busy"
        @click="submit"
      >
        Send to my agent
      </AgButton>
    </header>
    <p
      v-if="error"
      class="alert alert-error m-4 text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <main class="p-4 lg:p-6">
      <RouterView />
    </main>
  </div>
</template>
