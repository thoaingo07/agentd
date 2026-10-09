<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useConfigStore } from '../stores/config'
import { useConnectionStore } from '../stores/connection'
import { usePermissionsStore } from '../stores/permissions'
import { useSessionStore } from '../stores/session'
import HostPanel from '../components/HostPanel.vue'
import ThemeToggle from '../components/ThemeToggle.vue'
import { settingsAreas } from '../settingsAreas'

const config = useConfigStore()
const connection = useConnectionStore()
const session = useSessionStore()
const permissions = usePermissionsStore()
const isAdmin = computed(() => session.user?.roles.includes('Admin') ?? false)
onMounted(() => {
  void config.load().catch(() => {})
  void permissions.load().catch(() => {})
})
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-6">
    <h1 class="text-xl font-semibold">
      Settings
    </h1>
    <HostPanel />
    <nav
      v-if="isAdmin"
      aria-labelledby="settings-areas"
      class="grid gap-2"
    >
      <h2
        id="settings-areas"
        class="text-base font-semibold"
      >
        Configuration
      </h2>
      <ul class="flex flex-wrap gap-2">
        <li
          v-for="area in settingsAreas"
          :key="area.name"
        >
          <RouterLink
            :to="{ name: 'settings-area', params: { area: area.name } }"
            class="btn btn-sm btn-outline"
          >
            {{ area.title }}
          </RouterLink>
        </li>
      </ul>
    </nav>
    <div class="grid gap-2">
      <h2 class="text-base font-semibold">
        Appearance
      </h2>
      <ThemeToggle />
    </div>
    <div class="grid gap-1 text-sm">
      <h2 class="text-base font-semibold">
        Session &amp; connection
      </h2>
      <p>Signed in as <strong>{{ session.user?.name ?? '…' }}</strong> ({{ session.user?.provider ?? '…' }})</p>
      <p>Live updates: {{ connection.status }}</p>
      <p
        v-for="[stream, seq] in connection.subscriptions"
        :key="stream"
        class="font-mono text-[13px] text-muted"
      >
        {{ stream }} @ {{ seq }}
      </p>
    </div>
    <div
      v-if="config.config"
      class="grid gap-2 text-sm"
    >
      <h2 class="text-base font-semibold">
        Daemon (read-only)
      </h2>
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt class="text-muted">
          Work item tag
        </dt><dd class="font-mono">
          {{ config.config.tag }}
        </dd>
        <dt class="text-muted">
          Poll interval
        </dt><dd>{{ config.config.pollIntervalSeconds }} s</dd>
        <dt class="text-muted">
          Max concurrent jobs
        </dt><dd>{{ config.config.maxConcurrent }}</dd>
        <dt class="text-muted">
          Plan approval
        </dt><dd>{{ config.config.requirePlanApproval ? 'on' : 'off' }}</dd>
        <dt class="text-muted">
          Review loop / hand-off
        </dt><dd>{{ config.config.reviewLoop ? 'on' : 'off' }} / {{ config.config.handoff ? 'on' : 'off' }}</dd>
        <dt class="text-muted">
          Chat
        </dt><dd>{{ config.config.messagingProviders.join(', ') || 'none' }}</dd>
      </dl>
      <h3 class="mt-2 font-semibold">
        Repositories
      </h3>
      <ul class="list-inside list-disc">
        <li
          v-for="r in config.config.repositories"
          :key="r.name"
        >
          <span class="font-mono">{{ r.name }}</span> — {{ r.organization }}/{{ r.project }} ({{ r.baseBranch }})
        </li>
      </ul>
      <p class="text-xs text-muted">
        Change these in <code class="font-mono">~/.agentd/config/agentd.json</code>.
      </p>
    </div>
    <div class="grid gap-2 text-sm">
      <h2 class="text-base font-semibold">
        Remembered permissions
      </h2>
      <p class="text-xs text-muted">
        Tool calls people allowed "for this job" or "always in this repo". Revoking one makes the agent ask again{{ isAdmin ? '' : ' (Admins revoke)' }}.
      </p>
      <p
        v-if="permissions.rules.length === 0"
        class="text-muted"
      >
        None yet.
      </p>
      <ul
        v-else
        class="divide-y divide-base-300 rounded-box border border-base-300"
      >
        <li
          v-for="r in permissions.rules"
          :key="r.id"
          class="flex flex-wrap items-center gap-2 px-3 py-2"
          data-testid="permission-rule"
        >
          <code class="font-mono text-[13px]">{{ r.ruleKey }}</code>
          <span class="badge badge-ghost badge-sm">{{ r.jobId == null ? `always in ${r.repo}` : `job #${r.jobId}` }}</span>
          <span class="text-xs text-muted">by {{ r.createdBy }}, {{ new Date(r.createdAt).toLocaleString() }}</span>
          <button
            v-if="isAdmin"
            type="button"
            class="btn btn-ghost btn-xs ml-auto text-error"
            :disabled="permissions.revoking.has(r.id)"
            @click="permissions.revoke(r.id)"
          >
            Revoke
          </button>
        </li>
      </ul>
    </div>
  </section>
</template>
