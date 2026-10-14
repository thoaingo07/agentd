<script setup lang="ts">
import UBadge from '@nuxt/ui/components/Badge.vue'
import UButton from '@nuxt/ui/components/Button.vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { GuideChapter, GuideNote, ReviewFinding, ReviewGuide } from '../api/types'
import type { DiffFile } from '../utils/diff'
import { fileName } from '../utils/paths'
import ReviewFileTree, { type FileMarks } from './ReviewFileTree.vue'
import PaneResizer from '../components/PaneResizer.vue'
import { usePaneWidth } from '../utils/paneWidth'

// The guided review (docs/architect/review-sessions.md §2a): the reviewer's guide to the change, one chapter at a time.
// Step 0 is the overview; a chapter is two panes, like Plannotator's guided review: its story and file cards on the left,
// the picked file's real diff on the right (rendered by the page through the `file` slot, with the chapter's notes).
// Reviewed chapters are remembered in this browser. Explain this chapter asks the reviewer's agent (the page does the
// asking: the answer lands in its Questions). `by`: who wrote the guide (the reviewer and its model).
const props = defineProps<{ sessionId: number; guide: ReviewGuide; files: DiffFile[]; findings: ReviewFinding[]; readonly?: boolean; by?: string }>()
/** The chapter pane's width (wide screens), dragged by its edge and kept per browser. */
const leftWidth = usePaneWidth('guide-chapter', 380, 240, 720)
const emit = defineEmits<{ explainChapter: [chapter: GuideChapter] }>()
defineSlots<{ file(props: { file: DiffFile; notes: GuideNote[] }): unknown }>()

const step = ref(0)
const storageKey = computed(() => `agentd.guide.${props.sessionId}`)
const reviewed = ref<Set<number>>(new Set())
watch(storageKey, (key) => {
  try {
    reviewed.value = new Set(JSON.parse(window.localStorage.getItem(key) ?? '[]') as number[])
  } catch {
    reviewed.value = new Set()
  }
}, { immediate: true })
function remember(): void {
  try {
    window.localStorage.setItem(storageKey.value, JSON.stringify([...reviewed.value]))
  } catch {
    // Storage unavailable: progress lasts for this page only.
  }
}

const chapters = computed(() => props.guide.chapters)
const chapter = computed<GuideChapter | null>(() => (step.value > 0 ? chapters.value[step.value - 1] ?? null : null))
const byPath = computed(() => new Map(props.files.map((f) => [f.newPath, f])))
const notesFor = (path: string) => (props.guide.notes ?? []).filter((n) => n.file === path)
const paths = (c: GuideChapter) => c.files.map((f) => f.path)
const critical = (c: GuideChapter) => (props.guide.notes ?? []).filter((n) => n.critical && paths(c).includes(n.file)).length
const findingsIn = (c: GuideChapter) => props.findings.filter((f) => f.file && paths(c).includes(f.file)).length
/** pi-review's human reviewer callouts, in words. */
const calloutLabels: Record<string, string> = {
  migration: 'Adds a database migration',
  dependency: 'Adds or changes a dependency',
  auth: 'Changes auth or permissions',
  contract: 'Breaks a contract (API, schema)',
  destructive: 'Irreversible or destructive',
  'feature-flag': 'Feature flags',
  config: 'Changes a configuration default',
}
const firstUnread = computed(() => chapters.value.findIndex((_, i) => !reviewed.value.has(i + 1)) + 1)

/** The header above a chapter: the overview's first sentence as its title, the rest under it. */
const headline = computed(() => {
  const text = props.guide.overview.trim()
  const end = text.search(/[.!?](\s|$)/)
  return end < 0 ? { title: text, lede: '' } : { title: text.slice(0, end + 1), lede: text.slice(end + 1).trim() }
})

/** The file shown on the right: the first of the chapter until another card is picked. */
const picked = ref<string | null>(null)
const current = computed(() => chapter.value?.files.find((f) => f.path === picked.value) ?? chapter.value?.files[0] ?? null)
const storyOpen = ref(true)
watch(step, () => {
  picked.value = null
  storyOpen.value = true
})
const counts = (path: string) => byPath.value.get(path)
/** The chapter's files for its tree: their diffs, or an empty stand-in for one too big to show. */
const chapterFiles = computed<DiffFile[]>(() => (chapter.value?.files ?? []).map((f) => byPath.value.get(f.path)
  ?? { oldPath: f.path, newPath: f.path, status: 'modified', binary: false, hunks: [], additions: 0, deletions: 0 }))
