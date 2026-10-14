<script setup lang="ts">
// Settings → Prompts → one prompt (Admins): what agentd sent, whole, the system rules sent with it, and the reply.
import { ref, watch } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { PromptDetail } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import { dateTime, duration } from '../../shared/utils/format'
import { outcomeTone, sourceLabel, sourcePage } from '../components/prompts/sources'

const props = defineProps<{ id: number }>()
const prompt = ref<PromptDetail | null>(null)
const error = ref<string | null>(null)
const copied = ref<string | null>(null)

async function load(): Promise<void> {
  try {
    prompt.value = await get<PromptDetail>(`/api/prompts/${props.id}`)
    error.value = null
  } catch (err) {
    prompt.value = null
    error.value = err instanceof ApiError && err.status === 404 ? `agentd has no prompt #${props.id}.`
      : err instanceof ApiError && err.status === 403 ? 'Only Admins can read the prompts.' : 'Couldn\'t load the prompt.'
  }
}
watch(() => props.id, load, { immediate: true })

async function copy(what: string, text: string): Promise<void> {
  await navigator.clipboard.writeText(text)
  copied.value = what
  setTimeout(() => (copied.value = null), 1500)
}

const took = (p: PromptDetail) => (p.finishedAt ? duration((Date.parse(p.finishedAt) - Date.parse(p.startedAt)) / 1000) : null)
</script>

<template>
  <section class="grid gap-4">
    <nav
      class="breadcrumbs text-sm"
      aria-label="Breadcrumb"
    >
      <ul>
        <li>
          <RouterLink :to="{ name: 'prompts' }">
            Prompts
          </RouterLink>
        </li>
        <li aria-current="page">
          #{{ id }}
        </li>
      </ul>
    </nav>
    <p
      v-if="error"
      class="alert alert-warning text-sm"
      role="alert"
    >
      {{ error }}
    </p>
    <template v-else-if="prompt">
      <header class="grid gap-1">
        <h1 class="text-xl font-semibold">
          Prompt #{{ prompt.id }}
        </h1>
        <p
          class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted"
          data-testid="prompt-meta"
        >
          <RouterLink
            v-if="sourcePage(prompt.source, prompt.sourceId)"
            :to="sourcePage(prompt.source, prompt.sourceId)!"
            class="link"
          >
            {{ sourceLabel(prompt.source) }} #{{ prompt.sourceId }}
          </RouterLink>
          <span v-else>{{ sourceLabel(prompt.source) }} #{{ prompt.sourceId }}</span>
          <span
            v-if="prompt.step"
            class="badge badge-outline badge-sm"
          >{{ prompt.step }}</span>
          <span>{{ prompt.model ?? 'default model' }}{{ prompt.effort ? ` · ${prompt.effort}` : '' }}</span>
          <span v-if="prompt.profile">profile {{ prompt.profile }}</span>
          <span
            class="badge badge-sm"
            :class="prompt.outcome ? outcomeTone[prompt.outcome] ?? 'badge-ghost' : 'badge-info'"
          >{{ prompt.outcome ?? 'running' }}</span>
          <span class="tabular-nums">{{ dateTime(prompt.startedAt) }}<template v-if="took(prompt)"> · took {{ took(prompt) }}</template></span>
          <span
            v-if="prompt.session"
            class="font-mono text-[13px]"
            :title="prompt.session"
          >session {{ prompt.session.slice(0, 8) }}{{ prompt.resume ? ' (resumed)' : '' }}</span>
        </p>
      </header>
      <p
        v-if="prompt.error"
        class="alert alert-error text-sm"
        role="alert"
      >
        {{ prompt.error }}
      </p>
      <section
        class="grid gap-2"
        data-testid="prompt-text"
      >
        <div class="flex items-center gap-2">
          <h2 class="text-base font-semibold">
            Prompt
          </h2>
          <AgButton
            size="sm"
            variant="ghost"
            @click="copy('prompt', prompt.prompt)"
          >
            {{ copied === 'prompt' ? 'Copied' : 'Copy' }}
          </AgButton>
        </div>
        <pre class="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded-box border border-base-300 bg-base-200 p-3 text-xs">{{ prompt.prompt }}</pre>
      </section>
      <details
        v-if="prompt.rules"
        class="grid gap-2"
        data-testid="prompt-rules"
      >
        <summary class="cursor-pointer text-base font-semibold">
          System rules
        </summary>
        <pre class="mt-2 max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded-box border border-base-300 bg-base-200 p-3 text-xs">{{ prompt.rules }}</pre>
      </details>
      <section
        class="grid gap-2"
        data-testid="prompt-reply"
      >
        <div class="flex items-center gap-2">
          <h2 class="text-base font-semibold">
            Reply
          </h2>
          <AgButton
            v-if="prompt.reply"
            size="sm"
            variant="ghost"
            @click="copy('reply', prompt.reply)"
          >
            {{ copied === 'reply' ? 'Copied' : 'Copy' }}
          </AgButton>
        </div>
        <pre
          v-if="prompt.reply"
          class="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded-box border border-base-300 bg-base-200 p-3 text-xs"
        >{{ prompt.reply }}</pre>
        <p
          v-else
          class="text-sm text-muted"
        >
          {{ prompt.outcome ? 'No reply.' : 'Still running: the reply shows when it ends.' }}
        </p>
      </section>
    </template>
    <p
      v-else
      class="text-muted"
    >
      Loading…
    </p>
  </section>
</template>
