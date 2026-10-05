<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { AgToggleGroup } from '../../shared/components/ui'
import JobTable from '../components/JobTable.vue'
import { useConfigStore } from '../stores/config'
import { finalStates, fromQuery, pageSize, toQuery, useHistoryStore } from '../stores/history'
import { useJobsStore } from '../stores/jobs'

/** Typing in the search box waits this long before asking the server. */
const debounceMs = 300

const route = useRoute()
const router = useRouter()
const history = useHistoryStore()
const config = useConfigStore()
const jobs = useJobsStore()
const filters = reactive(fromQuery(route.query))
const stateOptions = finalStates.map((s) => ({ value: s, label: s }))
let timer: ReturnType<typeof setTimeout> | undefined

const total = computed(() => history.result?.total ?? 0)
const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize)))
const totalLabel = computed(() => `${total.value.toLocaleString()}${history.result?.totalCapped ? '+' : ''} job${total.value === 1 ? '' : 's'}`)

function apply(): void {
  void router.replace({ query: toQuery(filters) })
}

/** Any filter change goes back to page 1; text waits for a pause in typing. */
// A string key: Vue compares watcher results by identity, and a fresh array would always look changed.
watch(() => [filters.states.join(','), filters.repo, filters.from, filters.to].join('|'), () => {
  filters.page = 1
  apply()
})
watch(() => filters.q, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    filters.page = 1
    apply()
  }, debounceMs)
})

/** The URL is the source of truth: back/forward and reloads land on the same results. */
watch(
  () => route.query,
  (query) => {
    Object.assign(filters, fromQuery(query))
    void history.load(filters)
  },
  { immediate: true },
)

function go(page: number): void {
  filters.page = Math.min(Math.max(1, page), pages.value)
  apply()
}

onMounted(() => void config.load().catch(() => {}))
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <section class="mx-auto grid max-w-6xl gap-4">
    <div class="flex flex-wrap items-baseline justify-between gap-2">
      <h1 class="text-xl font-semibold">
        History
      </h1>
      <span class="text-sm text-muted tabular-nums">{{ totalLabel }}</span>
    </div>
    <div class="flex flex-wrap items-end gap-2">
      <AgToggleGroup
        v-model="filters.states"
        label="States"
        :options="stateOptions"
      />
      <select
        v-model="filters.repo"
        class="select select-sm select-bordered w-40"
        aria-label="Repository"
      >
        <option value="">
          All repositories
        </option>
        <option
          v-for="r in config.config?.repositories ?? []"
          :key="r.name"
          :value="r.name"
        >
          {{ r.name }}
        </option>
      </select>
      <label class="grid text-xs text-muted">From<input
        v-model="filters.from"
        type="date"
        class="input input-sm input-bordered"
        :max="filters.to || undefined"
      ></label>
      <label class="grid text-xs text-muted">To<input
        v-model="filters.to"
        type="date"
        class="input input-sm input-bordered"
        :min="filters.from || undefined"
      ></label>
      <input
        v-model="filters.q"
        type="search"
        class="input input-sm input-bordered w-full sm:ml-auto sm:w-64"
        placeholder="Title or WI-1234"
        aria-label="Search history"
      >
    </div>

    <div
      v-if="history.error"
      class="alert alert-error alert-soft text-sm"
      role="alert"
    >
      {{ history.error }}
    </div>
    <JobTable
      v-else-if="history.result?.items.length"
      :jobs="history.result.items"
      :pending="jobs.pending"
      :live="false"
      @retry="jobs.retry($event.id)"
    />
    <div
      v-else-if="!history.loading"
      class="alert text-sm"
    >
      No finished jobs match these filters.
    </div>

    <div
      v-if="pages > 1"
      class="join justify-self-center"
    >
      <button
        type="button"
        class="btn btn-sm join-item"
        :disabled="filters.page <= 1"
        aria-label="Previous page"
        @click="go(filters.page - 1)"
      >
        «
      </button>
      <span class="btn btn-sm join-item pointer-events-none tabular-nums">Page {{ filters.page }} of {{ pages }}{{ history.result?.totalCapped ? '+' : '' }}</span>
      <button
        type="button"
        class="btn btn-sm join-item"
        :disabled="filters.page >= pages"
        aria-label="Next page"
        @click="go(filters.page + 1)"
      >
        »
      </button>
    </div>
  </section>
</template>
