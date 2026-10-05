<script setup lang="ts">
// One idea: its drafts (stories with their tasks), the work items it created, and the whole conversation,
// which outlives the chat thread. Read-only: people continue in the thread.
import { computed, onBeforeUnmount, watch } from 'vue'
import type { WorkItemDraft } from '../../shared/api/types'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import IdeaStatusBadge from '../components/IdeaStatusBadge.vue'
import { useIdeasStore } from '../stores/ideas'

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
            {{ m.direction === 'out' ? 'agentd' : m.author }} · {{ clock(m.at) }}
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
        <p class="text-xs text-muted">
          Continue the conversation in the idea's chat thread.
        </p>
      </div>
    </template>
  </section>
</template>
