<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { ReviewItem } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const busy = ref<'review' | 'finish' | null>(null)
const error = ref<string | null>(null)
onMounted(() => void run('review'))

const blocked = computed(() => setup.review?.some((i) => i.required && !i.check.ok) ?? true)
const mark = (i: ReviewItem) => (i.check.ok ? '✅' : i.required ? '❌' : '⚠️')
const label = (i: ReviewItem) => (i.check.ok ? 'passed' : i.required ? 'failed, required' : 'warning')

async function run(action: 'review' | 'finish'): Promise<void> {
  busy.value = action
  error.value = null
  try {
    if (action === 'review') await setup.loadReview()
    else await setup.finish()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <section class="grid gap-4">
    <h2 class="text-lg font-semibold">
      Review &amp; finish
    </h2>

    <div
      v-if="setup.completedAt"
      class="alert alert-success grid gap-2"
      role="status"
    >
      <p class="font-medium">
        agentd is set up.
      </p>
      <p class="text-sm">
        Restart the daemon so it uses these settings: <code>agentd daemon restart</code>. Then open the
        <a
          href="/"
          class="link"
        >web UI</a>. This setup link no longer works; settings can be changed by an Admin from now on.
      </p>
    </div>

    <template v-else>
      <p class="text-sm text-muted">
        Each step is checked again with the saved settings. ❌ must be fixed before finishing; ⚠️ can wait.
      </p>
      <p
        v-if="busy === 'review'"
        class="flex items-center gap-2 text-sm"
      >
        <span
          class="loading loading-spinner loading-sm"
          aria-hidden="true"
        />
        Checking (Claude answers a test prompt, so this takes a few seconds)…
      </p>
      <ul
        v-else-if="setup.review"
        class="grid gap-2"
        aria-label="Checks"
      >
        <li
          v-for="(item, i) in setup.review"
          :key="i"
          class="flex gap-3 text-sm"
        >
          <span
            role="img"
            :aria-label="label(item)"
          >{{ mark(item) }}</span>
          <div class="grid gap-0.5">
            <p><span class="font-medium">{{ item.title }}</span>: {{ item.check.message }}</p>
            <p
              v-if="!item.check.ok && item.check.fix"
              class="text-xs text-muted"
            >
              Fix: {{ item.check.fix }}
            </p>
          </div>
        </li>
      </ul>
      <div class="flex flex-wrap gap-2">
        <AgButton
          variant="outline"
          :loading="busy === 'review'"
          @click="run('review')"
        >
          Check again
        </AgButton>
        <AgButton
          :disabled="blocked || busy !== null"
          :loading="busy === 'finish'"
          @click="run('finish')"
        >
          Finish setup
        </AgButton>
      </div>
      <p
        v-if="error"
        class="alert alert-error text-sm"
        role="alert"
      >
        {{ error }}
      </p>
    </template>
  </section>
</template>
