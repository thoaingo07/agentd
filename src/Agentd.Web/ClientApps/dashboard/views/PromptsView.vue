<script setup lang="ts">
// Settings → Prompts (Admins): every prompt agentd sent an agent, newest first (prompt-log.md). The filters live in the
// address, so a job, idea or review page links straight to its own prompts; older pages load on demand.
import { onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import { ApiError, get } from '../../shared/api/http'
import type { PromptPage, PromptSummary } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { dateTime, duration } from '../../shared/utils/format'
import { outcomeTone, promptOutcomes, promptSources, sourceLabel, sourcePage } from '../components/prompts/sources'

const keys = ['source', 'sourceId', 'step', 'model', 'outcome'] as const
type Filter = Record<(typeof keys)[number], string>

const route = useRoute()
const router = useRouter()
const prompts = ref<PromptSummary[]>([])
const hasMore = ref(false)
const loading = ref(false)
const error = ref<string | null>(null)
const form = ref<Filter>(fromQuery(route.query))
let debounce: ReturnType<typeof setTimeout> | undefined

function fromQuery(query: LocationQuery): Filter {
  const one = (k: string) => (typeof query[k] === 'string' ? query[k] : '')
  return { source: one('source'), sourceId: one('sourceId'), step: one('step'), model: one('model'), outcome: one('outcome') }
}

/** The filters that are set, trimmed: the address query and the API's. */
function set(filter: Filter): Record<string, string> {
  return Object.fromEntries(keys.map((k) => [k, filter[k].trim()]).filter(([, v]) => v))
}

async function page(before?: number): Promise<PromptPage> {
  const params = new URLSearchParams(set(fromQuery(route.query)))
  if (before !== undefined) params.set('before', String(before))
  return get<PromptPage>(`/api/prompts?${params}`)
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const p = await page()
    prompts.value = p.prompts
    hasMore.value = p.hasMore
    error.value = null
  } catch (err) {
    error.value = err instanceof ApiError && err.status === 403 ? 'Only Admins can read the prompts.' : 'Couldn\'t load the prompts.'
  } finally {
    loading.value = false
  }
}

async function older(): Promise<void> {
  const last = prompts.value.at(-1)
  if (!last) return
  const p = await page(last.id)
  prompts.value = [...prompts.value, ...p.prompts]
  hasMore.value = p.hasMore
}

// Typing waits a moment before the address (and so the list) follows.
watch(form, (f) => {
  clearTimeout(debounce)
  debounce = setTimeout(() => {
    if (JSON.stringify(set(f)) !== JSON.stringify(set(fromQuery(route.query)))) void router.replace({ query: set(f) })
  }, 300)
}, { deep: true })
watch(() => route.query, (q) => {
  if (JSON.stringify(set(fromQuery(q))) !== JSON.stringify(set(form.value))) form.value = fromQuery(q)
  void load()
}, { immediate: true })
onBeforeUnmount(() => clearTimeout(debounce))

function clear(): void {
  form.value = fromQuery({})
}

const took = (p: PromptSummary) => (p.finishedAt ? duration((Date.parse(p.finishedAt) - Date.parse(p.startedAt)) / 1000) : null)
const chars = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n))
</script>

<template>
  <section class="grid gap-4">
    <h1 class="text-xl font-semibold">
      Prompts
    </h1>
    <p class="text-sm text-muted">
      Every prompt agentd sent an agent: what it asked, with which model, and how it ended. Open one for the whole text, its
      system rules and the reply.
    </p>
    <form
      class="flex flex-wrap items-center gap-2"
      aria-label="Filter the prompts"
      @submit.prevent="load"
    >
      <select
        v-model="form.source"
        class="select select-sm w-auto"
        aria-label="Source"
      >
        <option value="">
          Every source
        </option>
        <option
          v-for="s in promptSources"
          :key="s.value"
          :value="s.value"
        >
          {{ s.label }}
        </option>
      </select>
      <input
        v-model="form.sourceId"
        class="input input-sm w-24"
        inputmode="numeric"
        placeholder="Its id"
        aria-label="Source id"
      >
      <input
        v-model="form.step"
        class="input input-sm w-32"
        placeholder="Step"
        aria-label="Step"
      >
      <input
        v-model="form.model"
        class="input input-sm w-32"
        placeholder="Model"
        aria-label="Model"
      >
      <select
        v-model="form.outcome"
        class="select select-sm w-auto"
        aria-label="Outcome"
      >
        <option value="">
          Any outcome
        </option>
        <option
          v-for="o in promptOutcomes"
          :key="o"
          :value="o"
        >
          {{ o }}
        </option>
      </select>
      <AgButton
        size="sm"
        variant="ghost"
        :disabled="loading"
        @click="load"
      >
        Refresh
      </AgButton>
      <AgButton
        v-if="Object.keys(set(form)).length"
        size="sm"
        variant="ghost"
        @click="clear"
      >
        Clear filters
      </AgButton>
    </form>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!prompts.length && !loading"
      class="text-muted"
    >
      No prompts{{ Object.keys(set(form)).length ? ' match these filters' : ' yet' }}.
    </p>
    <ol
      class="grid gap-2"
      aria-label="Prompts"
    >
      <li
        v-for="p in prompts"
        :key="p.id"
        class="grid gap-1 rounded-box border border-base-300 p-3"
        data-testid="prompt"
      >
        <p class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted">
          <span class="tabular-nums">{{ dateTime(p.startedAt) }}</span>
          <RouterLink
            v-if="sourcePage(p.source, p.sourceId)"
            :to="sourcePage(p.source, p.sourceId)!"
            class="link"
          >
            {{ sourceLabel(p.source) }} #{{ p.sourceId }}
          </RouterLink>
          <span v-else>{{ sourceLabel(p.source) }} #{{ p.sourceId }}</span>
          <span
            v-if="p.step"
            class="badge badge-outline badge-sm"
          >{{ p.step }}</span>
          <span v-if="p.model">{{ p.model }}{{ p.effort ? ` · ${p.effort}` : '' }}</span>
          <span
            class="badge badge-sm"
            :class="p.outcome ? outcomeTone[p.outcome] ?? 'badge-ghost' : 'badge-info'"
          >{{ p.outcome ?? 'running' }}</span>
          <span
            v-if="took(p)"
            class="tabular-nums"
          >{{ took(p) }}</span>
          <span v-if="p.resume">resumed</span>
          <span class="tabular-nums">{{ chars(p.promptChars) }} → {{ chars(p.replyChars) }} chars</span>
        </p>
        <RouterLink
          :to="{ name: 'prompt', params: { id: p.id } }"
          class="line-clamp-3 whitespace-pre-wrap break-words font-mono text-xs hover:underline"
        >
          {{ p.preview }}
        </RouterLink>
        <p
          v-if="p.error"
          class="text-sm text-error"
        >
          {{ p.error }}
        </p>
      </li>
    </ol>
    <div v-if="hasMore">
      <AgButton
        size="sm"
        variant="ghost"
        @click="older"
      >
        Older prompts
      </AgButton>
    </div>
  </section>
</template>
