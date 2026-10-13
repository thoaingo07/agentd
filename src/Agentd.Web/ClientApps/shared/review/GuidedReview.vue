<script setup lang="ts">
import UBadge from '@nuxt/ui/components/Badge.vue'
import UButton from '@nuxt/ui/components/Button.vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { GuideChapter, GuideNote, ReviewFinding, ReviewGuide } from '../api/types'
import type { DiffFile } from '../utils/diff'
import { fileName, folder } from '../utils/paths'

// The guided review (docs/architect/review-sessions.md §2a): the reviewer's guide to the change, one chapter at a time.
// Step 0 is the overview; each chapter shows its story beside its own files' real diffs (rendered by the page through the
// `file` slot, with the chapter's notes). Reviewed chapters are remembered in this browser.
const props = defineProps<{ sessionId: number; guide: ReviewGuide; files: DiffFile[]; findings: ReviewFinding[] }>()
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
    class="grid gap-4 lg:grid-cols-[18rem_minmax(0,1fr)]"
    data-testid="guided-review"
  >
    <nav
      aria-label="Guide"
      class="min-w-0"
    >
      <div class="sticky top-4 grid gap-3 text-sm">
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
              class="relative size-7 rounded-md border text-xs tabular-nums"
              :class="step === i - 1 ? 'border-primary bg-primary font-semibold text-primary-content' : reviewed.has(i - 1) ? 'border-success/50 bg-success/10' : 'border-base-300'"
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

        <template v-if="chapter">
          <p class="flex items-center gap-2 text-xs text-muted tabular-nums">
            {{ String(step).padStart(2, '0') }} / {{ String(chapters.length).padStart(2, '0') }}
            <UBadge
              :color="chapter.attention === 'skim' ? 'neutral' : 'primary'"
              :variant="chapter.attention === 'skim' ? 'subtle' : 'solid'"
              size="sm"
            >
              {{ chapter.attention === 'skim' ? 'Skim' : 'Core' }}
            </UBadge>
            <span v-if="critical(chapter)">⚠️ {{ critical(chapter) === 1 ? '1 spot needs' : `${critical(chapter)} spots need` }} care</span>
          </p>
          <h2 class="text-base font-semibold">
            {{ chapter.title }}
          </h2>
          <p
            v-if="chapter.why"
            class="whitespace-pre-wrap"
          >
            {{ chapter.why }}
          </p>
          <div
            v-if="chapter.focus?.length"
            class="grid gap-1"
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
          <ul class="grid gap-1 text-xs">
            <li
              v-for="f in chapter.files"
              :key="f.path"
              class="min-w-0"
            >
              <a
                :href="`#file-${f.path}`"
                :title="f.path"
                class="grid min-w-0 font-mono hover:underline"
              >
                <span class="truncate">{{ fileName(f.path) }}</span>
                <span class="truncate text-muted">{{ folder(f.path) }}</span>
              </a>
            </li>
          </ul>
          <div class="flex flex-wrap gap-2">
            <UButton
              size="sm"
              variant="outline"
              color="neutral"
              @click="go(step - 1)"
            >
              ← {{ step === 1 ? 'Overview' : 'Previous' }}
            </UButton>
            <UButton
              v-if="step < chapters.length"
              size="sm"
              variant="outline"
              color="neutral"
              @click="go(step + 1)"
            >
              Next →
            </UButton>
          </div>
        </template>
        <p
          v-else
          class="text-xs text-muted"
        >
          {{ reviewed.size }} of {{ chapters.length }} chapters reviewed · <kbd>]</kbd> next · <kbd>[</kbd> previous
        </p>
      </div>
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
              <span class="text-xs text-muted tabular-nums">{{ reviewed.has(i + 1) ? '✓' : String(i + 1).padStart(2, '0') }}</span>
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
      <template
        v-for="f in chapter.files"
        :key="f.path"
      >
        <p
          v-if="f.summary"
          class="text-sm"
        >
          <span class="font-mono text-xs text-muted">{{ fileName(f.path) }}</span> · {{ f.summary }}
        </p>
        <slot
          v-if="byPath.get(f.path)"
          name="file"
          :file="byPath.get(f.path)!"
          :notes="notesFor(f.path)"
        />
        <p
          v-else
          class="text-sm text-muted"
        >
          {{ f.path }}: no diff to show (too big, or binary).
        </p>
      </template>
      <footer class="flex flex-wrap items-center gap-2 border-t border-base-300 pt-3">
        <UButton @click="done()">
          {{ step < chapters.length ? 'Mark reviewed and continue' : 'Mark reviewed · back to the overview' }}
        </UButton>
        <UButton
          v-if="step < chapters.length"
          variant="ghost"
          color="neutral"
          @click="go(step + 1)"
        >
          Next: {{ chapters[step]!.title }} →
        </UButton>
      </footer>
    </div>
  </div>
</template>
