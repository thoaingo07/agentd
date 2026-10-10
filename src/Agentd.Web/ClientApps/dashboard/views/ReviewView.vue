<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { ReviewAsk, ReviewSent } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import DiffFileReview from '../../shared/review/DiffFileReview.vue'
import FindingCard from '../../shared/review/FindingCard.vue'
import { useReviewsStore } from '../stores/reviews'

// The review page (docs/architect/review-sessions.md §2): files, the diff with findings and comments at their lines,
// and the findings, your comments and a comment on the whole change at the side.
// `local`: agentd review's page on a laptop, which has its own Send bar.
const props = defineProps<{ id: number; local?: boolean }>()
const reviews = useReviewsStore()
const error = ref<string | null>(null)
const overall = ref('')
const sent = ref<ReviewSent | null>(null)
const copied = ref(false)
const sending = ref(false)
const s = computed(() => reviews.current?.session ?? null)
const readonly = computed(() => s.value?.status === 'Sent' || s.value?.status === 'Closed')
const counts = computed(() => ({
  breaks: s.value?.findings.filter((f) => f.severity === 'breaks').length ?? 0,
  performance: s.value?.findings.filter((f) => f.severity !== 'breaks').length ?? 0,
}))
const forFile = (path: string) => ({
  findings: s.value?.findings.filter((f) => f.file === path) ?? [],
  comments: reviews.current?.comments.filter((c) => c.file === path) ?? [],
})
const elsewhere = computed(() => s.value?.findings.filter((f) => !f.file || !reviews.files.some((x) => x.newPath === f.file)) ?? [])
const short = (sha?: string | null) => (sha ? sha.slice(0, 7) : '…')

watch(() => props.id, (id) => void reviews.open(id).catch((err) => (error.value = err instanceof ApiError ? err.message : 'This review can\'t be opened.')), { immediate: true })
onBeforeUnmount(() => reviews.close())

async function act(work: () => Promise<unknown>): Promise<void> {
  error.value = null
  try {
    await work()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  }
}

const decide = (n: number, d: 'kept' | 'dropped' | 'edited', t?: string) => act(() => reviews.decide(n, d, t))
const commentAt = (file: string, line: number, text: string) => act(() => reviews.comment({ file, line, text }))
const askAt = (file: string, line: number, text: string) => act(() => reviews.ask({ file, line, text }))
const overallSend = (kind: 'comment' | 'ask') => act(async () => {
  if (!overall.value.trim()) return
  if (kind === 'comment') await reviews.comment({ text: overall.value.trim() })
  else await reviews.ask({ text: overall.value.trim() })
  overall.value = ''
})
/** Fix it's progress, from the session's sentTo: fix:running, fix:<next review>:<commit>, fix:nochange, fix:failed. */
const fix = computed(() => {
  const to = s.value?.sentTo ?? ''
  if (!to.startsWith('fix:')) return null
  const [, state, commit] = to.split(':')
  return { state, next: Number(state) || null, commit }
})

async function sendTo(destination: 'pr' | 'text' | 'fix'): Promise<void> {
  if (destination === 'fix' && !window.confirm(`Fix it: an agent fixes what you kept, then agentd commits and pushes to ${s.value?.headRef}. Go ahead?`)) return
  sending.value = true
  await act(async () => (sent.value = await reviews.sendTo(destination)))
  sending.value = false
}

async function copy(text: string): Promise<void> {
  await navigator.clipboard?.writeText(text).catch(() => {})
  copied.value = true
}

/** Questions as conversations: each thread's first question, then its follow-ups. */
const threads = computed(() => {
  const asks = reviews.current?.asks ?? []
  return asks.filter((a) => a.threadId == null).map((root) => ({ root, asks: asks.filter((a) => a.id === root.id || a.threadId === root.id) }))
})
const replies = ref<Record<number, string>>({})
const followUp = (threadId: number) => act(async () => {
  const text = replies.value[threadId]?.trim()
  if (!text) return
  await reviews.followUp(threadId, text)
  replies.value[threadId] = ''
})

const draft = (threadId: number) => act(() => reviews.draft(threadId))
const failed = (answer: string) => answer.startsWith('⚠️')

/** Answers being turned into comments at their thread's place: ask id → the text, edited before it's added. */
const asComment = ref<Record<number, string>>({})
const addFromAnswer = (root: ReviewAsk, askId: number) => act(async () => {
  const text = asComment.value[askId]?.trim()
  if (!text) return
  await reviews.comment({ file: root.file, line: root.line, endLine: root.endLine, text })
  delete asComment.value[askId]
})

