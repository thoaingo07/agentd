<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { MonitoredApp, MonitorStep, SaveResult, StepCheck } from '../../shared/api/types'
import { AgButton } from '../../shared/components/ui'
import CheckResult from '../../shared/setup/components/CheckResult.vue'
import { useSetupStore } from '../../shared/setup/stores/setup'
import { useConfigStore } from '../stores/config'

// Settings → Monitoring (incident-monitor.md §4.2): the incident monitor's apps in agentd.json, used after a restart.
// Test connection reads an app's last hour of errors with the sign-in agentd runs with.
const setup = useSetupStore()
const config = useConfigStore()
const step = ref<MonitorStep | null>(null)
const enabled = ref(false)
const tenantId = ref('')
const apps = ref<MonitoredApp[]>([])
const busy = ref<string | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
const repos = computed(() => config.config?.repositories.map((r) => r.name) ?? [])

async function load(): Promise<void> {
  step.value = await get<MonitorStep>('/api/settings/monitor')
  enabled.value = step.value.enabled
  tenantId.value = step.value.tenantId ?? ''
  apps.value = step.value.apps.map((a) => ({ ...a, roleName: a.roleName ?? '' }))
}
onMounted(() => {
  void load().catch((err) => (error.value = err instanceof ApiError ? err.message : 'The settings weren’t read.'))
  void config.load().catch(() => {})
})

const add = () => apps.value.push({ name: '', resourceId: '', repository: repos.value[0] ?? '', roleName: '' })
const clean = (a: MonitoredApp) => ({ name: a.name.trim(), resourceId: a.resourceId.trim(), repository: a.repository, roleName: a.roleName?.trim() || null })

async function run(key: string, work: () => Promise<StepCheck>): Promise<void> {
  busy.value = key
  check.value = null
  error.value = null
  try {
    check.value = await work()
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Something went wrong.'
  } finally {
    busy.value = null
  }
}

const test = (a: MonitoredApp, n: number) => run(`test-${n}`, () => send<StepCheck>('POST', '/api/settings/monitor/test', clean(a)))
const save = () => run('save', async () => {
  const saved = await send<SaveResult>('PUT', '/api/settings/monitor', { enabled: enabled.value, tenantId: tenantId.value.trim() || null, apps: apps.value.map(clean) })
  setup.restartRequired ||= saved.restartRequired
  await load()
  return { ok: true, message: saved.restartRequired ? 'Saved: restart agentd to use it.' : 'Saved: nothing changed.', fix: null }
})
</script>

<template>
  <section class="grid gap-4">
    <h2 class="text-lg font-semibold">
      Monitoring
    </h2>
    <p class="text-sm text-muted">
      agentd reads each app's Application Insights for exceptions and failed requests, alerts on new ones in the chat channel and
      the Inbox, and investigates them in the app's repository. It signs in as a managed identity, an <code>AZURE_*</code> service
      principal or this machine's <code>az login</code>, which needs Reader (or Monitoring Reader) on each resource. Changes apply after a restart.
    </p>
    <p
      v-if="step"
      class="text-sm"
      data-testid="monitor-running"
    >
      Running now: <strong>{{ step.running ? 'on' : 'off' }}</strong>
    </p>
    <label class="flex items-center gap-2">
      <input
        v-model="enabled"
        type="checkbox"
        class="toggle toggle-primary"
        aria-label="Monitor on"
      >
      <span class="text-sm font-medium">Watch these apps</span>
      <span
        v-if="step?.enabledOverriddenBy"
        class="text-xs text-muted"
      >From {{ step.enabledOverriddenBy }}: saving here doesn't change it.</span>
    </label>
    <label class="grid gap-1">
      <span class="text-sm font-medium">Directory (tenant) id</span>
      <input
        v-model="tenantId"
        class="input w-full font-mono"
        placeholder="The default directory"
        spellcheck="false"
        aria-label="Tenant id"
      >
    </label>

    <fieldset
      v-for="(a, n) in apps"
      :key="n"
      class="grid gap-2 rounded-box border border-base-300 p-3"
      data-testid="monitor-app"
    >
      <legend class="px-1 text-sm font-medium">
        {{ a.name || 'New app' }}
      </legend>
      <div class="grid gap-2 sm:grid-cols-2">
        <input
          v-model="a.name"
          class="input input-sm w-full"
          placeholder="orders-api"
          aria-label="Name"
        >
        <select
          v-model="a.repository"
          class="select select-sm w-full"
          aria-label="Repository"
        >
          <option
            v-for="r in repos.includes(a.repository) || !a.repository ? repos : [a.repository, ...repos]"
            :key="r"
            :value="r"
          >
            {{ r }}
          </option>
        </select>
      </div>
      <input
        v-model="a.resourceId"
        class="input input-sm w-full font-mono"
        placeholder="/subscriptions/…/resourceGroups/…/providers/Microsoft.Insights/components/…"
        spellcheck="false"
        aria-label="Resource id"
      >
      <input
        v-model="a.roleName"
        class="input input-sm w-full"
        placeholder="Cloud role name (only when several apps share the resource)"
        aria-label="Role name"
      >
      <div class="flex gap-2">
        <AgButton
          size="sm"
          variant="outline"
          :loading="busy === `test-${n}`"
          @click="test(a, n)"
        >
          Test connection
        </AgButton>
        <AgButton
          size="sm"
          variant="ghost"
          @click="apps.splice(n, 1)"
        >
          Remove
        </AgButton>
      </div>
    </fieldset>

    <div class="flex flex-wrap gap-2">
      <AgButton
        variant="outline"
        @click="add"
      >
        Add app
      </AgButton>
      <AgButton
        :loading="busy === 'save'"
        @click="save"
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
