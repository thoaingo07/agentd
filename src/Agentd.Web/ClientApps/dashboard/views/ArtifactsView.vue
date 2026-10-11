<script setup lang="ts">
// Artifacts: HTML pages agents published (publish_artifact), newest first. Each opens on its own page,
// /artifacts/<name>.html, sandboxed away from agentd (docs/architect/artifacts.md).
import { onMounted, ref } from 'vue'
import { get } from '../../shared/api/http'
import type { Artifact } from '../../shared/api/types'
import { dateTime } from '../../shared/utils/format'

const artifacts = ref<Artifact[] | null>(null)
/** The page of what published it, when it has one (a PR review thread or a watched PR's fix round lives in chat). */
const pages: Record<string, string> = { job: 'job', chat: 'chat', idea: 'idea', 'review-session': 'review' }
const labels: Record<string, string> = { job: 'job', chat: 'chat', idea: 'idea', review: 'PR review', 'review-session': 'review', watch: 'watched PR', web: 'agentd' }
const error = ref(false)
onMounted(async () => {
  try {
    artifacts.value = await get<Artifact[]>('/api/artifacts')
  } catch {
    error.value = true
  }
})
</script>

<template>
  <section class="mx-auto grid max-w-4xl gap-4">
    <div class="grid gap-1">
      <h1 class="text-xl font-semibold">
        Artifacts
      </h1>
      <p class="text-sm text-muted">
        Pages agents published: reports, diagrams, explainers. Ask any agent (a job, a chat, an idea, a review) to "publish an artifact"; anyone who can open agentd can read them.
      </p>
    </div>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      Couldn't load the artifacts.
    </p>
    <p
      v-else-if="artifacts === null"
      class="text-muted"
    >
      Loading…
    </p>
    <p
      v-else-if="artifacts.length === 0"
      class="text-muted"
    >
      No artifacts yet.
    </p>
    <ul
      v-else
      class="grid gap-2"
    >
      <li
        v-for="a in artifacts"
        :key="a.name"
        class="flex flex-wrap items-center gap-2 rounded-box border border-base-300 p-3 text-sm"
        data-testid="artifact"
      >
        <a
          :href="a.url"
          target="_blank"
          rel="noopener noreferrer"
          class="link font-medium"
        >{{ a.title }}</a>
        <span class="text-xs text-muted">
          <RouterLink
            v-if="pages[a.source]"
            :to="{ name: pages[a.source]!, params: { id: a.sourceId } }"
            class="link"
          >{{ labels[a.source] }} #{{ a.sourceId }}</RouterLink><template v-else>
            {{ labels[a.source] ?? a.source }}{{ a.sourceId ? ` #${a.sourceId}` : '' }}
          </template>
          · {{ dateTime(a.updatedAt) }}
        </span>
      </li>
    </ul>
  </section>
</template>
