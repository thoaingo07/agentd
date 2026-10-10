<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { get } from '../../shared/api/http'
import type { IdeaSummary, JobState, JobSummary } from '../../shared/api/types'
import { AgStateBadge, AgTabs } from '../../shared/components/ui'
import { bytes, duration, percent } from '../../shared/utils/format'
import PermissionBanner from '../components/session/PermissionBanner.vue'
import RunActions from '../components/run/RunActions.vue'
import RunDetails from '../components/run/RunDetails.vue'
import RunDiff from '../components/run/RunDiff.vue'
import RunTranscript from '../components/run/RunTranscript.vue'
import ConversationTab from '../components/workitem/ConversationTab.vue'
import PlanTab from '../components/workitem/PlanTab.vue'
import TimelineTab from '../components/workitem/TimelineTab.vue'
import { label } from '../components/workitem/sections'
import { useConfigStore } from '../stores/config'
import { useEventsStore } from '../stores/events'
import { useJobsStore } from '../stores/jobs'
import { useResourcesStore } from '../stores/resources'
import { useWorkItemsStore } from '../stores/workItems'

// Everything about one work item on one page: its runs (the transcript with tool calls, the diff, and the actions of
// the run picked, ?run=<job id>), the timeline across runs, the chat, its PRs, and plan & usage.
const props = defineProps<{ id: number }>()
const store = useWorkItemsStore()
const config = useConfigStore()
const jobs = useJobsStore()
const events = useEventsStore()
const resources = useResourcesStore()
const route = useRoute()
const router = useRouter()
onBeforeUnmount(resources.watch())
const tab = ref('transcript')
const tabs = [
  { value: 'transcript', label: 'Transcript', keepMounted: true },
  { value: 'diff', label: 'Diff' },
  { value: 'timeline', label: 'Timeline' },
  { value: 'conversation', label: 'Conversation' },
  { value: 'prs', label: 'Pull requests' },
  { value: 'details', label: 'Details' },
]
const now = ref(Date.now())
const ticker = setInterval(() => (now.value = Date.now()), 1000)
const finalStates = ['Done', 'Failed', 'Cancelled']

const active = computed(() => store.summary?.jobs.find((j) => !finalStates.includes(j.state)))
/** The run shown: ?run=<job id> when it's one of this work item's, else the active one, else the latest. */
const runId = computed(() => {
  const asked = Number(route.query.run)
  const runs = store.summary?.jobs ?? []
  return runs.some((j) => j.id === asked) ? asked : (active.value ?? runs.at(-1))?.id
})
/** Live state from the jobs store (the dashboard stream) when it has it. */
const run = computed<JobSummary | undefined>(() => {
  const id = runId.value
  return id === undefined ? undefined : jobs.byId.get(id) ?? jobs.details.get(id)?.job ?? store.summary?.jobs.find((j) => j.id === id)
})
const detail = computed(() => (runId.value === undefined ? undefined : jobs.details.get(runId.value) ?? store.details.get(runId.value)))
const runFinal = computed(() => finalStates.includes(run.value?.state ?? ''))
const elapsed = computed(() => (run.value ? (runFinal.value ? run.value.elapsedSeconds : (now.value - Date.parse(run.value.startedAt)) / 1000) : 0))
const used = computed(() => (runId.value === undefined ? undefined : resources.byJob.get(runId.value)))
const runEvents = computed(() => (runId.value === undefined ? [] : events.windows.get(runId.value)?.events ?? []))
const sessionId = computed(() => {
  const session = [...runEvents.value].reverse().find((e) => e.type === 'agent.session')
  return session ? String((session.payload as Record<string, unknown>).sessionId ?? '') : ''
})
/** The latest step the agent started: "🔨 Implement · deepseek · deepseek-flash" (step.started events). */
const currentStep = computed(() => {
  const started = [...runEvents.value].reverse().find((e) => e.type === 'step.started')
  return started ? String((started.payload as Record<string, unknown>).line ?? '') : ''
})
const span = computed(() => (store.summary ? (Date.parse(store.summary.lastActivityAt) - Date.parse(store.summary.firstSeenAt)) / 1000 : 0))
const jobOf = (jobId: number) => store.summary?.jobs.findIndex((j) => j.id === jobId) ?? -1

