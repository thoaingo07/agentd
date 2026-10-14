<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '../../shared/api/http'
import type { OpenPullRequest } from '../../shared/api/types'
import UBadge from '@nuxt/ui/components/Badge.vue'
import UButton from '@nuxt/ui/components/Button.vue'
import UCard from '@nuxt/ui/components/Card.vue'
import UCollapsible from '@nuxt/ui/components/Collapsible.vue'
import UFieldGroup from '@nuxt/ui/components/FieldGroup.vue'
import UFormField from '@nuxt/ui/components/FormField.vue'
import UInput from '@nuxt/ui/components/Input.vue'
import USelect from '@nuxt/ui/components/Select.vue'
import UTextarea from '@nuxt/ui/components/Textarea.vue'
import { dateTime } from '../../shared/utils/format'
import { useConfigStore } from '../stores/config'
import { ideaEfforts, ideaModels } from '../stores/ideas'
import { useReviewsStore } from '../stores/reviews'

// Reviews: the registered repositories' open PRs (kept fresh while the page is shown), start one on any PR, pushed
// branch or two commits, and reopen mine.
// The Nuxt UI pilot screen (docs/ui §1): U* components instead of daisyUI classes and Ag* components.
const reviews = useReviewsStore()
const config = useConfigStore()
const router = useRouter()
const form = reactive({ repo: '', kind: 'branch' as 'pr' | 'branch' | 'range', pr: '', branch: '', base: '', head: '', model: '', effort: '', focus: '', instructions: '' })
const busy = ref(false)
const error = ref<string | null>(null)
const prFilter = ref('')
const repos = computed(() => config.config?.repositories.map((r) => r.name) ?? [])
const kinds = [{ value: 'pr', label: 'Pull request' }, { value: 'branch', label: 'Branch' }, { value: 'range', label: 'Commits' }] as const
// USelect items; '' (the default) can't be an item value, so "default" stands for it.
const repoFilterItems = computed(() => [{ value: 'all', label: 'All repositories' }, ...repos.value.map((r) => ({ value: r, label: r }))])
const modelItems = [{ value: 'default', label: 'Default model' }, ...ideaModels.map((m) => ({ value: m, label: m }))]
const effortItems = [{ value: 'default', label: 'Default effort' }, ...ideaEfforts.map((e) => ({ value: e, label: e }))]
const model = computed({ get: () => form.model || 'default', set: (v: string) => { form.model = v === 'default' ? '' : v } })
const effort = computed({ get: () => form.effort || 'default', set: (v: string) => { form.effort = v === 'default' ? '' : v } })
const repoFilter = computed({ get: () => prFilter.value || 'all', set: (v: string) => { prFilter.value = v === 'all' ? '' : v } })
const icons: Record<string, string> = { Reviewing: '⏳', Ready: '📝', Sent: '✅', Closed: '⚪', Failed: '⚠️' }

const prs = computed(() => (reviews.openPrs?.items ?? []).filter((p) => !prFilter.value || p.repo === prFilter.value))
const reviewing = ref<string | null>(null)

onMounted(async () => {
  reviews.watchOpenPrs()
  await Promise.all([reviews.loadMine().catch(() => {}), config.load().catch(() => {})])
  form.repo ||= repos.value[0] ?? ''
})
onUnmounted(() => reviews.stopOpenPrs())

/** My latest review of this PR, to reopen instead of starting another. */
function myReview(p: OpenPullRequest) {
  return reviews.mine.find((r) => r.repo === p.repo && r.target === 'pr' && r.pullRequestId === p.id)
}

async function begin(input: Parameters<typeof reviews.start>[0]): Promise<void> {
  error.value = null
  try {
    const s = await reviews.start(input)
    await router.push({ name: 'review', params: { id: s.id } })
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  }
}

async function reviewPr(p: OpenPullRequest): Promise<void> {
  reviewing.value = `${p.repo}/${p.id}`
  await begin({ repo: p.repo, pullRequestId: p.id })
  reviewing.value = null
}

