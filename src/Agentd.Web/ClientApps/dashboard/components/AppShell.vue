<script setup lang="ts">
import UNavigationMenu from '@nuxt/ui/components/NavigationMenu.vue'
import { computed, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { AgToastHost } from '../../shared/components/ui'
import { bytes } from '../../shared/utils/format'
import { useInboxStore } from '../stores/inbox'
import { watchInbox } from '../stores/inboxNotifications'
import { useJobsStore } from '../stores/jobs'
import { useResourcesStore } from '../stores/resources'
import { useUiStore } from '../stores/ui'
import ConnectionIndicator from './ConnectionIndicator.vue'
import ThemeToggle from './ThemeToggle.vue'

const jobs = useJobsStore()
const inbox = useInboxStore()
void inbox.loadCounts().catch(() => {})
const ui = useUiStore()
const resources = useResourcesStore()
onBeforeUnmount(resources.watch())
onBeforeUnmount(watchInbox(useRouter()))
const route = useRoute()
const links = [
  { to: '/', label: 'Dashboard', icon: 'i-lucide-layout-dashboard' },
  { to: '/inbox', label: 'Inbox', icon: 'i-lucide-inbox' },
  { to: '/history', label: 'History', icon: 'i-lucide-history' },
  { to: '/ideas', label: 'Ideas', icon: 'i-lucide-lightbulb' },
  { to: '/chats', label: 'Chat', icon: 'i-lucide-message-circle' },
  { to: '/reviews', label: 'Reviews', icon: 'i-lucide-git-pull-request' },
  { to: '/incidents', label: 'Incidents', icon: 'i-lucide-siren' },
  { to: '/artifacts', label: 'Artifacts', icon: 'i-lucide-file-text' },
  { to: '/settings', label: 'Settings', icon: 'i-lucide-settings' },
]
/** A section stays highlighted on its pages too (/reviews/7 → Reviews); the dashboard only on itself. */
const isActive = (to: string) => (to === '/' ? route.path === '/' : route.path === to || route.path.startsWith(`${to}/`))
// The active item: bold on a visible background (Nuxt UI's own highlight is too faint on agentd's base colors).
const items = computed(() => links.map((l) => ({
  ...l,
  active: isActive(l.to),
  class: isActive(l.to) ? 'font-semibold before:bg-base-300' : undefined,
  // The Inbox shows how many threads wait on you (or, when none do, how many have unread messages).
  badge: l.to === '/inbox' && (inbox.counts.waiting || inbox.counts.unread)
    ? { label: String(inbox.counts.waiting || inbox.counts.unread), color: inbox.counts.waiting ? 'warning' as const : 'neutral' as const, variant: 'solid' as const, size: 'sm' as const }
    : undefined,
})))
</script>

<template>
  <!-- A vertical menu on the left from md up; phones keep a top bar with the links folded into ☰. -->
  <div class="min-h-screen bg-base-100 text-base-content md:flex">
    <aside class="flex items-center gap-2 border-b border-base-300 bg-base-100 px-4 py-2 md:sticky md:top-0 md:h-screen md:w-56 md:shrink-0 md:flex-col md:items-stretch md:gap-4 md:overflow-y-auto md:border-r md:border-b-0 md:px-3 md:py-4">
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
      <RouterLink
        to="/"
        class="flex items-center gap-2 font-semibold md:px-3"
      >
        <span
          class="size-3 rounded-full bg-primary"
          aria-hidden="true"
        />
        agentd
      </RouterLink>
      <!-- Nuxt UI's menu (docs/ui §3), neutral: agentd green text is too light to read on the page background. -->
      <UNavigationMenu
        :items="items"
        orientation="vertical"
        color="neutral"
        class="hidden md:flex"
        aria-label="Main"
      />
      <div class="ml-auto flex items-center gap-3 md:mt-auto md:ml-0 md:flex-col md:items-start md:gap-2 md:px-3">
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
          class="flex flex-wrap items-center gap-x-2 text-xs tabular-nums hover:text-base-content"
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
    </aside>
    <main class="min-w-0 flex-1 p-4 lg:p-6">
      <slot />
    </main>
    <AgToastHost
      :toasts="ui.toasts"
      @dismiss="ui.dismiss"
    />
  </div>
</template>
