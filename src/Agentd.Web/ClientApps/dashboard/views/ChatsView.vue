<script setup lang="ts">
// Chat: questions about the code (like !chat): ask one here, or reopen an earlier one. The agent reads every registered
// repository (read-only) and agentd's Azure DevOps.
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '../../shared/api/http'
import { AgButton } from '../../shared/components/ui'
import { dateTime } from '../../shared/utils/format'
import { useChatsStore } from '../stores/chats'
import { useConfigStore } from '../stores/config'
import { ideaEfforts, ideaModels } from '../stores/ideas'

const chats = useChatsStore()
const config = useConfigStore()
const router = useRouter()
const repos = computed(() => config.config?.repositories.map((r) => r.name) ?? [])
const draft = ref({ text: '', repo: '', model: '', effort: '' })
const asking = ref(false)
const error = ref<string | null>(null)
onMounted(() => {
  void chats.load().catch(() => {})
  void config.load().catch(() => {})
})

async function ask(): Promise<void> {
  if (!draft.value.text.trim() || asking.value) return
  asking.value = true
  error.value = null
  try {
    const { text, repo, model, effort } = draft.value
    const id = await chats.start({ text: text.trim(), repo: repo || null, model: model || null, effort: effort || null })
    await router.push({ name: 'chat', params: { id } })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'The question wasn’t sent.'
  } finally {
    asking.value = false
  }
}
</script>

<template>
  <section class="mx-auto grid max-w-4xl gap-4">
    <div class="grid gap-1">
      <h1 class="text-xl font-semibold">
        Chat
      </h1>
      <p class="text-sm text-muted">
        Ask about the code, here or in chat (<code class="font-mono">!chat &lt;question&gt;</code>). The agent reads the repositories (read-only) and Azure DevOps, and you can follow up.
      </p>
    </div>
    <form
      class="grid gap-2 rounded-box border border-base-300 p-3"
      aria-label="New question"
      @submit.prevent="ask"
    >
      <textarea
        v-model="draft.text"
        class="textarea w-full"
        rows="3"
        maxlength="8000"
        placeholder="e.g. Where do we validate the login token? Which pipeline deploys sysmin?"
        aria-label="Your question"
        @keydown.ctrl.enter.prevent="ask"
        @keydown.meta.enter.prevent="ask"
      />
      <div class="flex flex-wrap items-center gap-2">
        <select
          v-if="repos.length > 1"
          v-model="draft.repo"
          class="select select-sm w-auto"
          aria-label="Repository"
        >
          <option value="">
            Every repository
          </option>
          <option
            v-for="r in repos"
            :key="r"
            :value="r"
          >
            {{ r }}
          </option>
        </select>
        <select
          v-model="draft.model"
          class="select select-sm w-auto"
          aria-label="Model"
        >
          <option value="">
            Default model
          </option>
          <option
            v-for="m in ideaModels"
            :key="m"
            :value="m"
          >
            {{ m }}
          </option>
        </select>
        <select
          v-model="draft.effort"
          class="select select-sm w-auto"
          aria-label="Effort"
        >
          <option value="">
            Default effort
          </option>
          <option
            v-for="e in ideaEfforts"
            :key="e"
            :value="e"
          >
            {{ e }}
          </option>
        </select>
        <AgButton
          type="submit"
          size="sm"
          :loading="asking"
          :disabled="!draft.text.trim()"
        >
          Ask
        </AgButton>
      </div>
      <p
        v-if="error"
        class="text-sm text-error"
        role="alert"
      >
        {{ error }}
      </p>
    </form>
    <p
      v-if="chats.list === null"
      class="text-muted"
    >
      Loading…
    </p>
    <p
      v-else-if="chats.list.length === 0"
      class="text-muted"
    >
      No questions yet.
    </p>
    <ul
      v-else
      class="grid gap-2"
    >
      <li
        v-for="c in chats.list"
        :key="c.id"
        class="flex flex-wrap items-center gap-2 rounded-box border border-base-300 p-3 text-sm"
        data-testid="chat-row"
      >
        <RouterLink
          :to="{ name: 'chat', params: { id: c.id } }"
          class="link link-hover min-w-0 flex-1 truncate"
        >
          <span class="font-mono text-[13px] text-muted">#{{ c.id }}</span> {{ c.title || 'Untitled' }}
        </RouterLink>
        <span
          class="badge badge-sm"
          :class="c.status === 'Open' ? 'badge-success' : 'badge-ghost'"
        >{{ c.status }}</span>
        <span class="text-xs text-muted">{{ c.author }} · {{ c.messages }} messages · {{ dateTime(c.updatedAt) }}{{ c.inChat ? ' · in chat' : '' }}</span>
      </li>
    </ul>
  </section>
</template>
