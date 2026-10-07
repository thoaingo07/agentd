<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const token = ref('')
const busy = ref<'test' | 'save' | 'remove' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadClaude())

async function run(action: 'test' | 'save' | 'remove'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  try {
    if (action === 'test') check.value = await setup.testClaude(token.value)
    else if (action === 'remove') {
      await setup.removeClaudeToken()
      check.value = { ok: true, message: 'Token removed: agents use the server\'s own login.', fix: null }
    } else {
      await setup.saveClaudeToken(token.value)
      token.value = ''   // write-only: the token isn't kept in the page
      check.value = { ok: true, message: 'Token saved.', fix: null }
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
      Claude
    </h2>
    <p class="text-sm text-muted">
      Agents run Claude Code on your Claude subscription. A long-lived token is easiest on a server; logging in on the
      server works too.
    </p>

    <dl
      v-if="setup.claude"
      class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm"
    >
      <dt class="text-muted">
        Claude Code
      </dt>
      <dd>{{ setup.claude.server.installed ? setup.claude.server.version : 'not installed (npm install -g @anthropic-ai/claude-code)' }}</dd>
      <dt class="text-muted">
        Login on this server
      </dt>
      <dd>
        <template v-if="setup.claude.server.loggedIn">
          logged in ({{ setup.claude.server.method }}<template v-if="setup.claude.server.plan">, {{ setup.claude.server.plan }}</template>)
        </template>
        <template v-else>
          not logged in
        </template>
      </dd>
    </dl>

    <div class="grid gap-1 text-sm">
      <p class="font-medium">
        Get a token
      </p>
      <p>
        On any computer with a browser, run <code>claude setup-token</code>, sign in, and paste the token it prints here.
      </p>
    </div>
    <SecretField
      v-model="token"
      label="Claude token"
      :status="setup.claude?.token ?? null"
      placeholder="sk-ant-oat01-…"
      hint="Test with the field empty to check the saved token (or, without one, the server's login)."
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
        :disabled="!token.trim()"
        :loading="busy === 'save'"
        @click="run('save')"
      >
        Save
      </AgButton>
      <AgButton
        v-if="setup.claude?.token.set"
        variant="ghost"
        :loading="busy === 'remove'"
        @click="run('remove')"
      >
        Remove the token
      </AgButton>
    </div>
    <CheckResult
      :check="check"
      :error="error"
    />
  </section>
</template>
