<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const form = reactive({ organization: '', project: '', auth: 'Pat', pat: '' })
const busy = ref<'test' | 'save' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadAzureDevOps())
watch(() => setup.azureDevOps, (step) => {
  if (!step) return
  form.organization ||= step.organization ?? ''
  form.project ||= step.project ?? ''
  form.auth = step.auth
}, { immediate: true })

async function run(action: 'test' | 'save'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  const input = { organization: form.organization, project: form.project, auth: form.auth, pat: form.auth === 'Pat' ? form.pat : null }
  try {
    if (action === 'test') check.value = await setup.testAzureDevOps(input)
    else {
      await setup.saveAzureDevOps(input)
      form.pat = ''   // write-only: the token isn't kept in the page
      check.value = { ok: true, message: 'Azure DevOps settings saved.', fix: null }
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
      Azure DevOps
    </h2>
    <p class="text-sm text-muted">
      Where agentd finds work items tagged for it and opens pull requests.
    </p>
    <label class="grid gap-1">
      <span class="text-sm font-medium">Organization</span>
      <input
        v-model="form.organization"
        class="input w-full"
        placeholder="myorg or https://dev.azure.com/myorg"
        autocomplete="off"
      >
    </label>
    <label class="grid gap-1">
      <span class="text-sm font-medium">Project</span>
      <input
        v-model="form.project"
        class="input w-full"
        autocomplete="off"
      >
    </label>
    <fieldset class="grid gap-2">
      <legend class="mb-1 text-sm font-medium">
        Sign in with
      </legend>
      <label class="flex items-center gap-2 text-sm">
        <input
          v-model="form.auth"
          type="radio"
          value="Pat"
          class="radio radio-sm"
        >
        A personal access token (recommended on a server)
      </label>
      <label class="flex items-center gap-2 text-sm">
        <input
          v-model="form.auth"
          type="radio"
          value="AzCli"
          class="radio radio-sm"
        >
        <span><code>az login</code> on this server</span>
      </label>
    </fieldset>
    <SecretField
      v-if="form.auth === 'Pat'"
      v-model="form.pat"
      label="Personal access token"
      :status="setup.azureDevOps?.pat ?? null"
      hint="Scopes: Work Items (read & write), Code (read & write), Build (read). Leave it empty to keep the saved one."
    />
    <div class="flex flex-wrap gap-2">
      <AgButton
        variant="outline"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Test
      </AgButton>
      <AgButton
        :disabled="!form.organization.trim() || !form.project.trim()"
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
