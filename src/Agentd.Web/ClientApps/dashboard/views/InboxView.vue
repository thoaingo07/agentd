<script setup lang="ts">
// The Inbox (docs/architect/inbox.md, like Plannotator's): agentd's messages to people, one thread per job, idea,
// review, chat, incident or watched PR. Threads waiting on you come first; open one to read it and answer with its buttons or a
// reply, which goes to its source as its page or chat thread would take it. j/k move, e marks read.
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter, type RouteLocationRaw } from 'vue-router'
import { ApiError } from '../../shared/api/http'
import type { InboxThread } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import PaneResizer from '../../shared/components/PaneResizer.vue'
import { ago } from '../../shared/utils/format'
import { usePaneWidth } from '../../shared/utils/paneWidth'
import { useInboxStore, type InboxView } from '../stores/inbox'
import { notificationsOn, setNotifications } from '../stores/inboxNotifications'

const props = defineProps<{ source?: string; id?: number }>()
const inbox = useInboxStore()
const router = useRouter()
const listWidth = usePaneWidth('inbox-list', 352, 260, 640)
const error = ref<string | null>(null)
const busy = ref(false)
const text = ref('')
const notice = ref<string | null>(null)
/** Browser notifications when a thread starts waiting on you: off until you turn them on here (the browser asks once). */
const supported = typeof Notification !== 'undefined'
const notifying = ref(notificationsOn())
const blocked = ref(supported && Notification.permission === 'denied')
async function toggleNotifications(): Promise<void> {
  notifying.value = await setNotifications(!notifying.value)
  blocked.value = supported && Notification.permission === 'denied'
}

const icon: Record<string, string> = { job: '🛠', idea: '💡', review: '🔍', chat: '💬', watch: '👀', incident: '🚨' }
/** Ideas, reviews, chats and (old) watched PRs name their thread with their own emoji already; a job's gets the source's. */
const titled = (t: { source: string; title: string }) => (/^\p{Extended_Pictographic}/u.test(t.title) ? t.title : `${icon[t.source] ?? '•'} ${t.title}`)
const views: { value: InboxView; label: string }[] = [{ value: 'all', label: 'Everyone' }, { value: 'mine', label: 'Mine' }]

/** The source's own page: a job's work item, an idea, a chat, an incident, the Reviews page for chat reviews (and old watched PRs). */
function page(t: { source: string; sourceId: number }): RouteLocationRaw {
  switch (t.source) {
    case 'job': return { name: 'job', params: { id: t.sourceId } }
    case 'idea': return { name: 'idea', params: { id: t.sourceId } }
    case 'chat': return { name: 'chat', params: { id: t.sourceId } }
    case 'incident': return { name: 'incident', params: { id: t.sourceId } }
    default: return { name: 'reviews' }
  }
}