/** What Post (or Copy) sends: the kept and edited findings, then the comments, each comment editable until then. */
const toPost = computed(() => s.value?.findings.filter((f) => f.decision !== 'dropped') ?? [])
const editing = ref<{ id: number; text: string } | null>(null)
const saveEdit = () => act(async () => {
  if (!editing.value?.text.trim()) return
  await reviews.updateComment(editing.value.id, editing.value.text.trim())
  editing.value = null
})

const where = (a: { file?: string | null; line?: number | null; endLine?: number | null }) =>
  a.file ? `${a.file}${a.line ? `:${a.line}${a.endLine && a.endLine !== a.line ? `-${a.endLine}` : ''}` : ''}` : 'the whole change'
</script>

<template>
  <section class="grid gap-4">
    <p
      v-if="error"
      class="alert alert-error text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <template v-if="s">
      <header class="grid gap-1">
        <nav
          class="breadcrumbs text-sm"
          aria-label="Breadcrumb"
        >
          <ul>
            <li>
              <RouterLink :to="{ name: 'reviews' }">
                Reviews
              </RouterLink>
            </li>
            <li aria-current="page">
              {{ s.repo }} · {{ s.target === 'pr' ? `PR !${s.pullRequestId}` : s.headRef }} → {{ s.baseRef }}
            </li>
          </ul>
        </nav>
        <h1 class="sr-only">
          Review of {{ s.headRef }}
        </h1>
        <p
          class="flex flex-wrap gap-x-3 text-sm text-muted tabular-nums"
          data-testid="review-summary"
        >
          <span>{{ reviews.files.length || reviews.diff?.files.length || 0 }} files</span>
          <span>🔴 {{ counts.breaks }} 🟠 {{ counts.performance }}</span>
          <span v-if="s.model">{{ s.model }}{{ s.effort ? ` · ${s.effort}` : '' }}</span>
          <span class="font-mono">{{ short(s.baseCommit) }}…{{ short(s.headCommit) }}</span>
          <span class="font-medium text-base-content">{{ s.status }}</span>
        </p>
        <p
          v-if="s.status === 'Reviewing'"
          class="alert alert-info text-sm"
          role="status"
        >
          <span class="loading loading-spinner loading-xs" /> Reviewing… the findings appear here when the reviewer is done. You can read the change and comment meanwhile.
        </p>
        <p
          v-else-if="s.status === 'Failed'"
          class="alert alert-warning text-sm"
          role="status"
        >
          ⚠️ {{ s.error }}
        </p>
        <p
          v-if="s.summary"
          class="text-sm"
        >
          {{ s.summary }}
        </p>
        <div
          v-if="!local && s.status === 'Ready'"
          class="flex flex-wrap gap-2"
          data-testid="send"
        >
          <AgButton
            v-if="s.target === 'pr'"
            :loading="sending"
            @click="sendTo('pr')"
          >
            Post to the PR
          </AgButton>
          <AgButton
            v-if="s.target === 'pr' || s.target === 'branch'"
            variant="outline"
            :loading="sending"
            @click="sendTo('fix')"
          >
            Fix it (push to {{ s.headRef }})
          </AgButton>
          <AgButton
            variant="outline"
            :loading="sending"
            @click="sendTo('text')"
          >
            Copy as text
          </AgButton>
          <span class="self-center text-xs text-muted">Sends what's under To post: {{ toPost.length }} finding(s) and {{ reviews.current?.comments.length ?? 0 }} comment(s).</span>
        </div>
        <p
          v-if="sent?.destination === 'pr' || (!sent && s.sentTo?.startsWith('pr:'))"
          class="alert alert-success text-sm"
          role="status"
        >
          ✅ Posted to the PR<template v-if="sent">
            ({{ sent.posted }} thread(s), {{ sent.asPerson ? 'under your name' : "as agentd: connect your Azure DevOps in Settings to post under your name" }})
          </template>.
          <a
            v-if="sent?.url"
            :href="sent.url"
            target="_blank"
            rel="noopener noreferrer"
            class="link"
          >Open it</a>
        </p>
        <p
          v-if="fix"
          :class="fix.state === 'failed' ? 'alert alert-warning text-sm' : fix.state === 'running' ? 'alert alert-info text-sm' : 'alert alert-success text-sm'"
          role="status"
          data-testid="fix"
        >
          <template v-if="fix.state === 'running'">
            <span class="loading loading-spinner loading-xs" /> 🔧 Fixing on <code>{{ s.headRef }}</code>… agentd pushes the fix and reviews it again.
          </template>
          <template v-else-if="fix.state === 'failed'">
            ⚠️ {{ s.error }}
          </template>
          <template v-else-if="fix.state === 'nochange'">
            Nothing changed: {{ s.error }}
          </template>
          <template v-else>
            ✅ Pushed {{ fix.commit }} to <code>{{ s.headRef }}</code>.
            <RouterLink
              v-if="fix.next"
              :to="{ name: 'review', params: { id: fix.next } }"
              class="link"
            >
              Round 2: review #{{ fix.next }}
            </RouterLink>
          </template>
        </p>
        <div
          v-if="sent?.destination === 'text' && sent.text"
          class="grid gap-2"
        >
          <textarea
            class="textarea w-full font-mono text-xs"
            rows="10"
            readonly
            aria-label="The review as text"
            :value="sent.text"
          />
          <div>
            <AgButton
              size="sm"
              @click="copy(sent.text)"
            >
              {{ copied ? 'Copied' : 'Copy' }}
            </AgButton>
          </div>
        </div>
      </header>

      <div class="grid gap-4 lg:grid-cols-[14rem_minmax(0,1fr)_20rem]">
        <nav
          aria-label="Files"
          class="hidden lg:block"
        >
          <ul class="sticky top-4 grid gap-1 text-xs">
            <li
              v-for="f in reviews.files"
              :key="f.newPath"
            >
              <a
                :href="`#file-${f.newPath}`"
                class="flex gap-1 font-mono hover:underline"
              >
                <span>{{ forFile(f.newPath).findings.some((x) => x.severity === 'breaks') ? '🔴' : forFile(f.newPath).findings.length ? '🟠' : '·' }}</span>
                <span class="truncate">{{ f.newPath }}</span>
              </a>
            </li>
          </ul>
        </nav>

        <div class="grid min-w-0 content-start gap-4">
          <p
            v-if="reviews.diff?.truncated"
            class="alert text-sm"
          >
            The change is too big to show ({{ reviews.diff.files.length }} files); the findings and comments still work.
          </p>
          <DiffFileReview
            v-for="f in reviews.files"
            :key="f.newPath"
            :file="f"
            :findings="forFile(f.newPath).findings"
            :comments="forFile(f.newPath).comments"
            :readonly="readonly"
            @decide="decide"
            @comment="(line, text) => commentAt(f.newPath, line, text)"
            @ask="(line, text) => askAt(f.newPath, line, text)"
            @remove-comment="(c) => act(() => reviews.removeComment(c))"
          />
        </div>

        <aside class="grid content-start gap-4">
          <section
            aria-labelledby="findings-title"
            class="grid gap-2"
          >
            <h2
              id="findings-title"
              class="font-semibold"
            >
              Findings · {{ s.findings.length }}
            </h2>
            <FindingCard
              v-for="f in elsewhere"
              :key="f.number"
              :finding="f"
              :readonly="readonly"
              @decide="(d, t) => decide(f.number, d, t)"
            />
            <ul class="grid gap-1 text-sm">
              <li
                v-for="f in s.findings.filter((x) => !elsewhere.includes(x))"
                :key="f.number"
              >
                <a
                  :href="`#file-${f.file}`"
                  class="hover:underline"
                  :class="f.decision === 'dropped' ? 'line-through opacity-60' : ''"
                >{{ f.severity === 'breaks' ? '🔴' : '🟠' }} {{ f.number }} {{ f.title }}</a>
              </li>
            </ul>
          </section>
          <section
            aria-labelledby="comments-title"
            class="grid gap-2 text-sm"
            data-testid="to-post"
          >
            <h2
              id="comments-title"
              class="font-semibold"
            >
              To post · {{ toPost.length + (reviews.current?.comments.length ?? 0) }}
            </h2>
            <p
              v-for="f in toPost"
              :key="`f${f.number}`"
            >
              {{ f.severity === 'breaks' ? '🔴' : '🟠' }} <span class="font-mono text-xs">{{ where(f) }}</span> {{ f.decision === 'edited' ? f.edited : f.title }}
            </p>
            <div
              v-for="c in reviews.current?.comments ?? []"
              :key="c.id"
              class="grid gap-1"
              data-testid="draft-comment"
            >
              <template v-if="editing?.id === c.id">
                <textarea
                  v-model="editing.text"
                  class="textarea w-full text-sm"
                  rows="3"
                  :aria-label="`Edit the comment on ${where(c)}`"
                />
                <div class="flex gap-2">
                  <AgButton
                    size="sm"
                    @click="saveEdit()"
                  >
                    Save
                  </AgButton>
                  <AgButton
                    size="sm"
                    variant="ghost"
                    @click="editing = null"
                  >
                    Cancel
                  </AgButton>
                </div>
              </template>
              <p v-else>
                💬 <span class="font-mono text-xs">{{ where(c) }}</span> {{ c.text }}
                <span
                  v-if="!readonly"
                  class="ml-1 inline-flex gap-1"
                >
                  <button
                    type="button"
                    class="link text-xs"
                    @click="editing = { id: c.id, text: c.text }"
                  >Edit</button>
                  <button
                    type="button"
                    class="link text-xs"
                    @click="act(() => reviews.removeComment(c.id))"
                  >Remove</button>
                </span>
              </p>
            </div>
            <template v-if="!readonly">
              <textarea
                v-model="overall"
                class="textarea w-full text-sm"
                rows="2"
                placeholder="A comment or a question on the whole change"
                aria-label="A comment or a question on the whole change"
              />
              <div class="flex gap-2">
                <AgButton
                  size="sm"
                  variant="outline"
                  @click="overallSend('comment')"
                >
                  Add comment
                </AgButton>
                <AgButton
                  size="sm"
                  variant="outline"
                  @click="overallSend('ask')"
                >
                  Ask
                </AgButton>
              </div>
            </template>
          </section>
          <section
            v-if="reviews.current?.asks.length"
            aria-labelledby="asks-title"
            class="grid gap-3 text-sm"
          >
            <h2
              id="asks-title"
              class="font-semibold"
            >
              Questions · {{ threads.length }}
            </h2>
            <article
              v-for="t in threads"
              :key="t.root.id"
              class="grid gap-2 rounded-box border border-base-300 p-2"
              data-testid="ask"
            >
              <p class="font-mono text-xs text-muted">
                ❓ {{ where(t.root) }}
              </p>
              <div
                v-for="a in t.asks"
                :key="a.id"
                class="grid gap-1"
              >
                <p><span class="text-xs text-muted">{{ a.author }}:</span> {{ a.question }}</p>
                <template v-if="a.answer">
                  <MarkdownText :text="a.answer" />
                  <div
                    v-if="!readonly && asComment[a.id] != null"
                    class="grid gap-1"
                  >
                    <textarea
                      v-model="asComment[a.id]"
                      class="textarea w-full text-sm"
                      rows="4"
                      :aria-label="`The comment on ${where(t.root)}`"
                    />
                    <div class="flex gap-2">
                      <AgButton
                        size="sm"
                        @click="addFromAnswer(t.root, a.id)"
                      >
                        Add comment
                      </AgButton>
                      <AgButton
                        size="sm"
                        variant="ghost"
                        @click="delete asComment[a.id]"
                      >
                        Cancel
                      </AgButton>
                    </div>
                  </div>
                  <div v-else-if="!readonly && !failed(a.answer)">
                    <button
                      type="button"
                      class="link text-xs"
                      @click="asComment[a.id] = a.answer"
                    >
                      Use as comment
                    </button>
                  </div>
                </template>
                <p
                  v-else
                  class="text-muted"
                >
                  <span class="loading loading-dots loading-xs" /> thinking…
                </p>
              </div>
              <form
                v-if="!readonly"
                class="flex gap-2"
                @submit.prevent="followUp(t.root.id)"
              >
                <input
                  v-model="replies[t.root.id]"
                  class="input input-sm min-w-0 flex-1"
                  placeholder="Ask a follow-up"
                  aria-label="Ask a follow-up"
                  :disabled="t.asks.some((a) => a.answer == null)"
                >
                <AgButton
                  size="sm"
                  variant="outline"
                  type="submit"
                  :disabled="t.asks.some((a) => a.answer == null)"
                >
                  Ask
                </AgButton>
                <AgButton
                  size="sm"
                  variant="outline"
                  :disabled="t.asks.some((a) => a.answer == null)"
                  @click="draft(t.root.id)"
                >
                  Draft a comment
                </AgButton>
              </form>
            </article>
          </section>
        </aside>
      </div>
    </template>
  </section>
</template>