function pick(jobId: number): void {
  void router.replace({ query: { ...route.query, run: String(jobId) } })
}

async function copySession(): Promise<void> {
  await navigator.clipboard?.writeText(sessionId.value).catch(() => {})
}

function onKey(e: KeyboardEvent): void {
  const target = e.target as HTMLElement
  if (target.closest('input, textarea, select') || e.ctrlKey || e.metaKey || e.altKey) return
  const n = Number(e.key)
  if (n >= 1 && n <= tabs.length) tab.value = tabs[n - 1]!.value
}

/** The brainstormed idea(s) this work item was created from (Phase 2d). */
const bornFrom = ref<IdeaSummary[]>([])
watch(() => props.id, (id) => {
  void store.open(id)
  bornFrom.value = []
  void get<IdeaSummary[]>(`/api/ideas?workItem=${id}`).then((ideas) => (bornFrom.value = ideas), () => {})
}, { immediate: true })
// The run's full event stream and live state, while it's the one shown.
watch(runId, (id, before) => {
  if (before !== undefined) events.close(before)
  if (id === undefined) return
  // A finished run's transcript is read page by page; an active one also streams what comes next.
  void events.open(id, !runFinal.value).catch(() => {})
  void jobs.refresh(id).catch(() => {})
})
watch(runFinal, (final) => {
  if (final && runId.value !== undefined) events.settle(runId.value)
})
onMounted(() => {
  globalThis.addEventListener('keydown', onKey)
  void config.load().catch(() => {})
})
onBeforeUnmount(() => {
  globalThis.removeEventListener('keydown', onKey)
  clearInterval(ticker)
  if (runId.value !== undefined) events.close(runId.value)
  store.close()
})
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <RouterLink
      to="/history"
      class="text-sm text-muted"
    >
      ← History
    </RouterLink>
    <p
      v-if="store.error"
      class="alert alert-warning alert-soft text-sm"
    >
      {{ store.error }}
    </p>
    <p
      v-else-if="!store.summary"
      class="text-sm text-muted"
    >
      Loading work item #{{ id }}…
    </p>
    <template v-else>
      <header class="grid gap-2">
        <div class="flex flex-wrap items-center gap-2">
          <AgStateBadge
            v-if="run"
            :state="run.state as JobState"
          />
          <h1 class="text-xl font-semibold">
            <span class="font-mono text-muted">WI-{{ store.summary.workItemId }}</span> · {{ store.summary.title }}
          </h1>
        </div>
        <p class="flex flex-wrap gap-x-3 gap-y-1 text-sm text-muted">
          <span>{{ store.summary.repo }}</span>
          <span>{{ store.summary.jobs.length }} run{{ store.summary.jobs.length === 1 ? '' : 's' }}</span>
          <span class="tabular-nums">{{ duration(span) }} from first pick-up to last activity</span>
          <span v-if="run?.phase">phase: {{ run.phase }}</span>
          <RouterLink
            v-for="idea in bornFrom"
            :key="idea.id"
            :to="{ name: 'idea', params: { id: idea.id } }"
            class="link"
          >
            💡 born from idea #{{ idea.id }}: {{ idea.title }}
          </RouterLink>
        </p>
        <div class="flex flex-wrap items-center gap-2">
          <a
            v-if="config.workItemUrl(store.summary.repo, store.summary.workItemId)"
            class="btn btn-ghost btn-sm"
            :href="config.workItemUrl(store.summary.repo, store.summary.workItemId)!"
            target="_blank"
            rel="noopener noreferrer"
          >Work item ↗</a>
          <a
            v-for="pr in store.summary.pullRequests"
            :key="pr.url"
            class="btn btn-ghost btn-sm"
            :href="pr.url"
            target="_blank"
            rel="noopener noreferrer"
          >PR {{ pr.url.split('/').pop() }} ↗</a>
          <template
            v-for="c in store.summary.conversations"
            :key="c.link ?? c.provider"
          >
            <a
              v-if="c.open && c.link"
              class="btn btn-ghost btn-sm"
              :href="c.link"
              target="_blank"
              rel="noopener noreferrer"
            >{{ c.provider }} thread ↗</a>
            <span
              v-else
              class="badge badge-ghost"
              title="The chat thread is closed or deleted; the conversation is kept here."
            >{{ c.provider }} thread closed</span>
          </template>
          <span class="flex-1" />
          <RunActions
            v-if="run"
            :job="run"
          />
        </div>
        <div
          v-if="run"
          class="flex flex-wrap items-center gap-2 text-sm text-muted"
          data-testid="run-bar"
        >
          <div
            v-if="store.summary.jobs.length > 1"
            class="join"
            role="radiogroup"
            aria-label="Run"
          >
            <button
              v-for="(j, i) in store.summary.jobs"
              :key="j.id"
              type="button"
              role="radio"
              :aria-checked="j.id === runId"
              class="btn btn-xs join-item"
              :class="j.id === runId ? 'btn-primary' : ''"
              :title="`job #${j.id} · ${j.state}`"
              @click="pick(j.id)"
            >
              {{ label(i, j) }}
            </button>
          </div>
          <span class="tabular-nums">{{ duration(elapsed) }}</span>
          <span
            v-if="currentStep"
            class="badge badge-outline badge-sm"
            data-testid="step"
            title="The step the agent runs: provider, model and effort"
          >{{ currentStep }}</span>
          <span
            v-if="run.branch"
            class="font-mono text-[13px]"
          >{{ run.branch }}</span>
          <button
            v-if="sessionId"
            type="button"
            class="font-mono text-[13px] hover:text-base-content"
            title="Copy the session id"
            @click="copySession"
          >
            session {{ sessionId.slice(0, 8) }}
          </button>
          <span v-if="detail?.usage">usage 5h {{ percent(detail.usage.fiveHour) }} · week {{ percent(detail.usage.weekly) }}</span>
          <span
            v-if="used"
            class="tabular-nums"
            data-testid="job-resources"
          >CPU {{ used.cpuPercent }}% · RAM {{ bytes(used.memoryBytes) }}<template v-if="used.worktreeBytes != null"> · disk {{ bytes(used.worktreeBytes) }}</template></span>
        </div>
      </header>

      <PermissionBanner
        v-if="run && detail?.permissions.length"
        :requests="detail.permissions"
        :repo="run.repo"
        :busy="jobs.pending.has(run.id)"
        @answer="(requestId, choice) => jobs.answerPermission(run!.id, requestId, choice)"
      />

      <AgTabs
        v-model="tab"
        :tabs="tabs"
      >
        <template #transcript>
          <RunTranscript
            v-if="run"
            :key="run.id"
            :job="run"
          />
        </template>
        <template #diff>
          <RunDiff
            v-if="run"
            :job-id="run.id"
          />
        </template>
        <template #timeline>
          <TimelineTab
            :jobs="store.summary.jobs"
            :events="store.events"
            :has-more="store.hasMore"
            @more="store.loadMore()"
          />
        </template>
        <template #conversation>
          <ConversationTab
            :entries="store.conversation"
            :active-job="active ? { id: active.id, state: active.state } : undefined"
          />
        </template>
        <template #prs>
          <ul class="grid gap-2 text-sm">
            <li
              v-for="pr in store.summary.pullRequests"
              :key="pr.url"
              class="flex flex-wrap items-center gap-2"
            >
              <a
                class="link link-primary"
                :href="pr.url"
                target="_blank"
                rel="noopener noreferrer"
              >PR {{ pr.url.split('/').pop() }}</a>
              <span class="text-muted">from run {{ jobOf(pr.jobId) + 1 }} (job #{{ pr.jobId }})</span>
              <span
                v-if="store.summary.jobs[jobOf(pr.jobId)]?.fixRounds"
                class="badge badge-ghost badge-sm"
              >{{ store.summary.jobs[jobOf(pr.jobId)]!.fixRounds }} review fix rounds</span>
            </li>
            <li
              v-if="!store.summary.pullRequests.length"
              class="text-muted"
            >
              No pull request yet.
            </li>
          </ul>
        </template>
        <template #details>
          <div class="grid gap-6">
            <RunDetails
              v-if="run"
              :job="run"
              :detail="detail"
            />
            <PlanTab
              :jobs="store.summary.jobs"
              :events="store.events"
              :details="store.details"
            />
          </div>
        </template>
      </AgTabs>
    </template>
  </section>
</template>