/** The list's one-line preview: the message's first line without Markdown marks. */
const preview = (t: InboxThread) => t.latestPreview.split('\n').find((l) => l.trim())?.replace(/[*_`#>]/g, '').trim() ?? ''

/** A waiting thread's buttons: its question's. */
const choices = computed(() => {
  const t = inbox.current
  if (!t?.waiting) return []
  return t.question?.options.map((o) => o.label) ?? []
})

async function act(work: () => Promise<void>): Promise<void> {
  busy.value = true
  error.value = null
  try {
    await work()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = false
  }
}

const select = (t: InboxThread) => router.push({ name: 'inbox-thread', params: { source: t.source, id: t.sourceId } })

const answer = (reply: string) => act(async () => {
  if (!inbox.open || !reply.trim()) return
  const outcome = await inbox.reply(inbox.open, reply.trim())
  text.value = ''
  notice.value = outcome === 'sent' || outcome === 'routed' ? 'Sent.' : `Sent (${outcome}).`
  setTimeout(() => (notice.value = null), 3000)
})

const markRead = () => act(async () => {
  const t = inbox.current
  if (t) await inbox.markRead({ source: t.source, sourceId: t.sourceId }, t.latestId)
})

/** j / k: the next / previous thread in the list's order; e: mark the open one read (not while typing). */
function onKey(e: KeyboardEvent): void {
  const typing = e.target instanceof Element && e.target.closest('input, textarea, select, [contenteditable="true"]')
  if (e.ctrlKey || e.metaKey || e.altKey || typing) return
  const order = inbox.sections.flatMap((s) => s.threads)
  const at = order.findIndex((t) => t === inbox.current)
  if (e.key === 'j' || e.key === 'k') {
    const next = order[e.key === 'j' ? Math.min(at + 1, order.length - 1) : Math.max(at - 1, 0)]
    if (next) void select(next)
  } else if (e.key === 'e') {
    void markRead()
  }
}

let stop: (() => void) | undefined
onMounted(() => {
  stop = inbox.watchList()
  window.addEventListener('keydown', onKey)
})
onBeforeUnmount(() => {
  stop?.()
  window.removeEventListener('keydown', onKey)
  inbox.close()
})
watch(() => inbox.view, () => void act(inbox.load))
watch(() => [props.source, props.id] as const, ([source, id]) => {
  void act(async () => {
    if (!inbox.threads) await inbox.load()
    if (source && id) await inbox.openThread({ source, sourceId: id })
    else inbox.close()
  })
}, { immediate: true })
</script>

<template>
  <section class="grid gap-4">
    <header class="flex flex-wrap items-center gap-3">
      <h1 class="text-xl font-semibold">
        Inbox
      </h1>
      <span class="ag-meta">{{ inbox.counts.waiting }} waiting on you · {{ inbox.counts.unread }} unread</span>
      <button
        v-if="supported"
        type="button"
        class="btn btn-ghost btn-sm ml-auto"
        :disabled="blocked"
        :title="blocked ? 'Notifications are blocked for this site in the browser settings' : 'A browser notification when a thread starts waiting on you'"
        data-testid="notify-toggle"
        @click="toggleNotifications"
      >
        {{ blocked ? '🔕 Notifications blocked' : notifying ? '🔔 Notifications on' : '🔕 Enable notifications' }}
      </button>
      <div
        class="join"
        :class="supported ? '' : 'ml-auto'"
        role="radiogroup"
        aria-label="Whose threads"
      >
        <button
          v-for="v in views"
          :key="v.value"
          type="button"
          role="radio"
          :aria-checked="inbox.view === v.value"
          class="btn btn-sm join-item"
          :class="inbox.view === v.value ? 'btn-primary' : ''"
          @click="inbox.view = v.value"
        >
          {{ v.label }}
        </button>
      </div>
    </header>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>

    <div
      class="grid gap-4 lg:grid-cols-[var(--inbox-list)_minmax(0,1fr)]"
      :style="{ '--inbox-list': `${listWidth}px` }"
    >
      <nav
        class="ag-pane relative self-start lg:sticky lg:top-4 lg:max-h-[calc(100vh-6rem)] lg:overflow-y-auto"
        aria-label="Threads"
      >
        <PaneResizer
          v-model="listWidth"
          :min="260"
          :max="640"
          :initial="352"
          label="Resize the thread list"
        />
        <p
          v-if="inbox.threads && !inbox.sections.length"
          class="p-4 text-sm text-muted"
        >
          Nothing yet. Questions, plans, results and failures from agentd land here, with or without chat.
        </p>
        <div
          v-for="section in inbox.sections"
          :key="section.id"
          role="group"
          :aria-label="section.label"
          :data-section="section.id"
          class="pb-2"
        >
          <h2 class="ag-section-title">
            {{ section.label }} <span class="ag-count">{{ section.threads.length }}</span>
          </h2>
          <button
            v-for="t in section.threads"
            :key="`${t.source}:${t.sourceId}`"
            type="button"
            class="ag-list-row"
            :class="{ 'is-active': inbox.current === t }"
            data-testid="inbox-thread"
            @click="select(t)"
          >
            <span class="grid min-w-0 flex-1 gap-0.5">
              <span class="flex min-w-0 items-center gap-2">
                <span
                  class="truncate text-sm"
                  :class="t.unread ? 'font-semibold' : ''"
                >{{ titled(t) }}</span>
                <span class="ag-meta ml-auto shrink-0">{{ ago(t.latestAt) }}</span>
              </span>
              <span class="flex min-w-0 items-center gap-2">
                <span class="ag-meta truncate">{{ t.waiting && t.question ? '❓ ' : '' }}{{ preview(t) }}</span>
                <span
                  v-if="t.unread"
                  class="ag-count ml-auto"
                  :class="{ 'is-urgent': t.waiting }"
                >{{ t.unread }}</span>
              </span>
            </span>
          </button>
        </div>
      </nav>

      <article
        v-if="inbox.current"
        class="ag-pane grid min-w-0 content-start gap-3 p-4"
        data-testid="inbox-open"
      >
        <header class="flex flex-wrap items-center gap-2">
          <h2 class="flex-1 text-lg font-semibold">
            {{ titled(inbox.current) }}
          </h2>
          <RouterLink
            :to="page(inbox.current)"
            class="link text-sm"
          >
            Open its page
          </RouterLink>
          <AgButton
            size="sm"
            variant="ghost"
            title="Mark read (e): out of the top until agentd writes again"
            @click="markRead"
          >
            Mark read
          </AgButton>
        </header>
        <ol class="grid gap-2">
          <li
            v-for="m in inbox.items"
            :key="m.id"
            class="ag-message"
            :class="{ 'is-question': m.kind === 'Question' }"
            data-testid="inbox-message"
          >
            <p class="ag-meta">
              {{ m.kind === 'Question' ? '❓ Question' : m.kind === 'Error' ? '⚠️ Problem' : m.kind === 'Result' ? '✅ Result' : 'agentd' }} · {{ ago(m.createdAt) }}
            </p>
            <MarkdownText :text="m.markdown" />
          </li>
        </ol>
        <div
          v-if="choices.length"
          class="flex flex-wrap gap-2"
          role="group"
          aria-label="Answer"
        >
          <AgButton
            v-for="c in choices"
            :key="c"
            size="sm"
            variant="outline"
            :loading="busy"
            @click="answer(c)"
          >
            {{ c }}
          </AgButton>
        </div>
        <form
          class="grid gap-2"
          @submit.prevent="answer(text)"
        >
          <textarea
            v-model="text"
            class="textarea w-full"
            rows="3"
            :placeholder="inbox.current.waiting ? 'Answer, or ask something back' : 'Write to it, as in its chat thread or page'"
            aria-label="Reply"
          />
          <div class="flex items-center gap-2">
            <AgButton
              type="submit"
              size="sm"
              :loading="busy"
              :disabled="!text.trim()"
            >
              Send
            </AgButton>
            <span
              v-if="notice"
              class="ag-meta"
              role="status"
            >{{ notice }}</span>
          </div>
        </form>
      </article>
      <p
        v-else
        class="ag-pane p-6 text-sm text-muted"
      >
        Pick a thread. <kbd>j</kbd> / <kbd>k</kbd> move through them, <kbd>e</kbd> marks one read.
      </p>
    </div>
  </section>
</template>
