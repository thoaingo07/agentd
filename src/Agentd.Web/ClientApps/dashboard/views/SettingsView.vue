<script setup lang="ts">
import { onMounted } from 'vue'
import { useConfigStore } from '../stores/config'
import { useConnectionStore } from '../stores/connection'
import { useSessionStore } from '../stores/session'
import ThemeToggle from '../components/ThemeToggle.vue'

const config = useConfigStore()
const connection = useConnectionStore()
const session = useSessionStore()
onMounted(() => void config.load().catch(() => {}))
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-6">
    <h1 class="text-xl font-semibold">
      Settings
    </h1>
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
  </section>
</template>
