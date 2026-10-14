<script setup lang="ts">
import { computed, ref } from 'vue'
import { ApiError, get } from '../api/http'
import type { GuideNote, ReviewComment, ReviewFile, ReviewFinding } from '../api/types'
import CommitFileView from './CommitFileView.vue'
import type { DiffFile, DiffLine } from '../utils/diff'
import { isGenerated } from '../utils/generated'
import { AgButton } from '../components/ui'
import FindingCard from './FindingCard.vue'

// One file of the change with its findings, comments and (in a guided review) the reviewer's notes at their lines; click a
// line number to comment there. `guided`: the guided review's right pane, which shows the path itself: no frame, the gap
// between hunks as "N unmodified lines", and long unchanged runs folded (a click unfolds them).
const props = defineProps<{ file: DiffFile; findings: ReviewFinding[]; comments: ReviewComment[]; readonly?: boolean; notes?: GuideNote[]; guided?: boolean; sessionId?: number }>()
const emit = defineEmits<{
  decide: [n: number, decision: 'kept' | 'dropped' | 'edited', text?: string]
  comment: [line: number, text: string]
  ask: [line: number, text: string]
  removeComment: [id: number]
  explainFile: []
  explainFinding: [finding: ReviewFinding]
}>()
const tint = { add: 'bg-success/10', del: 'bg-error/10', context: '' } as const
/** The guided view's left edge on changed lines, like Plannotator's. */
const accent = { add: 'border-l-2 border-success', del: 'border-l-2 border-error', context: 'border-l-2 border-transparent' } as const
const mark = { add: '+', del: '-', context: ' ' } as const
const at = ref<number | null>(null)
const generated = computed(() => isGenerated(props.file.newPath))
/** A generated file's diff is hidden until asked, unless the reviewer found something in it. */
const shown = ref(!generated.value || props.findings.length > 0)
const draft = ref('')

/**
 * View file: the whole file at the review's head commit (a deleted file: at its base), with the lines this change
 * added marked (deleted: the lines it removed), scrolled to the first; Back to the diff returns. Reading is fine on a
 * sent review too.
 */
const whole = ref<ReviewFile | null>(null)
const viewingFile = ref(false)
const wholeError = ref<string | null>(null)
const deleted = computed(() => props.file.status === 'deleted')
const changedLines = computed(() => new Set(props.file.hunks.flatMap((h) => h.lines
  .filter((l) => (deleted.value ? l.kind === 'del' : l.kind === 'add'))
  .map((l) => (deleted.value ? l.oldNo : l.newNo))
  .filter((n): n is number => n != null))))
const firstChange = computed(() => Math.min(...changedLines.value, Number.MAX_SAFE_INTEGER))
async function viewFile(): Promise<void> {
  viewingFile.value = !viewingFile.value
  if (!viewingFile.value || whole.value || props.sessionId == null) return
  try {
    const path = deleted.value ? props.file.oldPath : props.file.newPath
    whole.value = await get<ReviewFile>(`/api/reviews/${props.sessionId}/file?path=${encodeURIComponent(path)}&ref=${deleted.value ? 'base' : 'head'}`)
    wholeError.value = null
  } catch (err) {
    wholeError.value = err instanceof ApiError ? err.message : 'The file can\'t be read.'
  }
}

/** Findings and comments by the new-side line they're on; ones off the diff go at the top. */
const byLine = computed(() => {
  const shown = new Set(props.file.hunks.flatMap((h) => h.lines.map((l) => l.newNo)).filter((n): n is number => n != null))
  const findings = new Map<number, ReviewFinding[]>()
  const comments = new Map<number, ReviewComment[]>()
  const loose: ReviewFinding[] = []
  for (const f of props.findings) {
    if (f.line != null && shown.has(f.line)) findings.set(f.line, [...(findings.get(f.line) ?? []), f])
    else loose.push(f)
  }
  for (const c of props.comments) if (c.line != null) comments.set(c.endLine ?? c.line, [...(comments.get(c.endLine ?? c.line) ?? []), c])
  const notes = new Map<number, GuideNote[]>()
  for (const n of props.notes ?? []) if (shown.has(n.line)) notes.set(n.line, [...(notes.get(n.line) ?? []), n])
  return { findings, comments, loose, notes }
})

/** Unchanged runs at least this long fold in the guided view, keeping this many lines on each side. */
const foldAt = 8
const foldKeep = 3
const unfolded = ref(new Set<string>())
type Row = { kind: 'line'; line: DiffLine } | { kind: 'fold'; id: string; lines: DiffLine[] }

/** A line that carries a note, finding, comment or the comment being written never folds away. */
const busy = (n: number | null) => n != null && (byLine.value.findings.has(n) || byLine.value.comments.has(n) || byLine.value.notes.has(n) || at.value === n)

