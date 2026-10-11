<script setup lang="ts">
// Settings → System log (Admins): the daemon's own log, newest first, re-read every few seconds while you're at the
// top; older pages load on demand. Entries name their job when they have one.
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { SystemLogEntry, SystemLogPage } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { dateTime } from '../../shared/utils/format'

/** How often the newest entries are re-read while no older page is open. */
const systemLogPollMs = 5000

const entries = ref<SystemLogEntry[]>([])
const hasMore = ref(false)
const olderLoaded = ref(false)
const error = ref<string | null>(null)
const filter = ref({ level: 'Information', category: '', q: '' })
const levels = ['Information', 'Warning', 'Error', 'Critical'] as const
const tone: Record<string, string> = { Warning: 'badge-warning', Error: 'badge-error', Critical: 'badge-error' }
let timer: ReturnType<typeof setInterval> | undefined
let debounce: ReturnType<typeof setTimeout> | undefined

async function page(before?: number): Promise<SystemLogPage> {
  const params = new URLSearchParams({ level: filter.value.level, limit: '100' })
  if (filter.value.category.trim()) params.set('category', filter.value.category.trim())
  if (filter.value.q.trim()) params.set('q', filter.value.q.trim())
  if (before !== undefined) params.set('before', String(before))
  return get<SystemLogPage>(`/api/system-log?${params}`)
}

async function load(): Promise<void> {
  try {
    const p = await page()
    entries.value = p.entries
    hasMore.value = p.hasMore
    olderLoaded.value = false
    error.value = null
  } catch (err) {
    error.value = err instanceof ApiError && err.status === 403 ? 'Only Admins can read the system log.' : 'Couldn\'t load the system log.'
  }
}

async function older(): Promise<void> {
  const last = entries.value.at(-1)
  if (!last) return
  const p = await page(last.id)
  entries.value = [...entries.value, ...p.entries]
  hasMore.value = p.hasMore
  olderLoaded.value = true
}

watch(filter, () => {
  clearTimeout(debounce)
  debounce = setTimeout(() => void load(), 300)
}, { deep: true })

onMounted(() => {
  void load()
  timer = setInterval(() => {
    if (!olderLoaded.value && document.visibilityState !== 'hidden') void load()
  }, systemLogPollMs)
})
onBeforeUnmount(() => {
  clearInterval(timer)
  clearTimeout(debounce)
})

/** The logger's last name part (Agentd.Application.Jobs.Scheduler → Scheduler); the full name is the tooltip. */
const source = (category: string) => category.split('.').at(-1) ?? category
</script>

<template>
  <section class="grid gap-4">
    <h1 class="text-xl font-semibold">
      System log
    </h1>
    <p class="text-sm text-muted">
      agentd's own log: what it did and what went wrong. Kept 14 days by default (<code class="font-mono">SystemLog:RetentionDays</code>).
    </p>
    <form
      class="flex flex-wrap items-center gap-2"
      aria-label="Filter the log"
      @submit.prevent="load"
    >
      <select
        v-model="filter.level"
        class="select select-sm w-auto"
        aria-label="Level"
      >
        <option
          v-for="l in levels"
          :key="l"
          :value="l"
        >
          {{ l }} and above
        </option>
      </select>
      <input
        v-model="filter.category"
        class="input input-sm w-40"
        placeholder="Source"
        aria-label="Source"
      >
      <input
        v-model="filter.q"
        class="input input-sm min-w-0 flex-1"
        placeholder="Search messages"
        aria-label="Search messages"
      >
    </form>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!entries.length"
      class="text-muted"
    >
      Nothing logged.
    </p>
    <ol
      class="grid gap-1 font-mono text-xs"
      aria-label="Log entries"
    >
      <li
        v-for="e in entries"
        :key="e.id"
        class="grid gap-1 rounded-box border border-base-300 p-2"
        data-testid="log-entry"
      >
        <p class="flex flex-wrap items-center gap-2">
          <span class="text-muted">{{ dateTime(e.at) }}</span>
          <span
            class="badge badge-sm"
            :class="tone[e.level] ?? 'badge-ghost'"
          >{{ e.level }}</span>
          <span
            class="text-muted"
            :title="e.category"
          >{{ source(e.category) }}</span>
          <RouterLink
            v-if="e.jobId"
            :to="{ name: 'job', params: { id: e.jobId } }"
            class="link"
          >
            job #{{ e.jobId }}
          </RouterLink>
        </p>
        <p class="whitespace-pre-wrap break-words font-sans text-sm">
          {{ e.message }}
        </p>
        <details v-if="e.exception">
          <summary class="cursor-pointer text-muted">
            Exception
          </summary>
          <pre class="mt-1 overflow-x-auto whitespace-pre-wrap">{{ e.exception }}</pre>
        </details>
      </li>
    </ol>
    <div v-if="hasMore">
      <AgButton
        size="sm"
        variant="ghost"
        @click="older"
      >
        Older entries
      </AgButton>
    </div>
  </section>
</template>
