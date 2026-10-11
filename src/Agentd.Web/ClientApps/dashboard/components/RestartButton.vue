<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { RestartStatus } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'

// Settings → Restart now (Admins): agentd stops and its supervisor (the container's restart policy, the systemd unit)
// starts it again; the page reloads once agentd answers again. Without a supervisor it says how to restart instead.
const pollMs = 2000
const maxPolls = 90   // 3 minutes
const status = ref<RestartStatus | null>(null)
const state = ref<'idle' | 'restarting' | 'slow'>('idle')
const error = ref<string | null>(null)
onMounted(async () => {
  try {
    status.value = await get<RestartStatus>('/api/settings/restart')
  } catch {
    status.value = { available: false, reason: null }
  }
})

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

async function answers(): Promise<boolean> {
  try {
    await get<RestartStatus>('/api/settings/restart')
    return true
  } catch {
    return false
  }
}

async function restart(): Promise<void> {
  if (!confirm('Restart agentd now? Running agents stop and pick up where they were once it\'s back.')) return
  error.value = null
  try {
    await send('POST', '/api/settings/restart')
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
    return
  }
  state.value = 'restarting'
  // It answers for a moment before stopping: wait until it went away, then until it's back.
  let down = false
  for (let i = 0; i < maxPolls; i++) {
    await wait(pollMs)
    const ok = await answers()
    if (!ok) down = true
    else if (down || i >= 5) {
      location.reload()
      return
    }
  }
  state.value = 'slow'
}
</script>

<template>
  <div
    v-if="status"
    class="grid gap-1 text-sm"
  >
    <div
      v-if="status.available"
      class="flex flex-wrap items-center gap-3"
    >
      <AgButton
        size="sm"
        :loading="state === 'restarting'"
        :disabled="state !== 'idle'"
        @click="restart"
      >
        Restart now
      </AgButton>
      <span
        v-if="state === 'restarting'"
        role="status"
      >Restarting agentd… this page reloads when it's back.</span>
      <span
        v-else-if="state === 'slow'"
        role="alert"
      >agentd isn't back yet: check its logs (<code>docker compose logs agentd</code>).</span>
    </div>
    <p v-else>
      To restart, run <code>agentd daemon restart</code> or restart its container.<template v-if="status.reason">
        {{ status.reason }}
      </template>
    </p>
    <p
      v-if="error"
      role="alert"
      class="text-error"
    >
      {{ error }}
    </p>
  </div>
</template>