/** Each hunk's rows: its lines, with long unchanged runs folded in the guided view. */
const rows = computed<Row[][]>(() => props.file.hunks.map((h, hi) => {
  const lines = (list: DiffLine[]): Row[] => list.map((line) => ({ kind: 'line', line }))
  if (!props.guided) return lines(h.lines)
  const out: Row[] = []
  let run: DiffLine[] = []
  const flush = () => {
    const id = `${hi}:${run[0]?.newNo ?? run[0]?.oldNo}`
    if (run.length >= foldAt && !unfolded.value.has(id)) {
      out.push(...lines(run.slice(0, foldKeep)), { kind: 'fold', id, lines: run.slice(foldKeep, -foldKeep) }, ...lines(run.slice(-foldKeep)))
    } else {
      out.push(...lines(run))
    }
    run = []
  }
  for (const l of h.lines) {
    if (l.kind === 'context' && !busy(l.newNo)) {
      run.push(l)
    } else {
      flush()
      out.push({ kind: 'line', line: l })
    }
  }
  flush()
  return out
}))

/** The unchanged lines before each hunk (from its header: where it starts on the old side, after the last one ended). */
const gaps = computed(() => {
  let end = 1
  return props.file.hunks.map((h) => {
    const m = /^@@ -(\d+)(?:,(\d+))? /.exec(h.header)
    if (!m) return 0
    const start = Number(m[1])
    const count = m[2] === undefined ? 1 : Number(m[2])
    const gap = Math.max(0, start - end)
    end = start + count
    return gap
  })
})
const unmodified = (n: number) => `${n} unmodified line${n === 1 ? '' : 's'}`

function unfold(id: string): void {
  unfolded.value = new Set([...unfolded.value, id])
}

function startComment(line: number | null): void {
  if (props.readonly || line == null) return
  at.value = line
  draft.value = ''
}

function send(kind: 'comment' | 'ask'): void {
  if (at.value == null || !draft.value.trim()) return
  if (kind === 'comment') emit('comment', at.value, draft.value.trim())
  else emit('ask', at.value, draft.value.trim())
  at.value = null
}
</script>

