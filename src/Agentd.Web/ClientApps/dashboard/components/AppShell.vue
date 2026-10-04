<script setup lang="ts">
import { computed } from 'vue'
import { AgToastHost } from '../../shared/components/ui'
import { useConnectionStore } from '../stores/connection'
import { useJobsStore } from '../stores/jobs'
import { useUiStore } from '../stores/ui'
import ThemeToggle from './ThemeToggle.vue'

const connection = useConnectionStore()
const jobs = useJobsStore()
const ui = useUiStore()
const live = computed(() => ({
  connecting: { label: 'Connecting…', dot: 'bg-base-300' },
  live: { label: 'Live', dot: 'bg-primary' },
  reconnecting: { label: 'Reconnecting…', dot: 'bg-warning' },
  offline: { label: 'Offline', dot: 'bg-error' },
})[connection.status])
</script>

<template>
  <div class="min-h-screen bg-base-100 text-base-content">
    <header class="navbar border-b border-base-300 bg-base-100 px-4 lg:px-6">
      <div class="flex flex-1 items-center gap-6">
        <RouterLink
          to="/"
          class="flex items-center gap-2 font-semibold"
        >
          <span
            class="size-3 rounded-full bg-primary"
            aria-hidden="true"
          />
          agentd
        </RouterLink>
        <nav class="flex gap-4 text-sm">
          <RouterLink
            to="/"
            class="border-b-2 border-transparent py-1 text-muted hover:text-base-content"
            active-class="!border-primary !text-base-content"
          >
            Dashboard
            <span
              v-if="jobs.waitingCount > 0"
              class="badge badge-warning badge-xs ml-1"
              :aria-label="`${jobs.waitingCount} waiting for you`"
            >{{ jobs.waitingCount }}</span>
          </RouterLink>
        </nav>
      </div>
      <div class="flex items-center gap-4">
        <span
          class="flex items-center gap-2 text-xs text-muted"
          role="status"
        >
          <span
            class="size-2 rounded-full"
            :class="live.dot"
            aria-hidden="true"
          />
          {{ live.label }}
        </span>
        <ThemeToggle />
      </div>
    </header>
    <main class="p-4 lg:p-6">
      <slot />
    </main>
    <AgToastHost
      :toasts="ui.toasts"
      @dismiss="ui.dismiss"
    />
  </div>
</template>
