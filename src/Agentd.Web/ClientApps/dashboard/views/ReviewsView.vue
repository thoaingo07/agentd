<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '../../shared/api/http'
import { AgButton } from '../../shared/components/ui'
import { dateTime } from '../../shared/utils/format'
import { useConfigStore } from '../stores/config'
import { useReviewsStore } from '../stores/reviews'

// Reviews: start one on any PR, pushed branch or two commits of a registered repository, and reopen mine.
const reviews = useReviewsStore()
const config = useConfigStore()
const router = useRouter()
const form = reactive({ repo: '', kind: 'branch' as 'pr' | 'branch' | 'range', pr: '', branch: '', base: '', head: '' })
const busy = ref(false)
const error = ref<string | null>(null)
const repos = computed(() => config.config?.repositories.map((r) => r.name) ?? [])
const icons: Record<string, string> = { Reviewing: '⏳', Ready: '📝', Sent: '✅', Closed: '⚪', Failed: '⚠️' }

onMounted(async () => {
  await Promise.all([reviews.loadMine().catch(() => {}), config.load().catch(() => {})])
  form.repo ||= repos.value[0] ?? ''
})

async function start(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    const s = await reviews.start({
      repo: form.repo,
      pullRequestId: form.kind === 'pr' ? Number(form.pr.replace(/\D/g, '')) || null : null,
      branch: form.kind === 'branch' ? form.branch.trim() : null,
      base: form.kind === 'pr' ? null : form.base.trim() || null,
      head: form.kind === 'range' ? form.head.trim() : null,
    })
    await router.push({ name: 'review', params: { id: s.id } })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-6">
    <h1 class="text-xl font-semibold">
      Reviews
    </h1>
    <form
      class="grid gap-3 rounded-box border border-base-300 p-4"
      aria-labelledby="new-review"
      @submit.prevent="start"
    >
      <h2
        id="new-review"
        class="font-semibold"
      >
        New review
      </h2>
      <div class="flex flex-wrap items-end gap-3">
        <label class="grid gap-1">
          <span class="text-sm">Repository</span>
          <select
            v-model="form.repo"
            class="select select-sm"
          >
            <option
              v-for="r in repos"
              :key="r"
              :value="r"
            >{{ r }}</option>
          </select>
        </label>
        <div
          class="join"
          role="radiogroup"
          aria-label="What to review"
        >
          <label
            v-for="k in [{ v: 'pr', t: 'Pull request' }, { v: 'branch', t: 'Branch' }, { v: 'range', t: 'Commits' }]"
            :key="k.v"
            class="btn btn-sm join-item"
            :class="form.kind === k.v ? 'btn-primary' : ''"
          >
            <input
              v-model="form.kind"
              type="radio"
              class="sr-only"
              :value="k.v"
            >{{ k.t }}
          </label>
        </div>
      </div>
      <label
        v-if="form.kind === 'pr'"
        class="grid gap-1"
      >
        <span class="text-sm">PR number</span>
        <input
          v-model="form.pr"
          class="input input-sm w-40 font-mono"
          inputmode="numeric"
          placeholder="3944"
        >
      </label>
      <div
        v-else
        class="grid gap-3 sm:grid-cols-2"
      >
        <label
          v-if="form.kind === 'branch'"
          class="grid gap-1"
        >
          <span class="text-sm">Branch (pushed)</span>
          <input
            v-model="form.branch"
            class="input input-sm w-full font-mono"
            placeholder="feature/keyset-chunks"
            autocomplete="off"
          >
        </label>
        <label
          v-else
          class="grid gap-1"
        >
          <span class="text-sm">Head commit</span>
          <input
            v-model="form.head"
            class="input input-sm w-full font-mono"
            autocomplete="off"
          >
        </label>
        <label class="grid gap-1">
          <span class="text-sm">{{ form.kind === 'branch' ? 'Compare with (optional)' : 'Base commit' }}</span>
          <input
            v-model="form.base"
            class="input input-sm w-full font-mono"
            :placeholder="form.kind === 'branch' ? 'its base branch' : ''"
            autocomplete="off"
          >
        </label>
      </div>
      <div>
        <AgButton
          type="submit"
          :loading="busy"
          :disabled="!form.repo"
        >
          Review it
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

    <div class="grid gap-2">
      <h2 class="font-semibold">
        My reviews
      </h2>
      <p
        v-if="!reviews.mine.length"
        class="text-sm text-muted"
      >
        None yet.
      </p>
      <ul class="grid gap-2">
        <li
          v-for="r in reviews.mine"
          :key="r.id"
        >
          <RouterLink
            :to="{ name: 'review', params: { id: r.id } }"
            class="flex flex-wrap items-center gap-2 rounded-box border border-base-300 p-3 text-sm hover:bg-base-200"
          >
            <span>{{ icons[r.status] ?? '' }}</span>
            <span class="font-medium">{{ r.repo }}</span>
            <span class="font-mono text-xs">{{ r.target === 'pr' ? `PR !${r.pullRequestId}` : r.headRef }}</span>
            <span class="text-muted">→ {{ r.baseRef }}</span>
            <span class="ml-auto text-xs text-muted">{{ r.findings.length }} finding(s) · {{ dateTime(r.createdAt) }}</span>
          </RouterLink>
        </li>
      </ul>
    </div>
  </section>
</template>
