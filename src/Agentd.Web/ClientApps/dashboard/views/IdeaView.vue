<script setup lang="ts">
// One idea: its drafts (stories with their tasks), the work items it created, and the whole conversation, which
// outlives the chat thread. Talk to the agent here like in its thread; the choices are the chat's buttons.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { WorkItemDraft } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import IdeaStatusBadge from '../components/IdeaStatusBadge.vue'
import { ideaChoices, ideaEfforts, ideaModels, useIdeasStore } from '../stores/ideas'

const props = defineProps<{ id: number }>()
const ideas = useIdeasStore()
const idea = computed(() => (ideas.current?.idea.id === props.id ? ideas.current : null))

/** Stories (and parentless tasks) in order, each with the tasks that name it as parent. */
const tree = computed(() => {
  const drafts = idea.value?.drafts ?? []
  return drafts
    .map((d, i) => ({ d, i }))
    .filter(({ d }) => d.parent == null)
    .map(({ d, i }) => ({ draft: d, tasks: drafts.filter((t) => t.parent === i) }))
})
const estimate = (d: WorkItemDraft) => (d.estimate == null ? '' : d.type === 'Task' ? `${d.estimate} h` : `${d.estimate} pts`)
const clock = (at: string) => new Date(at).toLocaleString()

const open = computed(() => idea.value?.idea.status === 'Brainstorming' || idea.value?.idea.status === 'Proposed')
const text = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const settings = ref({ model: '', effort: '' })
watch(idea, (i) => (settings.value = { model: i?.idea.model ?? '', effort: i?.idea.effort ?? '' }))

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

const send = () => act(async () => {
  if (!text.value.trim()) return
  await ideas.message(text.value.trim())
  text.value = ''
})

/** Create and Create and start make Azure DevOps work items, so they're confirmed first. */
function choose(choice: keyof typeof ideaChoices): Promise<void> | undefined {
  const creates = choice === 'create' || choice === 'start'
  if (creates && !window.confirm(choice === 'start'
    ? 'Create these work items in Azure DevOps and tag them for agentd to start?'
    : 'Create these work items in Azure DevOps?')) return
  return act(() => ideas.message(ideaChoices[choice]))
}

const saveSettings = () => act(() => ideas.settings(settings.value.model || null, settings.value.effort || null))

watch(() => props.id, (id) => void ideas.open(id).catch(() => {}), { immediate: true })
onBeforeUnmount(() => ideas.close())
</script>

