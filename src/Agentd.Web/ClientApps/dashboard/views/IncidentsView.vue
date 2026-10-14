<script setup lang="ts">
// Incidents (docs/architect/incident-monitor.md §4): the errors the monitor found in Application Insights, one per bug,
// by status and app. Each opens its page: the alert, the investigation, occurrences and the thread's actions.
import { computed, onMounted, ref } from 'vue'
import { ApiError } from '../../shared/api/http'
import { ago } from '../../shared/utils/format'
import IncidentStatusBadge from '../components/IncidentStatusBadge.vue'
import { useIncidentsStore } from '../stores/incidents'

const incidents = useIncidentsStore()
const error = ref<string | null>(null)
onMounted(() => void incidents.load().catch((err) => (error.value = err instanceof ApiError ? err.message : 'The incidents weren’t read.')))

type Filter = 'active' | 'Resolved' | 'Ignored' | 'all'
const filters: { value: Filter; label: string }[] = [
  { value: 'active', label: 'Active' },
  { value: 'Resolved', label: 'Resolved' },
  { value: 'Ignored', label: 'Ignored' },
  { value: 'all', label: 'All' },
]
const filter = ref<Filter>('active')
const app = ref('')
const apps = computed(() => [...new Set((incidents.list ?? []).map((i) => i.app))].sort())
/** Active: still needs someone (Open, Investigating) or is being fixed. */
const shown = computed(() => (incidents.list ?? []).filter((i) =>
  (filter.value === 'all' || (filter.value === 'active' ? !['Resolved', 'Ignored'].includes(i.status) : i.status === filter.value))
  && (!app.value || i.app === app.value)))
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <div class="grid gap-1">
      <h1 class="text-xl font-semibold">
        Incidents
      </h1>
      <p class="text-sm text-muted">
        Exceptions and failed requests from your apps' Application Insights, one per bug however often it happens. agentd investigates each;
        <strong>🛠 Fix it</strong> files a Bug that a normal job fixes.
      </p>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <div
        class="join"
        role="radiogroup"
        aria-label="Status"
      >
        <button
          v-for="f in filters"
          :key="f.value"
          type="button"
          role="radio"
          :aria-checked="filter === f.value"
          class="btn btn-sm join-item"
          :class="filter === f.value ? 'btn-primary' : ''"
          @click="filter = f.value"
        >
          {{ f.label }}
        </button>
      </div>
      <select
        v-if="apps.length > 1"
        v-model="app"
        class="select select-sm w-auto"
        aria-label="App"
      >
        <option value="">
          All apps
        </option>
        <option
          v-for="a in apps"
          :key="a"
          :value="a"
        >
          {{ a }}
        </option>
      </select>
    </div>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="incidents.list === null"
      class="text-muted"
    >
      Loading…
    </p>
    <p
      v-else-if="shown.length === 0"
      class="text-muted"
    >
      {{ incidents.list.length === 0 ? 'No incidents. With the monitor on (Agentd:Monitor), new errors land here and in the chat channel.' : 'None here.' }}
    </p>
    <ul
      v-else
      class="grid gap-2"
      aria-label="Incidents"
    >
      <li
        v-for="i in shown"
        :key="i.id"
        class="grid gap-1 rounded-box border border-base-300 p-3 text-sm transition-colors hover:bg-base-200"
        data-testid="incident-row"
      >
        <p class="flex items-start gap-2">
          <RouterLink
            :to="{ name: 'incident', params: { id: i.id } }"
            class="link link-hover min-w-0 flex-1 font-medium"
          >
            <span class="font-mono text-[13px] font-normal text-muted">#{{ i.id }}</span> {{ i.title }}<template v-if="i.operation">
              <span class="font-normal text-muted"> in {{ i.operation }}</span>
            </template>
          </RouterLink>
          <IncidentStatusBadge :status="i.status" />
        </p>
        <p
          v-if="i.message"
          class="truncate text-xs"
        >
          {{ i.message }}
        </p>
        <p class="text-xs text-muted">
          {{ i.app }} · {{ i.kind }} · {{ i.totalCount }}× · last
          <time
            :datetime="i.lastSeen"
            :title="new Date(i.lastSeen).toLocaleString()"
          >{{ ago(i.lastSeen) }}</time>
          <template v-if="i.workItemId">
            ·
            <RouterLink
              :to="{ name: 'work-item', params: { id: i.workItemId } }"
              class="link"
            >
              Bug #{{ i.workItemId }}
            </RouterLink>
          </template>
          <template v-else-if="!i.analyzed">
            · not investigated yet
          </template>
        </p>
      </li>
    </ul>
  </section>
</template>
