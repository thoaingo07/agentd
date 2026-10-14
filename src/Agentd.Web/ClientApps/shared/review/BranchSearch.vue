<script setup lang="ts">
// Search beyond the diff (review-sessions.md §2): file names or text across the whole repository at the review's head
// commit. A changed file opens its diff on the page; any other file opens here, read-only, at the line found.
// "/" focuses the box, Esc clears it.
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ApiError, get } from '../api/http'
import type { ReviewFile, ReviewSearch, ReviewSearchHit } from '../api/types'
import { fileName, folder } from '../utils/paths'

const props = defineProps<{ sessionId: number; changed: readonly string[] }>()
const emit = defineEmits<{ openDiff: [path: string] }>()

const box = ref<HTMLInputElement | null>(null)
const q = ref('')
const kind = ref<'file' | 'text'>('file')
const matchCase = ref(false)
const results = ref<ReviewSearch | null>(null)
const error = ref<string | null>(null)
const viewing = ref<{ file: ReviewFile; line: number | null } | null>(null)
const changedSet = computed(() => new Set(props.changed))
let debounce: ReturnType<typeof setTimeout> | undefined
let asked = 0

async function search(): Promise<void> {
  const text = q.value.trim()
  const mine = ++asked
  if (!text) {
    results.value = null
    return
  }

  try {
    const params = new URLSearchParams({ q: text, kind: kind.value, limit: '50' })
    if (kind.value === 'text') params.set('ignoreCase', String(!matchCase.value))
    const found = await get<ReviewSearch>(`/api/reviews/${props.sessionId}/search?${params}`)
    if (mine === asked) {
      results.value = found
      error.value = null
    }
  } catch (err) {
    if (mine === asked) error.value = err instanceof ApiError ? err.message : 'The search failed.'
  }
}

watch([q, kind, matchCase], () => {
  clearTimeout(debounce)
  debounce = setTimeout(() => void search(), 250)
})

/** A changed file: its diff on the page. Anything else: read here, at the line found. */
async function open(path: string, line: number | null = null): Promise<void> {
  if (changedSet.value.has(path)) {
    emit('openDiff', path)
    return
  }

  try {
    viewing.value = { file: await get<ReviewFile>(`/api/reviews/${props.sessionId}/file?path=${encodeURIComponent(path)}`), line }
    await nextTick()
    document.getElementById(`search-line-${line}`)?.scrollIntoView?.({ block: 'center' })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'The file can\'t be read.'
  }
}

/** The snippet in three parts, so the match is marked without any HTML from the server. */
const parts = (h: ReviewSearchHit) => [h.snippet.slice(0, h.matchStart), h.snippet.slice(h.matchStart, h.matchStart + h.matchLength), h.snippet.slice(h.matchStart + h.matchLength)]
const lines = computed(() => viewing.value?.file.text.split('\n') ?? [])

function onKey(e: KeyboardEvent): void {
  const typing = e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable="true"]')
  if (e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault()
    box.value?.focus()
  }
}

function clear(): void {
  q.value = ''
  results.value = null
  viewing.value = null
}

onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  clearTimeout(debounce)
})
</script>

<template>
  <div
    class="grid min-w-0 gap-2"
    data-testid="branch-search"
  >
    <div class="flex flex-wrap items-center gap-2">
      <input
        ref="box"
        v-model="q"
        type="search"
        class="input input-sm min-w-0 flex-1"
        placeholder="Search the whole branch ( / )"
        aria-label="Search the whole branch"
        @keydown.esc="clear"
      >
      <div
        class="join"
        role="radiogroup"
        aria-label="Search for"
      >
        <button
          v-for="k in [{ v: 'file', t: 'Files' }, { v: 'text', t: 'Text' }] as const"
          :key="k.v"
          type="button"
          role="radio"
          :aria-checked="kind === k.v"
          class="btn btn-sm join-item"
          :class="kind === k.v ? 'btn-primary' : ''"
          @click="kind = k.v"
        >
          {{ k.t }}
        </button>
      </div>
      <label
        v-if="kind === 'text'"
        class="ag-meta flex items-center gap-1"
      >
        <input
          v-model="matchCase"
          type="checkbox"
          class="checkbox checkbox-xs"
        > Match case
      </label>
    </div>
    <p
      v-if="error"
      class="text-sm text-error"
      role="alert"
    >
      {{ error }}
    </p>
    <div
      v-if="results && q.trim()"
      class="ag-pane max-h-80 overflow-y-auto p-1"
      aria-label="Search results"
    >
      <p
        v-if="!results.files.length && !results.hits.length"
        class="ag-meta p-2"
      >
        Nothing at this commit matches.
      </p>
      <button
        v-for="path in results.files"
        :key="path"
        type="button"
        class="ag-list-row"
        data-testid="search-file"
        @click="open(path)"
      >
        <span class="min-w-0 flex-1 truncate font-mono text-xs"><strong>{{ fileName(path) }}</strong> <span class="text-muted">{{ folder(path, 4) }}</span></span>
        <span
          v-if="changedSet.has(path)"
          class="ag-count"
        >changed</span>
      </button>
      <button
        v-for="h in results.hits"
        :key="`${h.path}:${h.line}`"
        type="button"
        class="ag-list-row grid"
        data-testid="search-hit"
        @click="open(h.path, h.line)"
      >
        <span class="ag-meta truncate">{{ h.path }}:{{ h.line }}<template v-if="changedSet.has(h.path)"> · changed</template></span>
        <span class="ag-snippet">{{ parts(h)[0] }}<mark class="ag-mark">{{ parts(h)[1] }}</mark>{{ parts(h)[2] }}</span>
      </button>
    </div>
    <section
      v-if="viewing"
      class="grid gap-2"
      aria-label="File at the review's commit"
      data-testid="search-viewer"
    >
      <div class="flex items-center gap-2">
        <span class="min-w-0 flex-1 truncate font-mono text-sm">{{ viewing.file.path }}</span>
        <span class="ag-meta">read-only, at the review's commit</span>
        <button
          type="button"
          class="btn btn-ghost btn-xs"
          @click="viewing = null"
        >
          Close
        </button>
      </div>
      <p
        v-if="viewing.file.binary"
        class="ag-meta"
      >
        A binary file: not shown.
      </p>
      <div
        v-else
        class="ag-code"
      >
        <div
          v-for="(text, i) in lines"
          :id="`search-line-${i + 1}`"
          :key="i"
          class="ag-code-line"
          :class="{ 'is-hit': viewing.line === i + 1 }"
        >
          <span class="text-right text-muted select-none">{{ i + 1 }}</span><span>{{ text }}</span>
        </div>
        <p
          v-if="viewing.file.truncated"
          class="ag-meta px-2 py-1"
        >
          … the rest of this file is too big to show here.
        </p>
      </div>
    </section>
  </div>
</template>
