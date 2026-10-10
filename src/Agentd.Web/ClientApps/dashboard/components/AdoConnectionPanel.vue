<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError, get, send } from '../../shared/api/http'
import type { AdoConnection, AdoConnections } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { dateTime } from '../../shared/utils/format'

// Your Azure DevOps: connect once, and agentd acts as you there (your work items' commits and PRs, your !review findings).
// Connect is a full-page trip to Microsoft's sign-in (/bff/ado/connect); it comes back to /settings?ado=…. Or paste a
// personal access token (write-only: it's never shown again). The commit name and email default to your profile's.
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

const pat = ref('')
const patName = ref('')
const patEmail = ref('')
const editing = ref<string | null>(null)
const authorName = ref('')
const authorEmail = ref('')

async function run(key: string, action: () => Promise<void>): Promise<void> {
  busy.value = key
  error.value = null
  try {
    await action()
    await load()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}

function addPat(): Promise<void> {
  return run('pat', async () => {
    await send('POST', '/api/me/ado-connections/pat', { token: pat.value, commitName: patName.value, commitEmail: patEmail.value })
    pat.value = ''
  })
}

function edit(c: AdoConnection): void {
  editing.value = c.identityId
  authorName.value = c.commitName ?? ''
  authorEmail.value = c.commitEmail ?? ''
}

function saveAuthor(id: string): Promise<void> {
  return run(`author-${id}`, async () => {
    await send('PUT', `/api/me/ado-connections/${encodeURIComponent(id)}/commit-author`, { name: authorName.value, email: authorEmail.value })
    editing.value = null
  })
}

async function load(): Promise<void> {
  state.value = await get<AdoConnections>('/api/me/ado-connections')
}

function disconnect(id: string): Promise<void> {
  return run(id, () => send<void>('DELETE', `/api/me/ado-connections/${encodeURIComponent(id)}`))
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
      Connect once and agentd acts as you: commits, PRs, comments and updates for work items assigned to you, and the
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
        <span>Connected as <strong>{{ c.displayName }}</strong> ({{ c.uniqueName }}) · {{ c.kind === 'Pat' ? 'access token' : 'Microsoft' }} · {{ dateTime(c.connectedAt) }}</span>
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
        <div
          v-if="editing !== c.identityId"
          class="flex w-full flex-wrap items-center gap-2"
          data-testid="commit-author"
        >
          <span v-if="c.authorEmail">Commits as <strong>{{ c.authorName }}</strong> &lt;{{ c.authorEmail }}&gt;</span>
          <span
            v-else
            class="text-warning"
          >No commit email yet: your commits go under agentd's name.</span>
          <AgButton
            size="sm"
            variant="ghost"
            @click="edit(c)"
          >
            Change
          </AgButton>
        </div>
        <form
          v-else
          class="flex w-full flex-wrap items-end gap-2"
          @submit.prevent="saveAuthor(c.identityId)"
        >
          <label class="grid gap-1">
            <span class="text-xs">Commit name</span>
            <input
              v-model="authorName"
              class="input input-sm"
              :placeholder="c.displayName"
              maxlength="200"
            >
          </label>
          <label class="grid gap-1">
            <span class="text-xs">Commit email</span>
            <input
              v-model="authorEmail"
              type="email"
              class="input input-sm"
              :placeholder="c.uniqueName"
              maxlength="200"
            >
          </label>
          <AgButton
            size="sm"
            type="submit"
            :loading="busy === `author-${c.identityId}`"
          >
            Save
          </AgButton>
          <AgButton
            size="sm"
            variant="ghost"
            @click="editing = null"
          >
            Cancel
          </AgButton>
        </form>
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
    <form
      class="grid gap-2 rounded-box border border-base-300 p-3"
      data-testid="ado-pat"
      @submit.prevent="addPat"
    >
      <label class="grid gap-1">
        <span class="font-medium">{{ state?.available ? 'Or use a personal access token' : 'Personal access token' }}</span>
        <input
          v-model="pat"
          type="password"
          autocomplete="off"
          spellcheck="false"
          class="input w-full font-mono"
          placeholder="Paste a token from Azure DevOps → User settings → Personal access tokens"
        >
      </label>
      <p class="text-muted">
        For this organization, with <strong>Code (Read &amp; write)</strong> and <strong>Work Items (Read &amp; write)</strong>.
        agentd stores it encrypted and never shows it again; it replaces a Microsoft sign-in for the same account.
      </p>
      <div class="flex flex-wrap items-end gap-2">
        <label class="grid gap-1">
          <span class="text-xs">Commit name (optional)</span>
          <input
            v-model="patName"
            class="input input-sm"
            maxlength="200"
            placeholder="Your Azure DevOps name"
          >
        </label>
        <label class="grid gap-1">
          <span class="text-xs">Commit email (optional)</span>
          <input
            v-model="patEmail"
            type="email"
            class="input input-sm"
            maxlength="200"
            placeholder="Your sign-in email"
          >
        </label>
        <AgButton
          size="sm"
          type="submit"
          :disabled="!pat.trim()"
          :loading="busy === 'pat'"
        >
          Save token
        </AgButton>
      </div>
    </form>
    <p
      v-if="error"
      class="text-error"
      role="alert"
    >
      {{ error }}
    </p>
  </section>
</template>
