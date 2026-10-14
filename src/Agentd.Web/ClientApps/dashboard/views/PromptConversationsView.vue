<script setup lang="ts">
// Settings → Prompts (Admins), by conversation: every job, idea, chat, review, … agentd prompted an agent for, grouped
// by kind, the latest activity first. Open one to read all its turns like a work item's Transcript; All prompts is
// the flat list.
import { computed, ref, watch } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { PromptConversation, PromptConversationPage } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { ago } from '../../shared/utils/format'
import { outcomeTone, promptSources, sourceLabel } from '../components/prompts/sources'

const conversations = ref<PromptConversation[]>([])
const hasMore = ref(false)
const source = ref('')
const error = ref<string | null>(null)
const loading = ref(false)

/** Each kind's conversations, in the filter's order (Jobs, Ideas, Chats, …), the latest activity first inside. */
const groups = computed(() => promptSources
  .map((s) => ({ ...s, rows: conversations.value.filter((c) => c.source === s.value) }))
  .filter((g) => g.rows.length > 0))

async function page(before?: number): Promise<PromptConversationPage> {
  const params = new URLSearchParams({ limit: '100' })
  if (source.value) params.set('source', source.value)
  if (before !== undefined) params.set('before', String(before))
  return get<PromptConversationPage>(`/api/prompts/conversations?${params}`)
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const p = await page()
    conversations.value = p.conversations
    hasMore.value = p.hasMore
    error.value = null
  } catch (err) {
    error.value = err instanceof ApiError && err.status === 403 ? 'Only Admins can read the prompts.' : 'Couldn\'t load the prompts.'
  } finally {
    loading.value = false
  }
}

async function older(): Promise<void> {
  const last = conversations.value.at(-1)
  if (!last) return
  const p = await page(last.lastId)
  conversations.value = [...conversations.value, ...p.conversations]
  hasMore.value = p.hasMore
}

watch(source, () => void load(), { immediate: true })
</script>

<template>
  <section class="grid gap-4">
    <div class="flex flex-wrap items-center gap-3">
      <h1 class="text-xl font-semibold">
        Prompts
      </h1>
      <nav
        class="join ml-auto"
        aria-label="Prompts view"
      >
        <span class="btn btn-sm btn-primary join-item">By conversation</span>
        <RouterLink
          :to="{ name: 'prompts-all' }"
          class="btn btn-sm join-item"
        >
          All prompts
        </RouterLink>
      </nav>
    </div>
    <p class="text-sm text-muted">
      Every conversation agentd had with an agent: each job, idea, chat and review, with all its turns. Open one to read
      what was asked, every tool call and result, and the reply, like a work item's Transcript.
    </p>
    <select
      v-model="source"
      class="select select-sm w-auto"
      aria-label="Kind"
    >
      <option value="">
        Every kind
      </option>
      <option
        v-for="s in promptSources"
        :key="s.value"
        :value="s.value"
      >
        {{ s.label }}
      </option>
    </select>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!conversations.length && !loading"
      class="text-muted"
    >
      No prompts yet.
    </p>
    <section
      v-for="g in groups"
      :key="g.value"
      class="ag-pane pb-2"
      :aria-label="g.label"
      data-testid="conversation-group"
    >
      <h2 class="ag-section-title">
        {{ g.label }} <span class="ag-count">{{ g.rows.length }}</span>
      </h2>
      <RouterLink
        v-for="c in g.rows"
        :key="`${c.source}:${c.sourceId}`"
        :to="{ name: 'prompt-conversation', params: { source: c.source, sourceId: c.sourceId } }"
        class="ag-list-row"
        data-testid="conversation"
      >
        <span class="grid min-w-0 flex-1 gap-0.5">
          <span class="flex min-w-0 items-center gap-2">
            <span class="text-sm font-semibold">{{ sourceLabel(c.source) }} #{{ c.sourceId }}</span>
            <span class="ag-meta">{{ c.turns }} turn{{ c.turns === 1 ? '' : 's' }}<template v-if="c.models.length"> · {{ c.models.join(', ') }}</template></span>
            <span
              class="badge badge-sm"
              :class="c.running ? 'badge-info' : c.lastOutcome ? outcomeTone[c.lastOutcome] ?? 'badge-ghost' : 'badge-ghost'"
            >{{ c.running ? 'running' : c.lastOutcome ?? '—' }}</span>
            <span class="ag-meta ml-auto shrink-0">{{ ago(c.lastAt) }}</span>
          </span>
          <span class="ag-meta truncate font-mono">{{ c.preview }}</span>
        </span>
      </RouterLink>
    </section>
    <div v-if="hasMore">
      <AgButton
        size="sm"
        variant="ghost"
        @click="older"
      >
        Older conversations
      </AgButton>
    </div>
  </section>
</template>
