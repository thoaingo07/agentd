<script setup lang="ts">
import { computed, ref } from 'vue'
import type { GuideNote, ReviewComment, ReviewFinding } from '../api/types'
import type { DiffFile } from '../utils/diff'
import { isGenerated } from '../utils/generated'
import { AgButton } from '../components/ui'
import FindingCard from './FindingCard.vue'

// One file of the change with its findings, comments and (in a guided review) the reviewer's notes at their lines; click a
// line number to comment there.
const props = defineProps<{ file: DiffFile; findings: ReviewFinding[]; comments: ReviewComment[]; readonly?: boolean; notes?: GuideNote[] }>()
const emit = defineEmits<{
  decide: [n: number, decision: 'kept' | 'dropped' | 'edited', text?: string]
  comment: [line: number, text: string]
  ask: [line: number, text: string]
  removeComment: [id: number]
  explainFile: []
  explainFinding: [finding: ReviewFinding]
}>()
const tint = { add: 'bg-success/10', del: 'bg-error/10', context: '' } as const
const mark = { add: '+', del: '-', context: ' ' } as const
const at = ref<number | null>(null)
const generated = computed(() => isGenerated(props.file.newPath))
/** A generated file's diff is hidden until asked, unless the reviewer found something in it. */
const shown = ref(!generated.value || props.findings.length > 0)
const draft = ref('')

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
    class="grid gap-2 rounded-box border border-base-300"
  >
    <h3 class="flex items-center gap-2 border-b border-base-300 px-3 py-2 font-mono text-xs">
      <span class="font-semibold">{{ file.newPath }}</span>
      <span class="text-success">+{{ file.additions }}</span><span class="text-error">−{{ file.deletions }}</span>
      <span
        v-if="generated"
        class="badge badge-sm font-sans"
      >generated</span>
      <AgButton
        v-if="!readonly"
        size="sm"
        variant="ghost"
        class="ml-auto font-sans"
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
    <p
      v-if="!shown"
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
      v-for="(h, hi) in shown ? file.hunks : []"
      :key="hi"
      class="w-full border-collapse font-mono text-[12px] leading-5"
    >
      <tbody>
        <tr class="bg-base-200 text-muted">
          <td
            colspan="3"
            class="px-2"
          >
            {{ h.header }}
          </td>
        </tr>
        <template
          v-for="(l, li) in h.lines"
          :key="li"
        >
          <tr :class="tint[l.kind]">
            <td class="w-12 select-none pr-2 text-right tabular-nums">
              <button
                v-if="l.newNo != null && !readonly"
                type="button"
                class="text-muted hover:text-base-content"
                :aria-label="`Comment on line ${l.newNo}`"
                @click="startComment(l.newNo)"
              >
                {{ l.newNo }}
              </button>
              <span
                v-else
                class="text-muted"
              >{{ l.newNo ?? l.oldNo ?? '' }}</span>
            </td>
            <td
              class="w-4 select-none text-muted"
              aria-hidden="true"
            >
              {{ mark[l.kind] }}
            </td>
            <td class="whitespace-pre-wrap break-all pr-2">
              {{ l.text }}
            </td>
          </tr>
          <tr v-if="l.newNo != null && (byLine.findings.has(l.newNo) || byLine.comments.has(l.newNo) || byLine.notes.has(l.newNo) || at === l.newNo)">
            <td
              colspan="3"
              class="bg-base-200/40 p-2"
            >
              <div class="grid gap-2 font-sans">
                <p
                  v-for="(n, ni) in byLine.notes.get(l.newNo) ?? []"
                  :key="`n${ni}`"
                  class="flex items-start gap-2 rounded-box border p-2 text-sm"
                  :class="n.critical ? 'border-warning bg-warning/10' : 'border-info/40 bg-info/5'"
                  data-testid="guide-note"
                >
                  <span aria-hidden="true">{{ n.critical ? '⚠️' : '💡' }}</span>
                  <span><span class="sr-only">{{ n.critical ? 'Needs care: ' : 'Note: ' }}</span>{{ n.text }}</span>
                </p>
                <FindingCard
                  v-for="f in byLine.findings.get(l.newNo) ?? []"
                  :key="f.number"
                  :finding="f"
                  :readonly="readonly"
                  @decide="(d, t) => emit('decide', f.number, d, t)"
                  @explain="emit('explainFinding', f)"
                />
                <div
                  v-for="c in byLine.comments.get(l.newNo) ?? []"
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
                  v-if="at === l.newNo"
                  class="grid gap-2"
                >
                  <textarea
                    v-model="draft"
                    class="textarea w-full text-sm"
                    rows="2"
                    :aria-label="`Your comment or question on line ${l.newNo}`"
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
      </tbody>
    </table>
  </section>
</template>