<template>
  <section
    :id="`file-${file.newPath}`"
    class="grid gap-2"
    :class="guided ? '' : 'rounded-box border border-base-300'"
  >
    <h3
      v-if="!guided || generated || !readonly || sessionId != null"
      class="flex items-center gap-2 font-mono text-xs"
      :class="guided ? '-mt-2 justify-end' : 'border-b border-base-300 px-3 py-2'"
    >
      <template v-if="!guided">
        <span class="font-semibold">{{ file.newPath }}</span>
        <span class="text-success">+{{ file.additions }}</span><span class="text-error">−{{ file.deletions }}</span>
      </template>
      <span
        v-if="generated"
        class="badge badge-sm font-sans"
      >generated</span>
      <AgButton
        v-if="sessionId != null && !file.binary"
        size="sm"
        variant="ghost"
        class="ml-auto font-sans"
        :aria-pressed="viewingFile"
        data-testid="view-file"
        @click="viewFile"
      >
        {{ viewingFile ? '↩ Back to the diff' : '📄 View file' }}
      </AgButton>
      <AgButton
        v-if="!readonly"
        size="sm"
        variant="ghost"
        class="font-sans"
        :class="sessionId != null && !file.binary ? '' : 'ml-auto'"
        data-testid="explain-file"
        @click="emit('explainFile')"
      >
        ❓ Explain this file
      </AgButton>
    </h3>
    <div
      v-if="byLine.loose.length"
      class="grid gap-2 px-3"
    >
      <FindingCard
        v-for="f in byLine.loose"
        :key="f.number"
        :finding="f"
        :readonly="readonly"
        @decide="(d, t) => emit('decide', f.number, d, t)"
        @explain="emit('explainFinding', f)"
      />
    </div>
    <div
      v-if="viewingFile"
      class="grid gap-1 px-3 pb-3"
      data-testid="whole-file"
    >
      <p class="ag-meta">
        {{ deleted ? 'The file before this change deleted it (at the base commit); the removed lines are marked.' : 'The whole file at the review\'s commit; the lines this change added are marked.' }}
      </p>
      <p
        v-if="wholeError"
        class="text-sm text-error"
        role="alert"
      >
        {{ wholeError }}
      </p>
      <CommitFileView
        v-else-if="whole"
        :text="whole.text"
        :truncated="whole.truncated"
        :binary="whole.binary"
        :highlight="changedLines"
        :focus="firstChange === Number.MAX_SAFE_INTEGER ? null : firstChange"
      />
      <p
        v-else
        class="ag-meta"
      >
        Loading…
      </p>
    </div>
    <p
      v-else-if="!shown"
      class="flex flex-wrap items-center gap-2 px-3 pb-2 text-sm"
      data-testid="generated-hidden"
    >
      A generated file (a lockfile, built output or generated code): its diff is hidden.
      <AgButton
        size="sm"
        variant="outline"
        @click="shown = true"
      >
        Show the diff
      </AgButton>
    </p>
    <p
      v-else-if="file.binary"
      class="px-3 pb-2 text-sm text-muted"
    >
      Binary file.
    </p>
    <table
      v-for="(h, hi) in shown && !viewingFile ? file.hunks : []"
      :key="hi"
      class="w-full border-collapse font-mono text-[12px] leading-5"
      :class="guided ? 'overflow-hidden rounded-box bg-base-100' : ''"
    >
      <tbody>
        <tr
          v-if="!guided"
          class="bg-base-200 text-muted"
        >
          <td
            :colspan="guided ? 2 : 3"
            class="px-2"
          >
            {{ h.header }}
          </td>
        </tr>
        <tr
          v-else-if="gaps[hi]"
          class="bg-base-200 text-muted"
          data-testid="unmodified-gap"
        >
          <td
            :colspan="guided ? 2 : 3"
            class="px-3 py-1 font-sans text-xs"
          >
            {{ unmodified(gaps[hi]!) }}
          </td>
        </tr>
        <template
          v-for="(row, ri) in rows[hi]"
          :key="ri"
        >
          <tr
            v-if="row.kind === 'fold'"
            class="bg-base-200 text-muted"
          >
            <td :colspan="guided ? 2 : 3">
              <button
                type="button"
                class="w-full px-3 py-1 text-left font-sans text-xs hover:text-base-content"
                :aria-label="`Show ${unmodified(row.lines.length)}`"
                data-testid="unmodified-fold"
                @click="unfold(row.id)"
              >
                {{ unmodified(row.lines.length) }}
              </button>
            </td>
          </tr>
          <template v-else>
            <tr :class="tint[row.line.kind]">
              <td
                class="w-12 select-none pr-2 text-right tabular-nums"
                :class="guided ? accent[row.line.kind] : ''"
              >
                <button
                  v-if="row.line.newNo != null && !readonly"
                  type="button"
                  class="text-muted hover:text-base-content"
                  :aria-label="`Comment on line ${row.line.newNo}`"
                  @click="startComment(row.line.newNo)"
                >
                  {{ row.line.newNo }}
                </button>
                <span
                  v-else
                  class="text-muted"
                >{{ row.line.newNo ?? row.line.oldNo ?? '' }}</span>
              </td>
              <td
                v-if="!guided"
                class="w-4 select-none text-muted"
                aria-hidden="true"
              >
                {{ mark[row.line.kind] }}
              </td>
              <td
                class="whitespace-pre-wrap break-all pr-2"
                :class="guided ? 'pl-2' : ''"
              >
                {{ row.line.text }}
              </td>
            </tr>
            <tr v-if="row.line.newNo != null && (byLine.findings.has(row.line.newNo) || byLine.comments.has(row.line.newNo) || byLine.notes.has(row.line.newNo) || at === row.line.newNo)">
              <td
                :colspan="guided ? 2 : 3"
                class="bg-base-200/40 p-2"
              >
                <div class="grid gap-2 font-sans">
                  <p
                    v-for="(n, ni) in byLine.notes.get(row.line.newNo) ?? []"
                    :key="`n${ni}`"
                    class="flex items-start gap-2 rounded-box border p-2 text-sm"
                    :class="n.critical ? 'border-warning bg-warning/10' : 'border-info/40 bg-info/5'"
                    data-testid="guide-note"
                  >
                    <span aria-hidden="true">{{ n.critical ? '⚠️' : '💡' }}</span>
                    <span><span class="sr-only">{{ n.critical ? 'Needs care: ' : 'Note: ' }}</span>{{ n.text }}</span>
                  </p>
                  <FindingCard
                    v-for="f in byLine.findings.get(row.line.newNo) ?? []"
                    :key="f.number"
                    :finding="f"
                    :readonly="readonly"
                    @decide="(d, t) => emit('decide', f.number, d, t)"
                    @explain="emit('explainFinding', f)"
                  />
                  <div
                    v-for="c in byLine.comments.get(row.line.newNo) ?? []"
                    :key="c.id"
                    class="flex items-start gap-2 rounded-box border border-base-300 bg-base-100 p-2 text-sm"
                    data-testid="line-comment"
                  >
                    <span aria-hidden="true">💬</span>
                    <span class="whitespace-pre-wrap">{{ c.text }}</span>
                    <span class="text-xs text-muted">· {{ c.author }}</span>
                    <button
                      v-if="!readonly"
                      type="button"
                      class="ml-auto text-xs text-muted hover:text-error"
                      @click="emit('removeComment', c.id)"
                    >
                      Remove
                    </button>
                  </div>
                  <div
                    v-if="at === row.line.newNo"
                    class="grid gap-2"
                  >
                    <textarea
                      v-model="draft"
                      class="textarea w-full text-sm"
                      rows="2"
                      :aria-label="`Your comment or question on line ${row.line.newNo}`"
                    />
                    <div class="flex gap-2">
                      <AgButton
                        size="sm"
                        @click="send('comment')"
                      >
                        Comment
                      </AgButton>
                      <AgButton
                        size="sm"
                        variant="outline"
                        @click="send('ask')"
                      >
                        Ask
                      </AgButton>
                      <AgButton
                        size="sm"
                        variant="ghost"
                        @click="at = null"
                      >
                        Cancel
                      </AgButton>
                    </div>
                  </div>
                </div>
              </td>
            </tr>
          </template>
        </template>
      </tbody>
    </table>
  </section>
</template>
