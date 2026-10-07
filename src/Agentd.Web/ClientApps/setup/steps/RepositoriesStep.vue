<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { StepCheck } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import CheckResult from '../components/CheckResult.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const form = reactive({ url: '', name: '', baseBranch: '', matchTag: '', areaPaths: '' })
const busy = ref<'test' | 'add' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadRepositories())

async function run(action: 'test' | 'add'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  try {
    if (action === 'test') check.value = await setup.testRepository(form.url.trim())
    else {
      const added = await setup.addRepository({
        url: form.url.trim(),
        name: form.name.trim() || null,
        baseBranch: form.baseBranch.trim() || null,
        matchTag: form.matchTag.trim() || null,
        matchAreaPaths: form.areaPaths.split(/[\n,]/).map((p) => p.trim()).filter((p) => p.length > 0),
      })
      Object.assign(form, { url: '', name: '', baseBranch: '', matchTag: '', areaPaths: '' })
      check.value = { ok: true, message: `Added ${added.name} (base branch ${added.baseBranch}). It's cloned when the daemon restarts.`, fix: null }
    }
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
      Repositories
    </h2>
    <p class="text-sm text-muted">
      The repositories agentd works in. A work item goes to the repository whose tag it has (default
      <code>repo:&lt;name&gt;</code>) or whose area path it's under. More can be added later with <code>agentd repo add</code>.
    </p>

    <ul
      v-if="setup.repositories?.length"
      class="grid gap-2"
      aria-label="Added repositories"
    >
      <li
        v-for="r in setup.repositories"
        :key="r.url"
        class="rounded-box border border-base-300 p-3 text-sm"
      >
        <p class="font-medium">
          {{ r.name }} <span class="text-muted">· {{ r.baseBranch }}</span>
        </p>
        <p class="font-mono text-xs break-all text-muted">
          {{ r.url }}
        </p>
        <p class="text-xs">
          <template v-if="r.matchTag">
            tag <code>{{ r.matchTag }}</code>
          </template>
          <template v-if="r.matchAreaPaths.length">
            area {{ r.matchAreaPaths.join(', ') }}
          </template>
        </p>
      </li>
    </ul>
    <p
      v-else-if="setup.repositories"
      class="text-sm"
    >
      No repositories yet.
    </p>

    <label class="grid gap-1">
      <span class="text-sm font-medium">Clone URL</span>
      <input
        v-model="form.url"
        class="input w-full font-mono"
        placeholder="git@ssh.dev.azure.com:v3/myorg/MyProject/my-repo"
        autocomplete="off"
        spellcheck="false"
      >
    </label>
    <details class="text-sm">
      <summary class="cursor-pointer">
        Options (name, base branch, matching)
      </summary>
      <div class="mt-3 grid gap-3 sm:grid-cols-2">
        <label class="grid gap-1">
          <span>Name</span>
          <input
            v-model="form.name"
            class="input w-full"
            placeholder="the repository's name"
            autocomplete="off"
          >
        </label>
        <label class="grid gap-1">
          <span>Base branch</span>
          <input
            v-model="form.baseBranch"
            class="input w-full"
            placeholder="the remote's default branch"
            autocomplete="off"
          >
        </label>
        <label class="grid gap-1">
          <span>Work item tag</span>
          <input
            v-model="form.matchTag"
            class="input w-full"
            placeholder="repo:<name>"
            autocomplete="off"
          >
        </label>
        <label class="grid gap-1">
          <span>Area paths (comma-separated)</span>
          <input
            v-model="form.areaPaths"
            class="input w-full"
            placeholder="MyProject\Team"
            autocomplete="off"
          >
        </label>
      </div>
    </details>
    <div class="flex flex-wrap gap-2">
      <AgButton
        variant="outline"
        :disabled="!form.url.trim()"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Test
      </AgButton>
      <AgButton
        :disabled="!form.url.trim()"
        :loading="busy === 'add'"
        @click="run('add')"
      >
        Add
      </AgButton>
    </div>
    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
