<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { ApiError } from '../../api/http'
import type { ModelProfile, StepCheck, StepModel } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

/** Ready-made providers: they fill in the endpoint and models; the API key is still yours to paste. */
const presets: Record<string, { name: string; baseUrl: string; model: string; smallModel: string }> = {
  deepseek: { name: 'deepseek', baseUrl: 'https://api.deepseek.com/anthropic', model: 'deepseek-flash[1m]', smallModel: 'deepseek-flash' },
}
const stepInfo: Record<string, { title: string; hint: string; claudeOnly?: boolean }> = {
  plan: { title: 'Plan', hint: 'clarifies and writes the plan, before approval' },
  implement: { title: 'Implement', hint: 'writes the code, until the PR' },
  fix: { title: 'Fix', hint: 'answers review comments on the PR' },
  handoff: { title: 'Handoff', hint: 'hands the job over to a person' },
  review: { title: 'Review', hint: '!review: runs on Claude', claudeOnly: true },
  chat: { title: 'Chat', hint: '!chat: runs on Claude', claudeOnly: true },
}
const efforts = ['low', 'medium', 'high', 'xhigh', 'max']

const setup = useSetupStore()
const editing = ref<'new' | string | null>(null)
const form = reactive({ name: '', baseUrl: '', model: '', smallModel: '', apiKey: '' })
const steps = ref<StepModel[]>([])
const busy = ref<string | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadModels())
watch(() => setup.models, (m) => { steps.value = (m?.steps ?? []).map((s) => ({ ...s })) }, { immediate: true })

function edit(profile: ModelProfile | null, preset?: string): void {
  const from = profile ?? (preset ? presets[preset] : null)
  Object.assign(form, { name: from?.name ?? '', baseUrl: from?.baseUrl ?? '', model: from?.model ?? '', smallModel: from?.smallModel ?? '', apiKey: '' })
  editing.value = profile?.name ?? 'new'
  check.value = null
  error.value = null
}

const saved = (name: string) => setup.models?.profiles.find((p) => p.name === name) ?? null
const input = () => ({ ...form, smallModel: form.smallModel.trim() || null })

async function run(action: string, work: () => Promise<StepCheck | string>): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  try {
    const result = await work()
    check.value = typeof result === 'string' ? { ok: true, message: result, fix: null } : result
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}

const test = () => run('test', () => setup.testProfile(input()))
const save = () => run('save', async () => {
  await setup.saveProfile(input())
  form.apiKey = ''   // write-only: the key isn't kept in the page
  editing.value = null
  return `Provider ${form.name.trim().toLowerCase()} saved.`
})
const remove = (name: string) => run(`remove:${name}`, async () => {
  await setup.removeProfile(name)
  return `Provider ${name} removed.`
})
const saveSteps = () => run('steps', async () => {
  await setup.saveSteps(steps.value)
  return 'Models per step saved.'
})
</script>

