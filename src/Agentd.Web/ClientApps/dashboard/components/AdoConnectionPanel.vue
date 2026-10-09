<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, get, send } from '../../shared/api/http'
import type { AdoConnections } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { dateTime } from '../../shared/utils/format'

// Your Azure DevOps: connect once, and agentd acts as you there (your work items' PRs, your !review findings).
// Connect is a full-page trip to Microsoft's sign-in (/bff/ado/connect); it comes back to /settings?ado=….
const route = useRoute()
const state = ref<AdoConnections | null>(null)
const busy = ref<string | null>(null)
const error = ref<string | null>(null)
const outcome = computed(() => {
  const ado = String(route.query.ado ?? '')
  const reason = String(route.query.reason ?? '')
  if (ado === 'connected') return { ok: true, text: 'Connected. agentd will act as you in Azure DevOps.' }
  if (ado === 'error') return { ok: false, text: reason || 'The sign-in didn\'t work.' }
  if (ado === 'unavailable') return { ok: false, text: 'Not set up yet: an Admin sets up the Entra app in Settings → Azure DevOps (Service principal).' }
  return null
})

async function load(): Promise<void> {
  state.value = await get<AdoConnections>('/api/me/ado-connections')
}

async function disconnect(id: string): Promise<void> {
  busy.value = id
  error.value = null
  try {
    await send<void>('DELETE', `/api/me/ado-connections/${encodeURIComponent(id)}`)
    await load()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}

onMounted(() => void load().catch(() => {}))
</script>

<template>
  <section
    aria-labelledby="ado-connection-title"
    class="grid gap-2 text-sm"
  >
    <h2
      id="ado-connection-title"
      class="text-base font-semibold"
    >
      Your Azure DevOps
    </h2>
    <p class="text-muted">
      Connect once and agentd acts as you: PRs, comments and updates for work items assigned to you, and the
      <code>!review</code> findings you post, show your name. Otherwise they show agentd's.
    </p>
    <div
      v-if="outcome"
      :class="outcome.ok ? 'alert alert-success' : 'alert alert-warning'"
      role="status"
    >
      {{ outcome.text }}
    </div>
    <ul
      v-if="state?.connections.length"
      class="grid gap-2"
    >
      <li
        v-for="c in state.connections"
        :key="c.identityId"
        class="flex flex-wrap items-center gap-3 rounded-box border border-base-300 p-3"
        data-testid="ado-connection"
      >
        <span>Connected as <strong>{{ c.displayName }}</strong> ({{ c.uniqueName }}) · {{ dateTime(c.connectedAt) }}</span>
        <span
          v-if="c.failed"
          class="badge badge-warning badge-sm"
          :title="c.lastError ?? undefined"
        >needs reconnecting</span>
        <AgButton
          size="sm"
          variant="outline"
          class="ml-auto"
          :loading="busy === c.identityId"
          @click="disconnect(c.identityId)"
        >
          Disconnect
        </AgButton>
      </li>
    </ul>
    <div v-if="state?.available">
      <a
        class="btn btn-sm btn-primary"
        href="/bff/ado/connect"
      >{{ state.connections.length ? 'Reconnect' : 'Connect' }} with Microsoft</a>
    </div>
    <p
      v-else-if="state"
      class="text-muted"
    >
      Not available yet: an Admin sets up the Entra app first (Settings → Azure DevOps → Service principal).
    </p>
    <p
      v-if="error"
      class="text-error"
      role="alert"
    >
      {{ error }}
    </p>
  </section>
</template>