<template>
  <section class="mx-auto grid max-w-4xl gap-4">
    <RouterLink
      to="/ideas"
      class="text-sm text-muted"
    >
      ← Ideas
    </RouterLink>
    <p
      v-if="ideas.missing"
      class="text-muted"
    >
      There is no idea #{{ id }}.
    </p>
    <p
      v-else-if="!idea"
      class="text-muted"
    >
      Loading…
    </p>
    <template v-else>
      <header class="grid gap-1">
        <h1 class="flex flex-wrap items-center gap-2 text-xl font-semibold">
          💡 {{ idea.idea.title }} <IdeaStatusBadge :status="idea.idea.status" />
        </h1>
        <p class="text-sm text-muted">
          #{{ idea.idea.id }} · {{ idea.idea.repo }} · by {{ idea.idea.author }} · started {{ clock(idea.idea.createdAt) }}
          · model {{ idea.idea.model ?? 'default' }}, effort {{ idea.idea.effort ?? 'default' }}
        </p>
        <p
          v-if="idea.idea.createdWorkItems.length"
          class="text-sm"
        >
          Created:
          <RouterLink
            v-for="wi in idea.idea.createdWorkItems"
            :key="wi"
            :to="{ name: 'work-item', params: { id: wi } }"
            class="link mr-2 font-mono text-[13px]"
          >
            WI-{{ wi }}
          </RouterLink>
        </p>
      </header>

      <div
        v-if="tree.length"
        class="grid gap-2"
      >
        <h2 class="text-base font-semibold">
          {{ idea.idea.status === 'Proposed' ? 'Proposed work items' : 'Latest drafts' }}
        </h2>
        <ul class="grid gap-2">
          <li
            v-for="(node, n) in tree"
            :key="n"
            class="rounded-box border border-base-300 p-3"
            data-testid="draft"
          >
            <p class="font-medium">
              <span class="badge badge-outline badge-sm mr-1">{{ node.draft.type }}</span> {{ node.draft.title }}
              <span class="text-xs text-muted">{{ estimate(node.draft) }}</span>
            </p>
            <p
              v-if="node.draft.description"
              class="mt-1 whitespace-pre-wrap text-sm"
            >
              {{ node.draft.description }}
            </p>
            <p
              v-if="node.draft.acceptanceCriteria"
              class="mt-1 whitespace-pre-wrap text-sm text-muted"
            >
              {{ node.draft.acceptanceCriteria }}
            </p>
            <ul
              v-if="node.tasks.length"
              class="mt-2 grid gap-1 border-l-2 border-base-300 pl-3 text-sm"
            >
              <li
                v-for="(t, k) in node.tasks"
                :key="k"
              >
                {{ t.title }} <span class="text-xs text-muted">{{ estimate(t) }}</span>
              </li>
            </ul>
          </li>
        </ul>
        <div
          v-if="idea.idea.status === 'Proposed'"
          class="flex flex-wrap gap-2"
          role="group"
          aria-label="Choose"
        >
          <AgButton
            size="sm"
            :disabled="busy"
            @click="choose('create')"
          >
            {{ ideaChoices.create }}
          </AgButton>
          <AgButton
            size="sm"
            :disabled="busy"
            @click="choose('start')"
          >
            {{ ideaChoices.start }}
          </AgButton>
          <AgButton
            size="sm"
            variant="outline"
            :disabled="busy"
            @click="choose('change')"
          >
            {{ ideaChoices.change }}
          </AgButton>
          <AgButton
            size="sm"
            variant="ghost"
            :disabled="busy"
            @click="choose('discard')"
          >
            {{ ideaChoices.discard }}
          </AgButton>
        </div>
      </div>

      <div class="grid gap-2">
        <h2 class="text-base font-semibold">
          Conversation
        </h2>
        <p
          v-if="idea.messages.length === 0"
          class="text-muted"
        >
          No messages yet.
        </p>
        <div
          v-for="(m, i) in idea.messages"
          :key="i"
          class="chat"
          :class="m.direction === 'out' ? 'chat-start' : 'chat-end'"
          :data-direction="m.direction"
        >
          <div class="chat-header text-xs text-muted">
            {{ m.direction === 'out' ? (m.author === 'agentd' ? 'agentd' : 'agent') : m.author }} · {{ clock(m.at) }}
          </div>
          <div
            v-if="m.direction === 'out'"
            class="chat-bubble bg-base-200 text-base-content"
          >
            <MarkdownText :text="m.text" />
          </div>
          <div
            v-else
            class="chat-bubble whitespace-pre-wrap bg-primary/15 text-sm text-base-content"
          >
            {{ m.text }}
          </div>
        </div>
        <div
          v-if="idea.thinking"
          class="chat chat-start"
          role="status"
        >
          <div class="chat-bubble bg-base-200 text-sm text-muted">
            <span class="loading loading-dots loading-xs" /> thinking…
          </div>
        </div>
        <form
          v-if="open"
          class="grid gap-2"
          @submit.prevent="send"
        >
          <textarea
            v-model="text"
            class="textarea w-full"
            rows="2"
            maxlength="8000"
            placeholder="Message the agent (Ctrl+Enter to send)"
            aria-label="Message the agent"
            @keydown.ctrl.enter.prevent="send"
            @keydown.meta.enter.prevent="send"
          />
          <div class="flex flex-wrap items-center gap-2">
            <AgButton
              type="submit"
              size="sm"
              :loading="busy"
              :disabled="!text.trim()"
            >
              Send
            </AgButton>
            <select
              v-model="settings.model"
              class="select select-xs w-auto"
              aria-label="Model"
            >
              <option value="">
                Default model
              </option>
              <option
                v-for="m in ideaModels"
                :key="m"
                :value="m"
              >
                {{ m }}
              </option>
            </select>
            <select
              v-model="settings.effort"
              class="select select-xs w-auto"
              aria-label="Effort"
            >
              <option value="">
                Default effort
              </option>
              <option
                v-for="e in ideaEfforts"
                :key="e"
                :value="e"
              >
                {{ e }}
              </option>
            </select>
            <AgButton
              v-if="settings.model !== (idea.idea.model ?? '') || settings.effort !== (idea.idea.effort ?? '')"
              size="sm"
              variant="ghost"
              :disabled="busy"
              @click="saveSettings"
            >
              Use from the next reply
            </AgButton>
          </div>
        </form>
        <p
          v-if="error"
          class="text-sm text-error"
          role="alert"
        >
          {{ error }}
        </p>
      </div>
    </template>
  </section>
</template>
