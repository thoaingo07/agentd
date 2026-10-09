<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import { AgButton } from '../../shared/components/ui'
import DiffFileReview from '../../shared/review/DiffFileReview.vue'
import FindingCard from '../../shared/review/FindingCard.vue'
import { useReviewsStore } from '../stores/reviews'

// The review page (docs/architect/review-sessions.md §2): files, the diff with findings and comments at their lines,
// and the findings, your comments and a comment on the whole change at the side.
const props = defineProps<{ id: number }>()
const reviews = useReviewsStore()
const error = ref<string | null>(null)
const overall = ref('')
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
const commentAll = () => act(async () => {
  if (!overall.value.trim()) return
  await reviews.comment({ text: overall.value.trim() })
  overall.value = ''
})
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
                placeholder="A comment on the whole change"
                aria-label="A comment on the whole change"
              />
              <div>
                <AgButton
                  size="sm"
                  variant="outline"
                  @click="commentAll"
                >
                  Add comment
                </AgButton>
              </div>
            </template>
          </section>
        </aside>
      </div>
    </template>
  </section>
</template>
