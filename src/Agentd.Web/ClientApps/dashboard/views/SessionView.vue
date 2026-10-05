<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { AgentEvent, Diff, JobState } from '../../shared/api/types'
import { AgButton, AgModal, AgStateBadge, AgTabs } from '../../shared/components/ui'
import { duration, percent } from '../../shared/utils/format'
import DiffView from '../components/session/DiffView.vue'
import EventList from '../components/session/EventList.vue'
import MessageComposer from '../components/session/MessageComposer.vue'
import { useConfigStore } from '../stores/config'
import { useEventsStore } from '../stores/events'
import { useJobsStore } from '../stores/jobs'

const props = defineProps<{ id: number }>()
const jobs = useJobsStore()
const events = useEventsStore()
const config = useConfigStore()
const tab = ref('transcript')
const tabs = [
  { value: 'transcript', label: 'Transcript', keepMounted: true },
  { value: 'diff', label: 'Diff' },
  { value: 'details', label: 'Details' },
]
const confirming = ref<'cancel' | 'retry' | null>(null)
const pendingReplies = ref<string[]>([])
const now = ref(Date.now())
const ticker = setInterval(() => (now.value = Date.now()), 1000)

const job = computed(() => jobs.byId.get(props.id) ?? jobs.details.get(props.id)?.job)
const detail = computed(() => jobs.details.get(props.id))
const eventWindow = computed(() => events.windows.get(props.id))
const final = computed(() => ['Done', 'Failed', 'Cancelled'].includes(job.value?.state ?? ''))
const elapsed = computed(() => (job.value ? (final.value ? job.value.elapsedSeconds : (now.value - Date.parse(job.value.startedAt)) / 1000) : 0))
const sessionId = computed(() => {
  const session = [...(eventWindow.value?.events ?? [])].reverse().find((e) => e.type === 'agent.session')
  return session ? String((session.payload as Record<string, unknown>).sessionId ?? '') : ''
})
/** The agent's replies already in the transcript replace the optimistic ones. */
const shownReplies = computed(() => {
  const arrived = new Set((eventWindow.value?.events ?? []).filter((e: AgentEvent) => e.type === 'DeveloperReplied').map((e) => String((e.payload as Record<string, unknown>).reply ?? '')))
  return pendingReplies.value.filter((r) => !arrived.has(r))
})

/** Loaded on the first visit to the Diff tab, then refreshed (2 s debounce) after the agent edits files. */
const diffRefreshMs = 2000
const diff = ref<Diff | null>(null)
const diffError = ref<string | null>(null)
let diffTimer: ReturnType<typeof setTimeout> | undefined

async function loadDiff(): Promise<void> {
  try {
    diff.value = await get<Diff>(`/api/jobs/${props.id}/diff`)
    diffError.value = null
  } catch (err) {
    diffError.value = err instanceof ApiError && err.status === 404 ? 'No branch yet: the diff appears once the agent starts working.' : 'Could not load the diff.'
  }
}

watch(tab, (t) => {
  if (t === 'diff' && !diff.value) void loadDiff()
})

/** Finished Edit / MultiEdit / Write calls in the transcript. */
const editResults = computed(() => {
  const calls = new Map<string, string>()
  let count = 0
  for (const e of eventWindow.value?.events ?? []) {
    const p = e.payload as Record<string, unknown>
    if (e.type === 'agent.tool_call') calls.set(String(p.id), String(p.name))
    else if (e.type === 'agent.tool_result' && ['Edit', 'MultiEdit', 'Write'].includes(calls.get(String(p.toolUseId)) ?? '')) count++
  }
  return count
})

watch(editResults, (now, before) => {
  if (now <= before || tab.value !== 'diff') return
  clearTimeout(diffTimer)
  diffTimer = setTimeout(() => void loadDiff(), diffRefreshMs)
})

function onKey(e: KeyboardEvent): void {
  const target = e.target as HTMLElement
  if (target.closest('input, textarea') || e.ctrlKey || e.metaKey || e.altKey) return
  if (e.key === '1' || e.key === '2' || e.key === '3') tab.value = tabs[Number(e.key) - 1]!.value
}

async function act(): Promise<void> {
  const action = confirming.value
  confirming.value = null
  if (action === 'cancel') await jobs.cancel(props.id)
  if (action === 'retry') await jobs.retry(props.id)
  await jobs.refresh(props.id).catch(() => {})
}

async function copySession(): Promise<void> {
  await navigator.clipboard?.writeText(sessionId.value).catch(() => {})
}

