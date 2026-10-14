<script setup lang="ts">
// One incident: what the telemetry recorded, the investigation's analysis and its thread (the alert, answers). The actions
// and questions are the 🚨 thread's, sent as Inbox replies.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import { AgButton, AgCollapsible } from '../../shared/components/ui'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import { ago } from '../../shared/utils/format'
import IncidentStatusBadge from '../components/IncidentStatusBadge.vue'
import PromptsLink from '../components/prompts/PromptsLink.vue'
import { incidentActions, useIncidentsStore } from '../stores/incidents'
import { useSessionStore } from '../stores/session'

const props = defineProps<{ id: number }>()
const incidents = useIncidentsStore()
const session = useSessionStore()
const detail = computed(() => (incidents.current?.summary.id === props.id ? incidents.current : null))
const incident = computed(() => detail.value?.summary ?? null)
/** Fix it, Ignore, reopen and investigate again are for Operators and Admins (the server says so too). */
const canAct = computed(() => session.user?.roles.some((r) => r === 'Operator' || r === 'Admin') ?? false)
const clock = (at: string) => new Date(at).toLocaleString()

/** The actions that make sense now: Fix it until a Bug is filed, Ignore until it's quiet, reopen once it is. */
const actions = computed(() => {
  const i = incident.value
  if (!i || !canAct.value) return []
  const quiet = i.status === 'Resolved' || i.status === 'Ignored'
  return [
    ...(!i.workItemId && !quiet ? [{ key: 'fix', label: incidentActions.fix, confirm: 'File a Bug in Azure DevOps for agentd to fix?' }] : []),
    ...(!quiet ? [{ key: 'ignore', label: incidentActions.ignore, confirm: null }] : []),
    ...(quiet ? [{ key: 'reopen', label: '👀 Reopen', confirm: null }] : []),
    ...(i.status !== 'Investigating' && !quiet ? [{ key: 'investigate', label: '🔎 Investigate again', confirm: null }] : []),
  ] as { key: keyof typeof incidentActions; label: string; confirm: string | null }[]
})

const text = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const notice = ref<string | null>(null)

async function act(work: () => Promise<void>): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await work()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = false
  }
}

function run(a: { key: keyof typeof incidentActions; confirm: string | null }): Promise<void> | undefined {
  if (a.confirm && !window.confirm(a.confirm)) return
  return act(async () => void (await incidents.reply(incidentActions[a.key])))
}

const ask = () => act(async () => {
  if (!text.value.trim()) return
  await incidents.reply(text.value.trim())
  text.value = ''
  notice.value = 'Asked: the answer arrives here and in the thread.'
  setTimeout(() => (notice.value = null), 4000)
})

