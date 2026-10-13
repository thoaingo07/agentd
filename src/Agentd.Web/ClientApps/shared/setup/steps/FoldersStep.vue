<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import { useSetupStore } from '../stores/setup'

// Settings → Folders: where job worktrees and the repository clones live (agentd.json, used after a restart). Existing
// worktrees and clones stay where they are; new ones go to the new folders.
const setup = useSetupStore()
const worktrees = ref('')
const repositories = ref('')
const busy = ref<'test' | 'save' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadFolders())
watch(() => setup.folders, (f) => {
  worktrees.value = f?.worktrees ?? ''
  repositories.value = f?.repositories ?? ''
}, { immediate: true })

async function run(action: 'test' | 'save'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  const input = { worktrees: worktrees.value, repositories: repositories.value }
  try {
    if (action === 'test') check.value = await setup.testFolders(input)
    else {
      const saved = await setup.saveFolders(input)
      check.value = { ok: true, message: saved.restartRequired ? 'Saved: restart agentd to use them.' : 'Saved: nothing changed.', fix: null }
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
      Folders
    </h2>
    <p class="text-sm text-muted">
      Where agentd works on disk. Each job gets its own worktree under the worktree root; the clones are agentd's copies
      of your repositories. Changes apply after a restart. Existing worktrees and clones stay where they are.
    </p>

    <label class="grid gap-1">
      <span class="text-sm font-medium">Worktree root</span>
      <input
        v-model="worktrees"
        class="input w-full font-mono"
        placeholder="~/.agentd/worktrees"
        spellcheck="false"
        data-testid="folders-worktrees"
      >
      <span class="text-xs text-muted">
        <template v-if="setup.folders?.worktreesOverriddenBy">From {{ setup.folders.worktreesOverriddenBy }}: saving here doesn't change it. </template>
        A full path, or one under your home (~/…). Jobs check out here: {root}/{repository}/wi-{id}. Empty uses agentd's home.
      </span>
    </label>
    <label class="grid gap-1">
      <span class="text-sm font-medium">Repository clones</span>
      <input
        v-model="repositories"
        class="input w-full font-mono"
        placeholder="~/.agentd/repos"
        spellcheck="false"
        data-testid="folders-repositories"
      >
      <span class="text-xs text-muted">
        <template v-if="setup.folders?.repositoriesOverriddenBy">From {{ setup.folders.repositoriesOverriddenBy }}: saving here doesn't change it. </template>
        agentd's bare clones, fetched before each job. Empty uses agentd's home.
      </span>
    </label>
    <div class="flex flex-wrap gap-2">
      <AgButton
        variant="outline"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Test folders
      </AgButton>
      <AgButton
        :loading="busy === 'save'"
        @click="run('save')"
      >
        Save
      </AgButton>
    </div>
    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
