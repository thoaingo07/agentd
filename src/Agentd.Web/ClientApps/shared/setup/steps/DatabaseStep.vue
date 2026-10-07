<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const connectionString = ref('')
const busy = ref<'test' | 'save' | 'migrate' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadDatabase())

async function run(action: 'test' | 'save' | 'migrate'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  try {
    if (action === 'test') check.value = await setup.testDatabase(connectionString.value)
    else if (action === 'migrate') check.value = await setup.migrateDatabase()
    else {
      await setup.saveDatabase(connectionString.value)
      connectionString.value = ''   // write-only: the value isn't kept in the page
      check.value = { ok: true, message: 'Connection string saved.', fix: null }
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
      Database
    </h2>
    <p class="text-sm text-muted">
      agentd keeps its jobs and history in PostgreSQL. Test the connection, save it, then create the schema.
    </p>
    <SecretField
      v-model="connectionString"
      label="PostgreSQL connection string"
      :status="setup.database?.connectionString ?? null"
      placeholder="Host=127.0.0.1;Port=5432;Username=agentd;Password=…;Database=agentd"
      hint="Test with the field empty to check the saved one."
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
        :disabled="!connectionString.trim()"
        :loading="busy === 'save'"
        @click="run('save')"
      >
        Save
      </AgButton>
      <AgButton
        variant="outline"
        :disabled="!setup.database?.connectionString.set"
        :loading="busy === 'migrate'"
        @click="run('migrate')"
      >
        Create or update the schema
      </AgButton>
    </div>
    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