onMounted(() => {
  globalThis.addEventListener('keydown', onKey)
  void config.load().catch(() => {})
  void jobs.refresh(props.id).catch(() => {})
  void events.open(props.id).catch(() => {})
})
onBeforeUnmount(() => {
  globalThis.removeEventListener('keydown', onKey)
  clearInterval(ticker)
  clearTimeout(diffTimer)
  events.close(props.id)
})
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <RouterLink
      to="/"
      class="text-sm text-muted"
    >
      ← Dashboard
    </RouterLink>
    <p
      v-if="!job"
      class="text-sm text-muted"
    >
      Loading job #{{ id }}…
    </p>
    <template v-else>
      <header class="grid gap-2">
        <div class="flex flex-wrap items-center gap-2">
          <AgStateBadge :state="job.state as JobState" />
          <h1 class="text-xl font-semibold">
            <RouterLink
              :to="{ name: 'work-item', params: { id: job.workItemId } }"
              class="link font-mono text-muted"
              title="Every run of this work item"
            >
              WI-{{ job.workItemId }}
            </RouterLink> · {{ job.title }}
          </h1>
        </div>
        <p class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted">
          <span class="tabular-nums">{{ duration(elapsed) }}</span>
          <span v-if="job.phase">phase: {{ job.phase }}</span>
          <span>{{ job.repo }}</span>
          <span
            v-if="job.branch"
            class="font-mono text-[13px]"
          >{{ job.branch }}</span>
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
        </p>
        <div class="flex flex-wrap items-center gap-2">
          <a
            v-if="config.workItemUrl(job.repo, job.workItemId)"
            class="btn btn-ghost btn-sm"
            :href="config.workItemUrl(job.repo, job.workItemId)!"
            target="_blank"
            rel="noopener noreferrer"
          >Work item ↗</a>
          <template
            v-for="c in detail?.conversations ?? []"
            :key="c.provider"
          >
            <a
              v-if="c.link"
              class="btn btn-ghost btn-sm"
              :href="c.link"
              target="_blank"
              rel="noopener noreferrer"
            >{{ c.provider }} thread ↗</a>
          </template>
          <a
            v-if="job.prUrl"
            class="btn btn-ghost btn-sm"
            :href="job.prUrl"
            target="_blank"
            rel="noopener noreferrer"
          >Pull request ↗</a>
          <span class="flex-1" />
          <AgButton
            v-if="job.state === 'Failed'"
            size="sm"
            variant="outline"
            :loading="jobs.pending.has(id)"
            @click="confirming = 'retry'"
          >
            Retry
          </AgButton>
          <AgButton
            v-else-if="!final"
            size="sm"
            variant="error"
            :loading="jobs.pending.has(id)"
            @click="confirming = 'cancel'"
          >
            Cancel
          </AgButton>
        </div>
      </header>

      <AgTabs
        v-model="tab"
        :tabs="tabs"
      >
        <template #transcript>
          <div class="grid gap-3">
            <EventList
              v-if="eventWindow"
              :events="eventWindow.events"
              :has-more="eventWindow.hasMore"
              :load-earlier="() => events.loadEarlier(id)"
            />
            <p
              v-else
              class="text-sm text-muted"
            >
              Loading the transcript…
            </p>
            <div
              v-for="r in shownReplies"
              :key="r"
              class="chat chat-end opacity-60"
            >
              <div class="chat-header text-xs text-muted">
                you · sending
              </div>
              <div class="chat-bubble bg-primary/15 text-base-content">
                {{ r }}
              </div>
            </div>
            <MessageComposer
              :job-id="id"
              :state="job.state"
              @sent="pendingReplies.push($event)"
            />
          </div>
        </template>
        <template #diff>
          <DiffView
            v-if="diff"
            :diff="diff"
          />
          <p
            v-else-if="diffError"
            class="text-sm text-muted"
          >
            {{ diffError }}
          </p>
          <p
            v-else
            class="text-sm text-muted"
          >
            Loading the diff…
          </p>
        </template>
        <template #details>
          <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
            <dt class="text-muted">
              Job
            </dt><dd class="font-mono">
              #{{ job.id }}
            </dd>
            <dt class="text-muted">
              Attempt / resumes
            </dt><dd>{{ detail?.attempt ?? '—' }} / {{ detail?.resumeCount ?? '—' }}</dd>
            <dt class="text-muted">
              Plan
            </dt><dd>{{ job.planStatus }}<span v-if="detail?.estimate"> · ~{{ detail.estimate.minutes }} min, ~{{ detail.estimate.usagePercent }}% of the 5h window</span></dd>
            <dt class="text-muted">
              Review fix rounds
            </dt><dd>{{ job.fixRounds }}</dd>
            <dt class="text-muted">
              Hand-off
            </dt><dd>{{ job.handoff }}</dd>
            <dt class="text-muted">
              Unread messages
            </dt><dd>{{ detail?.pendingMessages ?? 0 }}</dd>
            <dt class="text-muted">
              Last activity
            </dt><dd>{{ detail?.lastActivity ?? '—' }}</dd>
            <template v-if="job.lastError">
              <dt class="text-muted">
                Last error
              </dt><dd class="text-error">
                {{ job.lastError }}
              </dd>
            </template>
          </dl>
        </template>
      </AgTabs>
    </template>

    <AgModal
      :open="confirming !== null"
      :title="confirming === 'retry' ? 'Retry this job?' : 'Cancel this job?'"
      @update:open="(v: boolean) => !v && (confirming = null)"
    >
      {{ confirming === 'retry' ? 'The job is queued again as a new attempt.' : 'The agent stops and the job ends as Cancelled.' }}
      <template #actions>
        <AgButton
          variant="ghost"
          @click="confirming = null"
        >
          Back
        </AgButton>
        <AgButton
          :variant="confirming === 'retry' ? 'primary' : 'error'"
          @click="act"
        >
          {{ confirming === 'retry' ? 'Retry' : 'Cancel job' }}
        </AgButton>
      </template>
    </AgModal>
  </section>
</template>