const noneViewed: ReadonlySet<string> = new Set()
const marks = (path: string): FileMarks => {
  const found = props.findings.filter((f) => f.file === path)
  return { severity: found.some((f) => f.severity === 'breaks') ? 'breaks' : found.length ? 'performance' : null, findings: found.length, comments: 0 }
}
const pad = (n: number) => String(n).padStart(2, '0')
/** The whole folder of a path, with its slash ("src/export/"), for the path above the diff. */
const dirOf = (path: string) => path.slice(0, path.lastIndexOf('/') + 1)

function go(to: number): void {
  step.value = Math.max(0, Math.min(chapters.value.length, to))
  window.scrollTo?.({ top: 0 })
}

/** Mark this chapter reviewed, then the next one (or the overview after the last). */
function done(): void {
  reviewed.value = new Set([...reviewed.value, step.value])
  remember()
  go(step.value < chapters.value.length ? step.value + 1 : 0)
}

/** The Reviewed checkbox: marks or unmarks this chapter without moving on. */
function toggleReviewed(): void {
  const next = new Set(reviewed.value)
  if (!next.delete(step.value)) next.add(step.value)
  reviewed.value = next
  remember()
}

/** ] next chapter, [ previous (not while typing). */
function onKey(e: KeyboardEvent): void {
  const typing = e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable="true"]')
  if (e.ctrlKey || e.metaKey || e.altKey || typing) return
  if (e.key === ']') go(step.value + 1)
  else if (e.key === '[') go(step.value - 1)
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <div
    class="grid min-w-0 gap-4"
    data-testid="guided-review"
  >
    <nav
      aria-label="Guide"
      class="flex flex-wrap items-center gap-x-3 gap-y-2 text-sm"
    >
      <ol
        class="flex flex-wrap gap-1"
        aria-label="Chapters"
      >
        <li
          v-for="i in chapters.length + 1"
          :key="i"
        >
          <button
            type="button"
            class="relative size-7 cursor-pointer rounded-md border text-xs tabular-nums transition-colors"
            :class="step === i - 1 ? 'border-primary bg-primary font-semibold text-primary-content' : reviewed.has(i - 1) ? 'border-success/50 bg-success/10 hover:border-primary' : 'border-base-300 hover:border-primary hover:bg-base-200'"
            :title="i === 1 ? 'Overview' : `${i - 1}. ${chapters[i - 2]!.title}${reviewed.has(i - 1) ? ' ✓' : ''}`"
            :aria-current="step === i - 1 ? 'step' : undefined"
            :aria-label="i === 1 ? 'Overview' : `Chapter ${i - 1}: ${chapters[i - 2]!.title}${reviewed.has(i - 1) ? ' (reviewed)' : ''}`"
            @click="go(i - 1)"
          >
            {{ i - 1 }}
            <span
              v-if="i > 1 && critical(chapters[i - 2]!)"
              class="absolute -top-1 -right-1 size-2 rounded-full bg-warning"
              aria-hidden="true"
            />
          </button>
        </li>
      </ol>
      <p class="text-xs text-muted">
        {{ reviewed.size }} of {{ chapters.length }} chapters reviewed · <kbd>]</kbd> next · <kbd>[</kbd> previous
      </p>
    </nav>

    <div
      v-if="!chapter"
      class="grid max-w-3xl content-start gap-4"
      data-testid="guide-overview"
    >
      <h2 class="text-lg font-semibold">
        What this change does
      </h2>
      <p class="whitespace-pre-wrap">
        {{ guide.overview }}
      </p>
      <div
        v-if="guide.before || guide.after"
        class="grid gap-2 sm:grid-cols-2"
      >
        <div
          v-if="guide.before"
          class="rounded-box border border-base-300 p-3 text-sm"
        >
          <p class="text-xs font-semibold text-muted">
            Before
          </p>
          {{ guide.before }}
        </div>
        <div
          v-if="guide.after"
          class="rounded-box border border-base-300 p-3 text-sm"
        >
          <p class="text-xs font-semibold text-muted">
            After
          </p>
          {{ guide.after }}
        </div>
      </div>
      <section
        v-if="guide.callouts?.length"
        aria-labelledby="guide-callouts"
        class="grid gap-1 rounded-box border border-warning/50 bg-warning/10 p-3 text-sm"
        data-testid="guide-callouts"
      >
        <h3
          id="guide-callouts"
          class="font-semibold"
        >
          Heads-up for you
        </h3>
        <p class="text-xs">
          Not problems: things worth knowing before you approve.
        </p>
        <ul class="grid list-disc gap-0.5 pl-5">
          <li
            v-for="(c, ci) in guide.callouts"
            :key="ci"
          >
            <strong>{{ calloutLabels[c.kind] ?? c.kind }}:</strong> {{ c.detail }}
          </li>
        </ul>
      </section>
      <h3 class="font-semibold">
        Read it in {{ chapters.length }} chapter{{ chapters.length === 1 ? '' : 's' }}: the core first, what to skim last
      </h3>
      <ol class="grid gap-2">
        <li
          v-for="(c, i) in chapters"
          :key="i"
        >
          <button
            type="button"
            class="grid w-full gap-1 rounded-box border border-base-300 p-3 text-left text-sm hover:bg-base-200"
            data-testid="guide-chapter-card"
            @click="go(i + 1)"
          >
            <span class="flex flex-wrap items-center gap-2">
              <span class="text-xs text-muted tabular-nums">{{ reviewed.has(i + 1) ? '✓' : pad(i + 1) }}</span>
              <span class="font-medium">{{ c.title }}</span>
              <UBadge
                :color="c.attention === 'skim' ? 'neutral' : 'primary'"
                :variant="c.attention === 'skim' ? 'subtle' : 'solid'"
                size="sm"
              >
                {{ c.attention === 'skim' ? 'Skim' : 'Core' }}
              </UBadge>
              <span class="ml-auto text-xs text-muted">{{ c.files.length }} file{{ c.files.length === 1 ? '' : 's' }}{{ findingsIn(c) ? ` · ${findingsIn(c)} finding${findingsIn(c) === 1 ? '' : 's'}` : '' }}</span>
            </span>
            <span
              v-if="c.why"
              class="line-clamp-2 text-muted"
            >{{ c.why }}</span>
          </button>
        </li>
      </ol>
      <div>
        <UButton @click="go(firstUnread || 1)">
          {{ reviewed.size ? 'Continue' : 'Start reading' }}
        </UButton>
      </div>
    </div>

    <div
      v-else
      class="grid min-w-0 content-start gap-4"
      data-testid="guide-chapter"
    >
      <header class="grid max-w-4xl gap-1">
        <h2 class="text-xl font-semibold">
          {{ headline.title }}
        </h2>
        <p
          v-if="headline.lede"
          class="text-muted"
        >
          {{ headline.lede }}
        </p>
        <p class="font-mono text-xs text-muted">
          {{ chapters.length }} chapter{{ chapters.length === 1 ? '' : 's' }} · from {{ by ?? 'agentd' }}
        </p>
      </header>

      <div
        class="grid min-w-0 overflow-hidden rounded-box border border-base-300 bg-base-200/30 lg:grid-cols-[var(--guide-left)_minmax(0,1fr)]"
        :style="{ '--guide-left': `${leftWidth}px` }"
      >
        <section
          class="relative grid min-w-0 content-start gap-3 border-b border-base-300 p-4 lg:border-r lg:border-b-0"
          aria-labelledby="guide-chapter-title"
        >
          <PaneResizer
            v-model="leftWidth"
            :min="240"
            :max="720"
            :initial="380"
            label="Resize the chapter pane"
          />
          <div class="flex items-start gap-2">
            <h3
              id="guide-chapter-title"
              class="flex-1 text-lg font-semibold leading-snug"
            >
              {{ chapter.title }}
            </h3>
            <button
              type="button"
              class="btn btn-ghost btn-xs btn-square"
              :aria-expanded="storyOpen"
              :aria-label="storyOpen ? 'Hide the chapter\'s story and files' : 'Show the chapter\'s story and files'"
              @click="storyOpen = !storyOpen"
            >
              <svg
                class="size-4 transition-transform"
                :class="storyOpen ? '' : 'rotate-180'"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                aria-hidden="true"
              ><path
                stroke-linecap="round"
                stroke-linejoin="round"
                d="M6 15l6-6 6 6"
              /></svg>
            </button>
          </div>
          <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
            <span class="font-mono tabular-nums">{{ pad(step) }} / {{ pad(chapters.length) }}</span>
            <label class="flex cursor-pointer items-center gap-1.5">
              <input
                type="checkbox"
                class="checkbox checkbox-xs"
                :checked="reviewed.has(step)"
                data-testid="chapter-reviewed"
                @change="toggleReviewed"
              >
              Reviewed
            </label>
            <UBadge
              :color="chapter.attention === 'skim' ? 'neutral' : 'primary'"
              :variant="chapter.attention === 'skim' ? 'subtle' : 'solid'"
              size="sm"
            >
              {{ chapter.attention === 'skim' ? 'Skim' : 'Core' }}
            </UBadge>
            <span v-if="critical(chapter)">⚠️ {{ critical(chapter) === 1 ? '1 spot needs' : `${critical(chapter)} spots need` }} care</span>
          </div>

          <template v-if="storyOpen">
            <p
              v-if="chapter.why"
              class="whitespace-pre-wrap leading-relaxed"
            >
              {{ chapter.why }}
            </p>
            <div
              v-if="chapter.focus?.length"
              class="grid gap-1 text-sm"
            >
              <p class="text-xs font-semibold text-muted">
                While reading, check
              </p>
              <ul class="grid list-disc gap-0.5 pl-5">
                <li
                  v-for="(f, fi) in chapter.focus"
                  :key="fi"
                >
                  {{ f }}
                </li>
              </ul>
            </div>
            <div v-if="!readonly">
              <UButton
                size="sm"
                variant="soft"
                color="neutral"
                icon="i-lucide-message-circle-question"
                data-testid="explain-chapter"
                @click="emit('explainChapter', chapter)"
              >
                Explain this chapter
              </UButton>
            </div>
            <div class="rounded-box border border-base-300 bg-base-100 p-1">
              <ReviewFileTree
                bare
                label="Files in this chapter"
                :files="chapterFiles"
                :active="current?.path ?? null"
                :viewed="noneViewed"
                :marks="marks"
                @select="(path: string) => (picked = path)"
              />
            </div>
          </template>

          <footer class="flex flex-wrap items-center gap-2 border-t border-base-300 pt-3">
            <UButton
              size="sm"
              @click="done()"
            >
              {{ step < chapters.length ? 'Mark reviewed and continue' : 'Mark reviewed · back to the overview' }}
            </UButton>
            <UButton
              size="sm"
              variant="ghost"
              color="neutral"
              @click="go(step - 1)"
            >
              ← {{ step === 1 ? 'Overview' : 'Previous' }}
            </UButton>
            <UButton
              v-if="step < chapters.length"
              size="sm"
              variant="ghost"
              color="neutral"
              :title="`Next: ${chapters[step]!.title}`"
              @click="go(step + 1)"
            >
              Next →
            </UButton>
          </footer>
        </section>

        <section
          v-if="current"
          class="grid min-w-0 content-start gap-3 p-4"
          aria-label="The file"
          data-testid="guide-file"
        >
          <p
            v-if="current.summary"
            class="leading-relaxed"
          >
            {{ current.summary }}
          </p>
          <p class="flex flex-wrap items-baseline gap-x-2 border-b border-base-300 pb-2 font-mono text-sm">
            <span class="min-w-0 break-all"><span class="text-muted">{{ dirOf(current.path) }}</span><span class="font-semibold">{{ fileName(current.path) }}</span></span>
            <template v-if="counts(current.path)">
              <span class="text-success">+{{ counts(current.path)!.additions }}</span>
              <span class="text-error">-{{ counts(current.path)!.deletions }}</span>
            </template>
          </p>
          <slot
            v-if="byPath.get(current.path)"
            name="file"
            :file="byPath.get(current.path)!"
            :notes="notesFor(current.path)"
          />
          <p
            v-else
            class="text-sm text-muted"
          >
            {{ current.path }}: no diff to show (too big, or binary).
          </p>
        </section>
      </div>
    </div>
  </div>
</template>