watch(() => props.id, (id) => void act(() => incidents.open(id)), { immediate: true })
onBeforeUnmount(() => incidents.close())
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <RouterLink
      :to="{ name: 'incidents' }"
      class="link text-sm"
    >
      ← Incidents
    </RouterLink>
    <p
      v-if="incidents.missing"
      class="text-muted"
    >
      There is no incident {{ props.id }}.
    </p>
    <p
      v-else-if="!incident || !detail"
      class="text-muted"
    >
      {{ error ?? 'Loading…' }}
    </p>
    <template v-else>
      <header class="grid gap-1">
        <h1 class="flex flex-wrap items-center gap-2 text-xl font-semibold">
          <span class="font-mono text-base font-normal text-muted">#{{ incident.id }}</span>
          {{ incident.title }}
          <IncidentStatusBadge :status="incident.status" />
        </h1>
        <p class="text-sm text-muted">
          {{ incident.app }}<template v-if="detail.repository">
            ({{ detail.repository }})
          </template> · {{ incident.kind }}<template v-if="incident.operation">
            · {{ incident.operation }}
          </template> · {{ incident.totalCount }}× from
          <time
            :datetime="incident.firstSeen"
            :title="clock(incident.firstSeen)"
          >{{ ago(incident.firstSeen) }}</time> to
          <time
            :datetime="incident.lastSeen"
            :title="clock(incident.lastSeen)"
          >{{ ago(incident.lastSeen) }}</time>
        </p>
        <p class="flex flex-wrap gap-x-3 text-sm">
          <RouterLink
            v-if="incident.workItemId"
            :to="{ name: 'work-item', params: { id: incident.workItemId } }"
            class="link"
          >
            Bug #{{ incident.workItemId }}
          </RouterLink>
          <a
            v-if="detail.portalUrl"
            :href="detail.portalUrl"
            target="_blank"
            rel="noopener noreferrer"
            class="link"
          >Failures in the Azure portal ↗</a>
          <RouterLink
            :to="{ name: 'inbox-thread', params: { source: 'incident', id: incident.id } }"
            class="link"
          >
            Inbox thread
          </RouterLink>
          <PromptsLink
            source="incident"
            :source-id="incident.id"
          />
        </p>
      </header>

      <div
        v-if="actions.length"
        class="flex flex-wrap gap-2"
        role="group"
        aria-label="Actions"
      >
        <AgButton
          v-for="a in actions"
          :key="a.key"
          size="sm"
          :variant="a.key === 'fix' ? 'primary' : 'outline'"
          :loading="busy"
          @click="run(a)"
        >
          {{ a.label }}
        </AgButton>
      </div>
      <p
        v-if="error"
        class="alert alert-warning text-sm"
        role="alert"
      >
        {{ error }}
      </p>

      <article
        class="ag-pane grid gap-2 p-4"
        aria-label="Investigation"
      >
        <h2 class="font-semibold">
          🔎 Investigation
        </h2>
        <MarkdownText
          v-if="detail.analysis"
          :text="detail.analysis"
        />
        <p
          v-else
          class="text-sm text-muted"
        >
          {{ incident.status === 'Investigating' ? 'The agent is reading the code…' : 'Not investigated yet.' }}
        </p>
      </article>

      <AgCollapsible
        title="What the telemetry recorded"
        :default-open="!detail.analysis"
      >
        <p
          v-if="incident.message"
          class="mb-2"
        >
          {{ incident.message }}
        </p>
        <p
          v-if="detail.resultCode"
          class="ag-meta mb-2"
        >
          Result code {{ detail.resultCode }}
        </p>
        <pre
          v-if="detail.stack"
          class="overflow-x-auto font-mono text-xs whitespace-pre"
          data-testid="incident-stack"
        >{{ detail.stack }}</pre>
      </AgCollapsible>

      <section
        class="grid gap-2"
        aria-label="Thread"
      >
        <h2 class="font-semibold">
          Thread
        </h2>
        <ol class="grid gap-2">
          <li
            v-for="m in incidents.thread"
            :key="m.id"
            class="ag-message"
            :class="{ 'is-question': m.kind === 'Question' }"
            data-testid="incident-message"
          >
            <p class="ag-meta">
              {{ m.kind === 'Question' ? '❓ Question' : m.kind === 'Error' ? '⚠️ Problem' : m.kind === 'Result' ? '✅ Result' : 'agentd' }} · {{ ago(m.createdAt) }}
            </p>
            <MarkdownText :text="m.markdown" />
          </li>
        </ol>
        <form
          class="grid gap-2"
          @submit.prevent="ask"
        >
          <textarea
            v-model="text"
            class="textarea w-full"
            rows="3"
            placeholder="Ask the agent about it: it resumes its investigation"
            aria-label="Question"
          />
          <div class="flex items-center gap-2">
            <AgButton
              type="submit"
              size="sm"
              :loading="busy"
              :disabled="!text.trim()"
            >
              Ask
            </AgButton>
            <span
              v-if="notice"
              class="ag-meta"
              role="status"
            >{{ notice }}</span>
          </div>
        </form>
      </section>
    </template>
  </section>
</template>
