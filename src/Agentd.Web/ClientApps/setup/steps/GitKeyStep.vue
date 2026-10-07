<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError } from '../../shared/api/http'
import type { StepCheck } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import CheckResult from '../components/CheckResult.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const url = ref('')
const busy = ref<'generate' | 'test' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
const copied = ref(false)
onMounted(() => {
  void setup.loadGitKey()
  if (!setup.azureDevOps) void setup.loadAzureDevOps().catch(() => {})
})

/** Where an Azure DevOps user adds an SSH public key (per organization). */
const azureDevOpsKeys = computed(() => {
  const org = setup.azureDevOps?.organization
  return org ? `https://dev.azure.com/${encodeURIComponent(org)}/_usersSettings/keys` : null
})

async function run(action: 'generate' | 'test'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  try {
    if (action === 'generate') await setup.generateGitKey()
    else check.value = await setup.testGitAccess(url.value.trim())
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}

async function copy(): Promise<void> {
  if (!setup.gitKey?.publicKey) return
  await navigator.clipboard.writeText(setup.gitKey.publicKey)
  copied.value = true
}
</script>

<template>
  <section class="grid gap-4">
    <h2 class="text-lg font-semibold">
      Git access
    </h2>
    <p class="text-sm text-muted">
      agentd clones and pushes over SSH with its own key. Generate it, add the public key where your repositories live,
      then test a clone URL.
    </p>

    <div
      v-if="setup.gitKey && !setup.gitKey.exists"
      class="grid justify-items-start gap-2"
    >
      <p class="text-sm">
        agentd has no SSH key yet, so git uses the SSH setup of the user it runs as.
      </p>
      <AgButton
        :loading="busy === 'generate'"
        @click="run('generate')"
      >
        Generate agentd's SSH key
      </AgButton>
    </div>

    <div
      v-else-if="setup.gitKey?.exists"
      class="grid gap-2"
    >
      <label
        for="public-key"
        class="text-sm font-medium"
      >Public key</label>
      <textarea
        id="public-key"
        :value="setup.gitKey.publicKey ?? ''"
        readonly
        rows="3"
        class="textarea w-full font-mono text-xs"
      />
      <div class="flex flex-wrap items-center gap-3 text-sm">
        <AgButton
          variant="outline"
          size="sm"
          @click="copy"
        >
          {{ copied ? 'Copied' : 'Copy' }}
        </AgButton>
        <span class="font-mono text-xs text-muted">{{ setup.gitKey.fingerprint }}</span>
      </div>
      <p class="text-sm">
        Add it in
        <a
          v-if="azureDevOpsKeys"
          :href="azureDevOpsKeys"
          target="_blank"
          rel="noopener noreferrer"
          class="link"
        >Azure DevOps → User settings → SSH public keys</a>
        <template v-else>
          Azure DevOps → User settings → SSH public keys
        </template>,
        or as a
        <a
          href="https://docs.github.com/en/authentication/connecting-to-github-with-ssh/managing-deploy-keys"
          target="_blank"
          rel="noopener noreferrer"
          class="link"
        >GitHub deploy key</a>
        (with write access, so agentd can push its branches).
      </p>
    </div>

    <label class="grid gap-1">
      <span class="text-sm font-medium">A repository's SSH clone URL</span>
      <input
        v-model="url"
        class="input w-full font-mono"
        placeholder="git@ssh.dev.azure.com:v3/myorg/MyProject/my-repo"
        autocomplete="off"
        spellcheck="false"
      >
    </label>
    <div>
      <AgButton
        variant="outline"
        :disabled="!url.trim()"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Test
      </AgButton>
    </div>
    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
