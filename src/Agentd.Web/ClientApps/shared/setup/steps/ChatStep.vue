<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { ApiError } from '../../api/http'
import type { StepCheck } from '../../api/types'
import { AgButton } from '../../components/ui'
import CheckResult from '../components/CheckResult.vue'
import SecretField from '../components/SecretField.vue'
import { useSetupStore } from '../stores/setup'

const setup = useSetupStore()
const form = reactive({ enabled: true, guildId: '', channelId: '', botToken: '', userName: '', userDiscordId: '' })
const busy = ref<'test' | 'save' | null>(null)
const check = ref<StepCheck | null>(null)
const error = ref<string | null>(null)
onMounted(() => void setup.loadChat())
watch(() => setup.chat, (step) => {
  if (!step) return
  form.guildId ||= step.guildId ?? ''
  form.channelId ||= step.channelId ?? ''
}, { immediate: true })

async function run(action: 'test' | 'save'): Promise<void> {
  busy.value = action
  check.value = null
  error.value = null
  const input = { ...form, userName: form.userName.trim() || null, userDiscordId: form.userDiscordId.trim() || null }
  try {
    if (action === 'test') check.value = await setup.testChat(input)
    else {
      await setup.saveChat(input)
      form.botToken = ''   // write-only: the token isn't kept in the page
      check.value = { ok: true, message: form.enabled ? 'Chat settings saved.' : 'Chat turned off.', fix: null }
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
      Chat (optional)
    </h2>
    <p class="text-sm text-muted">
      agentd posts each job in its own Discord thread, asks its questions there, and takes your answers and commands.
      Without chat, everything happens in the web UI.
    </p>
    <label class="flex items-center gap-2 text-sm">
      <input
        v-model="form.enabled"
        type="checkbox"
        class="checkbox checkbox-sm"
      >
      Use Discord
    </label>

    <template v-if="form.enabled">
      <p class="text-sm">
        Create a bot in the
        <a
          href="https://discord.com/developers/applications"
          target="_blank"
          rel="noopener noreferrer"
          class="link"
        >Discord Developer Portal</a>
        (turn on the Message Content intent), invite it to your server, and turn on Developer Mode in Discord
        (Settings → Advanced) to copy IDs with a right-click.
      </p>
      <SecretField
        v-model="form.botToken"
        label="Bot token"
        :status="setup.chat?.botToken ?? null"
        hint="Leave it empty to keep the saved one."
      />
      <div class="grid gap-4 sm:grid-cols-2">
        <label class="grid gap-1">
          <span class="text-sm font-medium">Server ID</span>
          <input
            v-model="form.guildId"
            class="input w-full font-mono"
            inputmode="numeric"
            autocomplete="off"
          >
        </label>
        <label class="grid gap-1">
          <span class="text-sm font-medium">Channel ID</span>
          <input
            v-model="form.channelId"
            class="input w-full font-mono"
            inputmode="numeric"
            autocomplete="off"
          >
        </label>
      </div>
      <fieldset class="grid gap-2">
        <legend class="mb-1 text-sm font-medium">
          You
        </legend>
        <p class="text-xs text-muted">
          agentd only answers people it knows. Add yourself (as an Admin), or add your Discord ID to an existing user with
          the same name.
          <template v-if="setup.chat?.users.length">
            It answers: {{ setup.chat.users.map((u) => u.name).join(', ') }}.
          </template>
        </p>
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="grid gap-1">
            <span class="text-sm">Your name</span>
            <input
              v-model="form.userName"
              class="input w-full"
              autocomplete="off"
            >
          </label>
          <label class="grid gap-1">
            <span class="text-sm">Your Discord user ID</span>
            <input
              v-model="form.userDiscordId"
              class="input w-full font-mono"
              inputmode="numeric"
              autocomplete="off"
            >
          </label>
        </div>
      </fieldset>
    </template>

    <div class="flex flex-wrap gap-2">
      <AgButton
        v-if="form.enabled"
        variant="outline"
        :loading="busy === 'test'"
        @click="run('test')"
      >
        Send a test message
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
