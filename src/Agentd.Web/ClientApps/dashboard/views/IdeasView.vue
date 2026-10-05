<script setup lang="ts">
import { onMounted } from 'vue'
import IdeaStatusBadge from '../components/IdeaStatusBadge.vue'
import { useIdeasStore } from '../stores/ideas'

const ideas = useIdeasStore()
onMounted(() => void ideas.load().catch(() => {}))
</script>

<template>
  <section class="mx-auto grid max-w-5xl gap-4">
    <div class="grid gap-1">
      <h1 class="text-xl font-semibold">
        Ideas
      </h1>
      <p class="text-sm text-muted">
        Brainstormed with the agent in chat (<code class="font-mono">!idea &lt;text&gt;</code>), grounded in the code, until they become work items.
      </p>
    </div>
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
      No ideas yet. Start one in the chat channel with <code class="font-mono">!idea</code>.
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
