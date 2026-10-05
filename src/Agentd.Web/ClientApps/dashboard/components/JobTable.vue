<script setup lang="ts">
// The job list for the Dashboard (and History, T3.11). Keyboard: j/k move between rows, Enter opens.
import { onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { AgStateBadge } from '../../shared/components/ui'
import type { JobState, JobSummary } from '../../shared/api/types'
import { useConfigStore } from '../stores/config'

/** `live: false` (History): no change flash; a Completed column instead of a ticking elapsed time. */
const props = withDefaults(defineProps<{ jobs: JobSummary[]; pending?: Set<number>; live?: boolean }>(), { pending: undefined, live: true })
const emit = defineEmits<{ cancel: [job: JobSummary]; retry: [job: JobSummary] }>()
const router = useRouter()
const config = useConfigStore()

/** Rows whose state/phase/PR just changed, highlighted for ~600 ms. */
const flashing = ref(new Set<number>())
const seen = new Map<number, string>()
const timers: ReturnType<typeof setTimeout>[] = []
watch(
  () => props.jobs.map((j) => [j.id, `${j.state}|${j.phase}|${j.prUrl}|${j.fixRounds}`] as const),
  (rows) => {
    for (const [id, signature] of rows) {
      const before = seen.get(id)
      seen.set(id, signature)
      if (!props.live || before === undefined || before === signature) continue
      flashing.value = new Set(flashing.value).add(id)
      timers.push(setTimeout(() => {
        const next = new Set(flashing.value)
        next.delete(id)
        flashing.value = next
      }, 600))
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => timers.forEach(clearTimeout))

const now = ref(Date.now())
const ticker = setInterval(() => (now.value = Date.now()), 10_000)
onBeforeUnmount(() => clearInterval(ticker))

function elapsed(job: JobSummary): string {
  const final = ['Done', 'Failed', 'Cancelled'].includes(job.state)
  const s = final ? job.elapsedSeconds : Math.max(job.elapsedSeconds, (now.value - Date.parse(job.startedAt)) / 1000)
  const m = Math.floor(s / 60)
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h ${m % 60}m`
}

function open(job: JobSummary): void {
  void router.push({ name: 'job', params: { id: job.id } })
}

function onKey(e: KeyboardEvent): void {
  const rows = [...(e.currentTarget as HTMLElement).querySelectorAll<HTMLElement>('tbody tr')]
  const i = rows.indexOf(document.activeElement as HTMLElement)
  if (e.key === 'j' || e.key === 'k') {
    e.preventDefault()
    rows[Math.min(rows.length - 1, Math.max(0, i + (e.key === 'j' ? 1 : -1)))]?.focus()
  } else if (e.key === 'Enter' && i >= 0) {
    open(props.jobs[i]!)
  }
}
</script>

<template>
  <div class="overflow-x-auto rounded-box border border-base-300">
    <table
      class="table table-sm"
      @keydown="onKey"
    >
      <thead>
        <tr>
          <th>State</th><th>Work item</th><th class="hidden md:table-cell">
            Repo
          </th><th class="hidden lg:table-cell">
            Branch
          </th><th>Phase</th><th
            v-if="!live"
            class="hidden sm:table-cell"
          >
            Completed
          </th><th class="text-right">
            {{ live ? 'Elapsed' : 'Took' }}
          </th><th><span class="sr-only">Actions</span></th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="job in jobs"
          :key="job.id"
          tabindex="0"
          class="cursor-pointer transition-colors hover:bg-base-200 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary motion-reduce:transition-none"
          :class="[job.state === 'WaitingForHuman' || job.pendingPermissions > 0 ? 'bg-warning/10' : '', flashing.has(job.id) ? 'bg-primary/10' : '']"
          :data-flash="flashing.has(job.id) || undefined"
          @click="open(job)"
        >
          <td>
            <AgStateBadge :state="job.state as JobState" />
            <span
              v-if="job.pendingPermissions > 0"
              class="badge badge-warning badge-sm ml-1"
              :title="`${job.pendingPermissions} permission request(s) waiting`"
            >🔐 {{ job.pendingPermissions }}</span>
          </td>
          <td class="max-w-72">
            <RouterLink
              :to="{ name: 'work-item', params: { id: job.workItemId } }"
              class="link font-mono text-[13px] text-muted"
              title="Every run of this work item"
              @click.stop
            >
              WI-{{ job.workItemId }}
            </RouterLink>
            <span class="block truncate">{{ job.title }}</span>
          </td>
          <td class="hidden md:table-cell">
            {{ job.repo }}
          </td>
          <td class="hidden max-w-48 truncate font-mono text-[13px] lg:table-cell">
            {{ job.branch ?? '—' }}
          </td>
          <td class="text-muted">
            {{ job.phase ?? '—' }}
          </td>
          <td
            v-if="!live"
            class="hidden whitespace-nowrap text-muted sm:table-cell"
          >
            {{ job.completedAt ? new Date(job.completedAt).toLocaleString() : '—' }}
          </td>
          <td class="text-right tabular-nums">
            {{ elapsed(job) }}
          </td>
          <td
            class="whitespace-nowrap text-right"
            @click.stop
          >
            <a
              v-if="config.workItemUrl(job.repo, job.workItemId)"
              class="btn btn-ghost btn-xs"
              :href="config.workItemUrl(job.repo, job.workItemId)!"
              target="_blank"
              rel="noopener noreferrer"
            >Work item ↗</a>
            <a
              v-if="job.prUrl"
              class="btn btn-ghost btn-xs"
              :href="job.prUrl"
              target="_blank"
              rel="noopener noreferrer"
            >PR ↗</a>
            <button
              v-if="job.state === 'Failed' || job.state === 'Cancelled'"
              type="button"
              class="btn btn-ghost btn-xs"
              :disabled="pending?.has(job.id)"
              @click="emit('retry', job)"
            >
              Retry
            </button>
            <button
              v-else-if="!['Done', 'Cancelled'].includes(job.state)"
              type="button"
              class="btn btn-ghost btn-xs text-error"
              :disabled="pending?.has(job.id)"
              @click="emit('cancel', job)"
            >
              Cancel
            </button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
