<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

// Settings → Artifacts: where agents' pages are stored (the database, or S3-compatible storage such as Cloudflare R2)
// and the public domain their links use. The keys are write-only (docs/architect/artifacts.md).
const setup = useSetupStore()
const url = ref('')
const publicUrl = ref('')
const accessKeyId = ref('')
const secretAccessKey = ref('')
const busy = ref<'test' | 'save' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadArtifactStorage())
watch(() => setup.artifactStorage, (s) => {
  url.value = s?.url ?? ''
  publicUrl.value = s?.publicUrl ?? ''
}, { immediate: true })

async function run(action: 'test' | 'save'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  const input = { url: url.value, publicUrl: publicUrl.value, accessKeyId: accessKeyId.value, secretAccessKey: secretAccessKey.value }
  try {
    if (action === 'test') check.value = await setup.testArtifactStorage(input)
    else {
      await setup.saveArtifactStorage(input)
      accessKeyId.value = ''   // write-only: the keys aren't kept in the page
      secretAccessKey.value = ''
      check.value = { ok: true, message: url.value.trim() ? 'Saved.' : 'Saved: new pages are kept in the database.', fix: null }
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
      Artifacts
    </h2>
    <p class="text-sm text-muted">
      Pages agents publish are kept in the database unless you add S3-compatible storage (Cloudflare R2). With a public
      domain on the bucket, their links open there, for anyone with the link.
    </p>

    <label class="grid gap-1">
      <span class="text-sm font-medium">S3 API URL</span>
      <input
        v-model="url"
        type="url"
        class="input w-full font-mono"
        placeholder="https://<account>.r2.cloudflarestorage.com/agentd"
        spellcheck="false"
      >
      <span class="text-xs text-muted">
        <template v-if="setup.artifactStorage?.urlOverriddenBy">From {{ setup.artifactStorage.urlOverriddenBy }}: saving here doesn't change it. </template>
        Ends with the bucket's name. Empty keeps new pages in the database.
      </span>
    </label>
    <label class="grid gap-1">
      <span class="text-sm font-medium">Public domain</span>
      <input
        v-model="publicUrl"
        type="url"
        class="input w-full font-mono"
        placeholder="https://agentd.example.com"
        spellcheck="false"
      >
      <span class="text-xs text-muted">
        <template v-if="setup.artifactStorage?.publicUrlOverriddenBy">From {{ setup.artifactStorage.publicUrlOverriddenBy }}: saving here doesn't change it. </template>
        The bucket's custom domain (R2 → the bucket → Settings → Custom Domains). Empty keeps links on agentd.
      </span>
    </label>
    <SecretField
      v-model="accessKeyId"
      label="Access key ID"
      :status="setup.artifactStorage?.accessKeyId ?? null"
    />
    <SecretField
      v-model="secretAccessKey"
      label="Secret access key"
      :status="setup.artifactStorage?.secretAccessKey ?? null"
      hint="From an R2 API token with Object Read & Write on the bucket (R2 → Manage API tokens). Empty keeps the saved keys."
    />
    <div class="flex flex-wrap gap-2">
      <AgButton
        variant="outline"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Test connection
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
