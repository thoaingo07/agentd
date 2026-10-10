<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '../../shared/api/http'
import { AgButton } from '../../shared/components/ui'
import IdeaStatusBadge from '../components/IdeaStatusBadge.vue'
import { useConfigStore } from '../stores/config'
import { ideaEfforts, ideaModels, useIdeasStore } from '../stores/ideas'

const ideas = useIdeasStore()
const config = useConfigStore()
const router = useRouter()
const repos = computed(() => config.config?.repositories.map((r) => r.name) ?? [])
const draft = ref({ text: '', repo: '', model: '', effort: '' })
const starting = ref(false)
const error = ref<string | null>(null)
onMounted(() => {
  void ideas.load().catch(() => {})
  void config.load().then((c) => (draft.value.repo ||= c.repositories[0]?.name ?? '')).catch(() => {})
})

/** Starts the brainstorm here (no chat thread) and opens its page, where the agent's first reply arrives. */
async function start(): Promise<void> {
  if (!draft.value.text.trim() || starting.value) return
  starting.value = true
  error.value = null
  try {
    const { text, repo, model, effort } = draft.value
    const id = await ideas.start({ text: text.trim(), repo: repo || null, model: model || null, effort: effort || null })
    await router.push({ name: 'idea', params: { id } })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'The idea wasn’t started.'
  } finally {
    starting.value = false
  }
}
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <div class="grid gap-1">
      <h1 class="text-xl font-semibold">
        Ideas
      </h1>
      <p class="text-sm text-muted">
        Brainstormed with the agent, here or in chat (<code class="font-mono">!idea &lt;text&gt;</code>), grounded in the code, until they become work items.
      </p>
    </div>
    <form
      class="grid gap-2 rounded-box border border-base-300 p-3"
      aria-label="New idea"
      @submit.prevent="start"
    >
      <textarea
        v-model="draft.text"
        class="textarea w-full"
        rows="3"
        maxlength="8000"
        placeholder="Describe the idea: what and why. The agent reads the code and talks it through with you."
        aria-label="The idea"
      />
      <div class="flex flex-wrap items-center gap-2">
        <select
          v-if="repos.length > 1"
          v-model="draft.repo"
          class="select select-sm w-auto"
          aria-label="Repository"
        >
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
          :loading="starting"
          :disabled="!draft.text.trim()"
        >
          Start brainstorm
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
      v-if="ideas.list === null"
      class="text-muted"
    >
      Loading…
    </p>
    <p
      v-else-if="ideas.list.length === 0"
      class="text-muted"
    >
      No ideas yet. Start one above, or in the chat channel with <code class="font-mono">!idea</code>.
    </p>
    <div
      v-else
      class="overflow-x-auto rounded-box border border-base-300"
    >
      <table class="table table-sm">
        <thead>
          <tr>
            <th>Idea</th><th>Status</th><th class="hidden md:table-cell">
              Repo
            </th><th class="hidden sm:table-cell">
              By
            </th><th>Work items</th><th class="hidden lg:table-cell text-right">
              Updated
            </th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="idea in ideas.list"
            :key="idea.id"
            class="hover:bg-base-200"
            data-testid="idea-row"
          >
            <td class="max-w-80">
              <RouterLink
                :to="{ name: 'idea', params: { id: idea.id } }"
                class="link link-hover"
              >
                <span class="font-mono text-[13px] text-muted">#{{ idea.id }}</span> {{ idea.title }}
              </RouterLink>
              <span class="block text-xs text-muted">{{ idea.messages }} messages<span v-if="idea.drafts"> · {{ idea.drafts }} drafts</span></span>
            </td>
            <td><IdeaStatusBadge :status="idea.status" /></td>
            <td class="hidden md:table-cell">
              {{ idea.repo }}
            </td>
            <td class="hidden sm:table-cell">
              {{ idea.author }}
            </td>
            <td class="whitespace-nowrap">
              <RouterLink
                v-for="wi in idea.createdWorkItems"
                :key="wi"
                :to="{ name: 'work-item', params: { id: wi } }"
                class="link mr-2 font-mono text-[13px]"
              >
                WI-{{ wi }}
              </RouterLink>
              <span v-if="idea.createdWorkItems.length === 0">—</span>
            </td>
            <td class="hidden whitespace-nowrap text-right text-muted lg:table-cell">
              {{ new Date(idea.updatedAt).toLocaleString() }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</template>
