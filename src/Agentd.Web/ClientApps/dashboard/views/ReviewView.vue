<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { ReviewSent } from '../../shared/api/types'
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
async function sendTo(destination: 'pr' | 'text'): Promise<void> {
  sending.value = true
  await act(async () => (sent.value = await reviews.sendTo(destination)))
  sending.value = false
}

async function copy(text: string): Promise<void> {
  await navigator.clipboard?.writeText(text).catch(() => {})
  copied.value = true
}

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
            variant="outline"
            :loading="sending"
            @click="sendTo('text')"
          >
            Copy as text
          </AgButton>
          <span class="self-center text-xs text-muted">Sends the kept and edited findings and your comments.</span>
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
          >
            <h2
              id="comments-title"
              class="font-semibold"
            >
              Your comments · {{ reviews.current?.comments.length ?? 0 }}
            </h2>
            <p
              v-for="c in reviews.current?.comments ?? []"
              :key="c.id"
            >
              💬 <span class="font-mono text-xs">{{ c.file ? `${c.file}:${c.line}` : 'the whole change' }}</span> {{ c.text }}
            </p>
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
              Questions · {{ reviews.current.asks.length }}
            </h2>
            <article
              v-for="a in reviews.current.asks"
              :key="a.id"
              class="grid gap-1 rounded-box border border-base-300 p-2"
              data-testid="ask"
            >
              <p>❓ <span class="font-mono text-xs">{{ where(a) }}</span> {{ a.question }}</p>
              <MarkdownText
                v-if="a.answer"
                :text="a.answer"
              />
              <p
                v-else
                class="text-muted"
              >
                <span class="loading loading-dots loading-xs" /> thinking…
              </p>
            </article>
          </section>
        </aside>
      </div>
    </template>
  </section>
</template>
