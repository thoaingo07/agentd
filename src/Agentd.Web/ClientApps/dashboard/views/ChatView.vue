<script setup lang="ts">
// One code question: the whole conversation, follow-ups like in its chat thread, and Close.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '../../shared/api/http'
import { AgButton } from '../../shared/components/ui'
import MarkdownText from '../../shared/components/MarkdownText.vue'
import { useChatsStore } from '../stores/chats'

const props = defineProps<{ id: number }>()
const chats = useChatsStore()
const chat = computed(() => (chats.current?.id === props.id ? chats.current : null))
const text = ref('')
const busy = ref(false)
const error = ref<string | null>(null)
const clock = (at: string) => new Date(at).toLocaleString()

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

const send = () => act(async () => {
  if (!text.value.trim()) return
  await chats.message(text.value.trim())
  text.value = ''
})

watch(() => props.id, (id) => void chats.open(id).catch(() => {}), { immediate: true })
onBeforeUnmount(() => chats.close())
</script>

<template>
  <section class="mx-auto grid max-w-4xl gap-4">
    <RouterLink
      to="/chats"
      class="text-sm text-muted"
    >
      ← Chat
    </RouterLink>
    <p
      v-if="chats.missing"
      class="text-muted"
    >
      There is no chat #{{ id }}.
    </p>
    <p
      v-else-if="!chat"
      class="text-muted"
    >
      Loading…
    </p>
    <template v-else>
      <header class="flex flex-wrap items-center gap-2">
        <h1 class="text-xl font-semibold">
          💬 Chat #{{ chat.id }}
        </h1>
        <span
          class="badge badge-sm"
          :class="chat.status === 'Open' ? 'badge-success' : 'badge-ghost'"
        >{{ chat.status }}</span>
        <span class="text-sm text-muted">{{ chat.repos.join(', ') }} · by {{ chat.author }} · model {{ chat.model ?? 'default' }}, effort {{ chat.effort ?? 'default' }}</span>
        <AgButton
          v-if="chat.status === 'Open'"
          class="ml-auto"
          size="sm"
          variant="ghost"
          :disabled="busy"
          @click="act(chats.closeChat)"
        >
          Close chat
        </AgButton>
      </header>
      <div
        v-for="(m, i) in chat.messages"
        :key="i"
        class="chat"
        :class="m.direction === 'out' ? 'chat-start' : 'chat-end'"
        :data-direction="m.direction"
      >
        <div class="chat-header text-xs text-muted">
          {{ m.author }} · {{ clock(m.at) }}
        </div>
        <div
          v-if="m.direction === 'out'"
          class="chat-bubble bg-base-200 text-base-content"
        >
          <MarkdownText :text="m.text" />
        </div>
        <div
          v-else
          class="chat-bubble whitespace-pre-wrap bg-primary/15 text-sm text-base-content"
        >
          {{ m.text }}
        </div>
      </div>
      <div
        v-if="chat.thinking"
        class="chat chat-start"
        role="status"
      >
        <div class="chat-bubble bg-base-200 text-sm text-muted">
          <span class="loading loading-dots loading-xs" /> thinking…
        </div>
      </div>
      <form
        v-if="chat.status === 'Open'"
        class="grid gap-2"
        @submit.prevent="send"
      >
        <textarea
          v-model="text"
          class="textarea w-full"
          rows="2"
          maxlength="8000"
          placeholder="Follow up (Ctrl+Enter to send)"
          aria-label="Follow up"
          @keydown.ctrl.enter.prevent="send"
          @keydown.meta.enter.prevent="send"
        />
        <div>
          <AgButton
            type="submit"
            size="sm"
            :loading="busy"
            :disabled="!text.trim()"
          >
            Send
          </AgButton>
        </div>
      </form>
      <p
        v-else
        class="text-sm text-muted"
      >
        This chat is closed. Ask a new question from Chat.
      </p>
      <p
        v-if="error"
        class="text-sm text-error"
        role="alert"
      >
        {{ error }}
      </p>
    </template>
  </section>
</template>
