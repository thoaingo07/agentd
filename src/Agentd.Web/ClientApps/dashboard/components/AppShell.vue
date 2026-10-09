<script setup lang="ts">
import { onBeforeUnmount } from 'vue'
import { AgToastHost } from '../../shared/components/ui'
import { bytes } from '../../shared/utils/format'
import { useJobsStore } from '../stores/jobs'
import { useResourcesStore } from '../stores/resources'
import { useUiStore } from '../stores/ui'
import ConnectionIndicator from './ConnectionIndicator.vue'
import ThemeToggle from './ThemeToggle.vue'

const jobs = useJobsStore()
const ui = useUiStore()
const resources = useResourcesStore()
onBeforeUnmount(resources.watch())
const links = [
  { to: '/', label: 'Dashboard' },
  { to: '/history', label: 'History' },
  { to: '/ideas', label: 'Ideas' },
  { to: '/reviews', label: 'Reviews' },
  { to: '/settings', label: 'Settings' },
]
</script>

<template>
  <div class="min-h-screen bg-base-100 text-base-content">
    <header class="navbar gap-2 border-b border-base-300 bg-base-100 px-4 lg:px-6">
      <!-- Phones: the links fold into a menu (a native <details>, no script). -->
      <details class="dropdown md:hidden">
        <summary
          class="btn btn-ghost btn-sm"
          aria-label="Menu"
        >
          ☰
        </summary>
        <ul class="menu dropdown-content z-20 mt-2 w-44 rounded-box border border-base-300 bg-base-100 p-2 shadow-sm">
          <li
            v-for="link in links"
            :key="link.to"
          >
            <RouterLink :to="link.to">
              {{ link.label }}
            </RouterLink>
          </li>
        </ul>
      </details>
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
        <nav class="hidden gap-4 text-sm md:flex">
          <RouterLink
            v-for="link in links"
            :key="link.to"
            :to="link.to"
            class="border-b-2 border-transparent py-1 text-muted hover:text-base-content"
            exact-active-class="!border-primary !text-base-content"
          >
            {{ link.label }}
          </RouterLink>
        </nav>
      </div>
      <div class="flex items-center gap-3">
        <RouterLink
          v-if="jobs.waitingCount > 0"
          :to="{ path: '/', query: { filter: 'waiting' } }"
          class="badge badge-warning gap-1"
          :aria-label="`${jobs.waitingCount} job${jobs.waitingCount === 1 ? '' : 's'} waiting for you`"
        >
          {{ jobs.waitingCount }} waiting
        </RouterLink>
        <RouterLink
          v-if="resources.machine"
          :to="{ name: 'settings' }"
          class="flex items-center gap-2 text-xs tabular-nums hover:text-base-content"
          :class="resources.machine.lowDisk || resources.machine.lowMemory ? 'font-semibold text-base-content' : 'text-muted'"
          data-testid="machine"
          :title="`Machine: CPU ${resources.machine.cpuPercent}% · RAM ${bytes(resources.machine.memoryAvailable)} free of ${bytes(resources.machine.memoryTotal)} · disk ${bytes(resources.machine.diskFree)} free of ${bytes(resources.machine.diskTotal)}`"
          :aria-label="`Host: CPU ${resources.machine.cpuPercent}%, RAM ${bytes(resources.machine.memoryAvailable)} free, disk ${bytes(resources.machine.diskFree)} free`"
        >
          {{ resources.machine.lowDisk || resources.machine.lowMemory ? '⚠️' : '' }}
          <!-- Phones: the short form; the Host panel in Settings has the rest. -->
          <span class="lg:hidden">CPU {{ resources.machine.cpuPercent }}% · {{ bytes(resources.machine.memoryAvailable) }}</span>
          <span class="hidden lg:inline">CPU {{ resources.machine.cpuPercent }}% · RAM {{ bytes(resources.machine.memoryAvailable) }} free · disk {{ bytes(resources.machine.diskFree) }} free</span>
        </RouterLink>
        <ConnectionIndicator />
        <ThemeToggle class="hidden sm:inline-flex" />
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
