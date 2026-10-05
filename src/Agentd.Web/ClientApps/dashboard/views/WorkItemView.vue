<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { JobState } from '../../shared/api/types'
import { AgStateBadge, AgTabs } from '../../shared/components/ui'
import { duration } from '../../shared/utils/format'
import ActivityTab from '../components/workitem/ActivityTab.vue'
import ConversationTab from '../components/workitem/ConversationTab.vue'
import PlanTab from '../components/workitem/PlanTab.vue'
import TimelineTab from '../components/workitem/TimelineTab.vue'
import { useConfigStore } from '../stores/config'
import { useWorkItemsStore } from '../stores/workItems'

const props = defineProps<{ id: number }>()
const store = useWorkItemsStore()
const config = useConfigStore()
const tab = ref('timeline')
const tabs = [
  { value: 'timeline', label: 'Timeline', keepMounted: true },
  { value: 'conversation', label: 'Conversation' },
  { value: 'activity', label: 'Activity' },
  { value: 'prs', label: 'Pull requests' },
  { value: 'plan', label: 'Plan & usage' },
]

const latest = computed(() => store.summary?.jobs.at(-1))
const active = computed(() => store.summary?.jobs.find((j) => !['Done', 'Failed', 'Cancelled'].includes(j.state)))
const span = computed(() => (store.summary ? (Date.parse(store.summary.lastActivityAt) - Date.parse(store.summary.firstSeenAt)) / 1000 : 0))
const jobOf = (jobId: number) => store.summary?.jobs.findIndex((j) => j.id === jobId) ?? -1

onMounted(() => void config.load().catch(() => {}))
watch(() => props.id, (id) => void store.open(id), { immediate: true })
onBeforeUnmount(() => store.close())
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
            v-if="latest"
            :state="latest.state as JobState"
          />
          <h1 class="text-xl font-semibold">
            <span class="font-mono text-muted">WI-{{ store.summary.workItemId }}</span> · {{ store.summary.title }}
          </h1>
        </div>
        <p class="flex flex-wrap gap-x-3 gap-y-1 text-sm text-muted">
          <span>{{ store.summary.repo }}</span>
          <span>{{ store.summary.jobs.length }} run{{ store.summary.jobs.length === 1 ? '' : 's' }}</span>
          <span class="tabular-nums">{{ duration(span) }} from first pick-up to last activity</span>
          <span v-if="latest?.phase">phase: {{ latest.phase }}</span>
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
        </div>
      </header>

      <AgTabs
        v-model="tab"
        :tabs="tabs"
      >
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
        <template #activity>
          <ActivityTab
            :jobs="store.summary.jobs"
            :events="store.events"
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
          <p class="mt-3 text-xs text-muted">
            Review threads and their fix rounds get their own screen with the PR reviewer (Phase 7).
          </p>
        </template>
        <template #plan>
          <PlanTab
            :jobs="store.summary.jobs"
            :events="store.events"
            :details="store.details"
          />
        </template>
      </AgTabs>
    </template>
  </section>
</template>