async function start(): Promise<void> {
  busy.value = true
  await begin({
    repo: form.repo,
    pullRequestId: form.kind === 'pr' ? Number(form.pr.replace(/\D/g, '')) || null : null,
    branch: form.kind === 'branch' ? form.branch.trim() : null,
    base: form.kind === 'pr' ? null : form.base.trim() || null,
    head: form.kind === 'range' ? form.head.trim() : null,
    model: form.model || null,
    effort: form.effort || null,
    focus: form.focus.trim() || null,
    instructions: form.instructions.trim() || null,
  })
  busy.value = false
}
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-6">
    <h1 class="text-xl font-semibold">
      Reviews
    </h1>
    <div
      class="grid gap-2"
      data-testid="open-prs"
    >
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="font-semibold">
          Open pull requests
        </h2>
        <USelect
          v-if="repos.length > 1"
          v-model="repoFilter"
          :items="repoFilterItems"
          size="xs"
          aria-label="Repository"
        />
        <span
          v-if="reviews.openPrs"
          class="ml-auto text-xs text-muted"
        >updated {{ dateTime(reviews.openPrs.fetchedAt) }}</span>
        <UButton
          size="sm"
          variant="ghost"
          color="neutral"
          icon="i-lucide-refresh-cw"
          :class="reviews.openPrs ? '' : 'ml-auto'"
          @click="reviews.loadOpenPrs()"
        >
          Refresh
        </UButton>
      </div>
      <p
        v-for="f in reviews.openPrs?.failed ?? []"
        :key="f.repo"
        class="text-sm text-warning"
      >
        {{ f.repo }}: {{ f.reason }}
      </p>
      <p
        v-if="reviews.openPrsError"
        class="text-sm text-warning"
      >
        {{ reviews.openPrsError }}
      </p>
      <p
        v-else-if="reviews.openPrs && !prs.length"
        class="text-sm text-muted"
      >
        No open pull requests.
      </p>
      <ul class="grid gap-2">
        <li
          v-for="p in prs"
          :key="`${p.repo}/${p.id}`"
          data-testid="open-pr"
        >
          <UCard :ui="{ body: 'flex flex-wrap items-center gap-2 p-3 sm:p-3 text-sm' }">
            <a
              :href="p.url"
              target="_blank"
              rel="noopener noreferrer"
              class="font-mono text-xs text-brand-ink hover:underline"
            >!{{ p.id }}</a>
            <span class="font-medium">{{ p.title }}</span>
            <UBadge
              v-if="p.isDraft"
              color="neutral"
              variant="subtle"
              size="sm"
            >
              draft
            </UBadge>
            <span class="w-full text-xs text-muted sm:w-auto">
              {{ p.repo }} · {{ p.author }} · <span class="font-mono">{{ p.sourceBranch }}</span> → {{ p.targetBranch }} · {{ dateTime(p.createdAt) }}
            </span>
            <span class="ml-auto flex items-center gap-2">
              <UButton
                v-if="myReview(p)"
                :to="{ name: 'review', params: { id: myReview(p)!.id } }"
                size="xs"
                variant="ghost"
                color="neutral"
              >
                My review
              </UButton>
              <UButton
                size="sm"
                :loading="reviewing === `${p.repo}/${p.id}`"
                @click="reviewPr(p)"
              >
                {{ myReview(p) ? 'Review again' : 'Review' }}
              </UButton>
            </span>
          </UCard>
        </li>
      </ul>
    </div>

    <UCard
      as="form"
      aria-labelledby="new-review"
      :ui="{ body: 'grid gap-3' }"
      @submit.prevent="start"
    >
      <h2
        id="new-review"
        class="font-semibold"
      >
        New review
      </h2>
      <div class="flex flex-wrap items-end gap-3">
        <UFormField label="Repository">
          <USelect
            v-model="form.repo"
            :items="repos"
            size="sm"
            class="min-w-40"
            aria-label="Repository to review"
          />
        </UFormField>
        <UFieldGroup
          role="radiogroup"
          aria-label="What to review"
        >
          <UButton
            v-for="k in kinds"
            :key="k.value"
            size="sm"
            role="radio"
            :aria-checked="form.kind === k.value"
            :color="form.kind === k.value ? 'primary' : 'neutral'"
            :variant="form.kind === k.value ? 'solid' : 'outline'"
            @click="form.kind = k.value"
          >
            {{ k.label }}
          </UButton>
        </UFieldGroup>
      </div>
      <UFormField
        v-if="form.kind === 'pr'"
        label="PR number"
      >
        <UInput
          v-model="form.pr"
          size="sm"
          class="w-40"
          :ui="{ base: 'font-mono' }"
          inputmode="numeric"
          placeholder="3944"
        />
      </UFormField>
      <div
        v-else
        class="grid gap-3 sm:grid-cols-2"
      >
        <UFormField
          v-if="form.kind === 'branch'"
          label="Branch (pushed)"
        >
          <UInput
            v-model="form.branch"
            size="sm"
            class="w-full"
            :ui="{ base: 'font-mono' }"
            placeholder="feature/keyset-chunks"
            autocomplete="off"
          />
        </UFormField>
        <UFormField
          v-else
          label="Head commit"
        >
          <UInput
            v-model="form.head"
            size="sm"
            class="w-full"
            :ui="{ base: 'font-mono' }"
            autocomplete="off"
          />
        </UFormField>
        <UFormField :label="form.kind === 'branch' ? 'Compare with (optional)' : 'Base commit'">
          <UInput
            v-model="form.base"
            size="sm"
            class="w-full"
            :ui="{ base: 'font-mono' }"
            :placeholder="form.kind === 'branch' ? 'its base branch' : ''"
            autocomplete="off"
          />
        </UFormField>
      </div>
      <UCollapsible class="grid gap-2 text-sm">
        <UButton
          variant="link"
          color="neutral"
          size="sm"
          class="justify-self-start px-0"
          trailing-icon="i-lucide-chevron-down"
        >
          Options: model, effort, focus, instructions
        </UButton>
        <template #content>
          <div class="mt-2 grid gap-2 sm:grid-cols-2">
            <USelect
              v-model="model"
              :items="modelItems"
              size="sm"
              class="w-full"
              aria-label="Reviewer model"
            />
            <USelect
              v-model="effort"
              :items="effortItems"
              size="sm"
              class="w-full"
              aria-label="Reviewer effort"
            />
            <UInput
              v-model="form.focus"
              size="sm"
              class="w-full sm:col-span-2"
              maxlength="200"
              placeholder="Focus, e.g. security, tests"
              aria-label="Focus"
            />
            <UTextarea
              v-model="form.instructions"
              class="w-full sm:col-span-2"
              :rows="2"
              maxlength="4000"
              placeholder="Instructions for the reviewer (optional)"
              aria-label="Instructions for the reviewer"
            />
          </div>
        </template>
      </UCollapsible>
      <div>
        <UButton
          type="submit"
          :loading="busy"
          :disabled="!form.repo"
        >
          Review it
        </UButton>
      </div>
      <p
        v-if="error"
        class="text-sm text-error"
        role="alert"
      >
        {{ error }}
      </p>
    </UCard>

    <div class="grid gap-2">
      <h2 class="font-semibold">
        My reviews and reviews from chat
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
            class="flex flex-wrap items-center gap-2 rounded-lg border border-default p-3 text-sm hover:bg-elevated"
          >
            <span>{{ icons[r.status] ?? '' }}</span>
            <span class="font-medium">{{ r.repo }}</span>
            <span class="font-mono text-xs">{{ r.target === 'pr' ? `PR !${r.pullRequestId}` : r.headRef }}</span>
            <span class="text-muted">→ {{ r.baseRef }}</span>
            <UBadge
              v-if="r.chatLink"
              color="neutral"
              variant="subtle"
              size="sm"
            >
              💬 {{ r.createdBy }}
            </UBadge>
            <span class="ml-auto text-xs text-muted">{{ r.findings.length }} finding(s) · {{ dateTime(r.createdAt) }}</span>
          </RouterLink>
        </li>
      </ul>
    </div>
  </section>
</template>
