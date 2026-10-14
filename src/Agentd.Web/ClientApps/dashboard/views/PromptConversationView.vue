<script setup lang="ts">
// Settings → Prompts → one conversation (Admins): every turn agentd sent for this job, idea, chat or review, oldest
// first, each like a work item's Transcript: what was asked (with its system rules), the agent's text, tool calls and
// results, then its reply and how it ended.
import { ref, watch } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { PromptDetail } from '../../shared/api/types'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import { dateTime, duration } from '../../shared/utils/format'
import TurnTranscript from '../components/prompts/TurnTranscript.vue'
import { outcomeTone, sourceLabel, sourcePage } from '../components/prompts/sources'

const props = defineProps<{ source: string; sourceId: number }>()
const turns = ref<PromptDetail[] | null>(null)
const error = ref<string | null>(null)

async function load(): Promise<void> {
  try {
    turns.value = await get<PromptDetail[]>(`/api/prompts/conversations/${props.source}/${props.sourceId}`)
    error.value = null
  } catch (err) {
    turns.value = null
    error.value = err instanceof ApiError && err.status === 404 ? `No prompts were sent for ${sourceLabel(props.source).toLowerCase()} #${props.sourceId}.`
      : err instanceof ApiError && err.status === 403 ? 'Only Admins can read the prompts.' : 'Couldn\'t load the conversation.'
  }
}
watch(() => [props.source, props.sourceId], load, { immediate: true })

const took = (t: PromptDetail) => (t.finishedAt ? duration((Date.parse(t.finishedAt) - Date.parse(t.startedAt)) / 1000) : null)
</script>

<template>
  <section class="grid gap-4">
    <nav
      class="breadcrumbs text-sm"
      aria-label="Breadcrumb"
    >
      <ul>
        <li>
          <RouterLink :to="{ name: 'prompts' }">
            Prompts
          </RouterLink>
        </li>
        <li aria-current="page">
          {{ sourceLabel(source) }} #{{ sourceId }}
        </li>
      </ul>
    </nav>
    <header class="flex flex-wrap items-center gap-3">
      <h1 class="text-xl font-semibold">
        {{ sourceLabel(source) }} #{{ sourceId }}
      </h1>
      <span
        v-if="turns"
        class="ag-meta"
      >{{ turns.length }} turn{{ turns.length === 1 ? '' : 's' }}</span>
      <RouterLink
        v-if="sourcePage(source, sourceId)"
        :to="sourcePage(source, sourceId)!"
        class="link ml-auto text-sm"
      >
        Open its page
      </RouterLink>
    </header>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!turns"
      class="text-muted"
    >
      Loading…
    </p>
    <ol
      v-else
      class="grid gap-4"
      aria-label="Turns"
    >
      <li
        v-for="(t, i) in turns"
        :key="t.id"
        class="ag-turn"
        data-testid="turn"
      >
        <p class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
          <span class="font-semibold">Turn {{ i + 1 }}</span>
          <span
            v-if="t.step"
            class="badge badge-outline badge-sm"
          >{{ t.step }}</span>
          <span class="ag-meta">{{ t.model ?? 'default model' }}{{ t.effort ? ` · ${t.effort}` : '' }}{{ t.resume ? ' · resumed' : '' }}</span>
          <span
            class="badge badge-sm"
            :class="t.outcome ? outcomeTone[t.outcome] ?? 'badge-ghost' : 'badge-info'"
          >{{ t.outcome ?? 'running' }}</span>
          <span class="ag-meta ml-auto">{{ dateTime(t.startedAt) }}<template v-if="took(t)"> · took {{ took(t) }}</template></span>
          <RouterLink
            :to="{ name: 'prompt', params: { id: t.id } }"
            class="link ag-meta"
          >
            #{{ t.id }}
          </RouterLink>
        </p>
        <details data-testid="turn-prompt">
          <summary class="ag-summary">
            Prompt
          </summary>
          <pre class="ag-pre mt-2">{{ t.prompt }}</pre>
          <details
            v-if="t.rules"
            class="mt-2"
          >
            <summary class="ag-summary">
              System rules
            </summary>
            <pre class="ag-pre mt-2">{{ t.rules }}</pre>
          </details>
        </details>
        <TurnTranscript :prompt-id="t.id" />
        <p
          v-if="t.error"
          class="alert alert-error text-sm"
          role="alert"
        >
          {{ t.error }}
        </p>
        <div
          v-if="t.reply"
          class="grid gap-1"
          data-testid="turn-reply"
        >
          <span class="ag-meta">Reply</span>
          <MarkdownText :text="t.reply" />
        </div>
      </li>
    </ol>
  </section>
</template>