<template>
  <section class="grid gap-6">
    <div class="grid gap-2">
      <h2 class="text-lg font-semibold">
        Models
      </h2>
      <p class="text-sm text-muted">
        Every step runs on your Claude subscription unless you pick another model or provider for it. A provider is
        any Anthropic-compatible endpoint, such as DeepSeek: Claude Code still runs the agent, with the provider's model.
      </p>
    </div>

    <div class="grid gap-3">
      <h3 class="font-medium">
        Providers
      </h3>
      <p
        v-if="setup.models && !setup.models.profiles.length"
        class="text-sm text-muted"
      >
        None yet.
      </p>
      <ul class="grid gap-2">
        <li
          v-for="p in setup.models?.profiles ?? []"
          :key="p.name"
          class="flex flex-wrap items-center gap-3 rounded-box border border-base-300 p-3 text-sm"
        >
          <span class="font-mono font-semibold">{{ p.name }}</span>
          <span class="text-muted">{{ p.model }} · {{ p.baseUrl }}</span>
          <span :class="p.apiKey.set ? 'badge badge-success badge-sm' : 'badge badge-warning badge-sm'">
            {{ p.apiKey.set ? 'key set' : 'no key' }}
          </span>
          <span class="ml-auto flex gap-2">
            <AgButton
              size="sm"
              variant="outline"
              @click="edit(p)"
            >Edit</AgButton>
            <AgButton
              size="sm"
              variant="outline"
              :loading="busy === `remove:${p.name}`"
              @click="remove(p.name)"
            >Remove</AgButton>
          </span>
        </li>
      </ul>
      <div
        v-if="!editing"
        class="flex flex-wrap gap-2"
      >
        <AgButton
          variant="outline"
          @click="edit(null, 'deepseek')"
        >
          Add DeepSeek
        </AgButton>
        <AgButton
          variant="outline"
          @click="edit(null)"
        >
          Add another provider
        </AgButton>
      </div>

      <form
        v-else
        class="grid gap-4 rounded-box border border-base-300 p-4"
        @submit.prevent="save"
      >
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="grid gap-1">
            <span class="text-sm font-medium">Name</span>
            <input
              v-model="form.name"
              class="input w-full font-mono"
              autocomplete="off"
              :readonly="editing !== 'new'"
              placeholder="deepseek"
            >
          </label>
          <label class="grid gap-1">
            <span class="text-sm font-medium">Endpoint</span>
            <input
              v-model="form.baseUrl"
              class="input w-full font-mono"
              type="url"
              autocomplete="off"
              placeholder="https://…/anthropic"
            >
          </label>
          <label class="grid gap-1">
            <span class="text-sm font-medium">Model</span>
            <input
              v-model="form.model"
              class="input w-full font-mono"
              autocomplete="off"
            >
          </label>
          <label class="grid gap-1">
            <span class="text-sm font-medium">Small model (optional)</span>
            <input
              v-model="form.smallModel"
              class="input w-full font-mono"
              autocomplete="off"
            >
          </label>
        </div>
        <p class="text-xs text-muted">
          Check the endpoint and model IDs in the provider's Claude Code guide: providers rename models.
        </p>
        <SecretField
          v-model="form.apiKey"
          label="API key"
          :status="editing === 'new' ? null : (saved(editing)?.apiKey ?? null)"
          hint="Stored on the server and never shown again. Leave it empty to keep the saved one."
        />
        <div class="flex flex-wrap gap-2">
          <AgButton
            variant="outline"
            :loading="busy === 'test'"
            @click="test"
          >
            Send a test prompt
          </AgButton>
          <AgButton
            type="submit"
            :loading="busy === 'save'"
          >
            Save provider
          </AgButton>
          <AgButton
            variant="ghost"
            @click="editing = null"
          >
            Cancel
          </AgButton>
        </div>
      </form>
    </div>

    <div class="grid gap-3">
      <h3 class="font-medium">
        Models per step
      </h3>
      <p class="text-xs text-muted">
        Empty fields use the defaults: the subscription's model, or the provider's own model.
      </p>
      <div class="overflow-x-auto">
        <table class="table table-sm">
          <thead>
            <tr>
              <th>Step</th>
              <th>Provider</th>
              <th>Model</th>
              <th>Effort</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="s in steps"
              :key="s.step"
            >
              <td>
                <div class="font-medium">
                  {{ stepInfo[s.step]?.title ?? s.step }}
                </div>
                <div class="text-xs text-muted">
                  {{ stepInfo[s.step]?.hint }}
                </div>
              </td>
              <td>
                <select
                  v-model="s.profile"
                  class="select select-sm"
                  :aria-label="`${s.step} provider`"
                  :disabled="stepInfo[s.step]?.claudeOnly"
                >
                  <option :value="null">
                    Claude
                  </option>
                  <option
                    v-for="p in setup.models?.profiles ?? []"
                    :key="p.name"
                    :value="p.name"
                  >
                    {{ p.name }}
                  </option>
                </select>
              </td>
              <td>
                <input
                  v-model="s.model"
                  class="input input-sm w-44 font-mono"
                  :aria-label="`${s.step} model`"
                  autocomplete="off"
                  placeholder="default"
                >
              </td>
              <td>
                <select
                  v-model="s.effort"
                  class="select select-sm"
                  :aria-label="`${s.step} effort`"
                >
                  <option :value="null">
                    default
                  </option>
                  <option
                    v-for="e in efforts"
                    :key="e"
                    :value="e"
                  >
                    {{ e }}
                  </option>
                </select>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div>
        <AgButton
          :loading="busy === 'steps'"
          @click="saveSteps"
        >
          Save models per step
        </AgButton>
      </div>
    </div>

    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
