<script setup lang="ts">
import { ref, useId } from 'vue'
import type { SecretStatus } from '../../api/types'
import { AgButton } from '../../components/ui'
import { dateTime } from '../../utils/format'

// A write-only secret: once set, only "set · updated … by …" shows, until Replace opens an empty input.
const props = defineProps<{ label: string; status: SecretStatus | null; placeholder?: string; hint?: string }>()
const value = defineModel<string>({ default: '' })
const replacing = ref(false)
const id = useId()

function replace(): void {
  replacing.value = true
  value.value = ''
}

const isSet = () => props.status?.set === true && !replacing.value
</script>

<template>
  <div class="grid gap-1">
    <label
      :for="id"
      class="text-sm font-medium"
    >{{ label }}</label>
    <div
      v-if="isSet()"
      class="flex flex-wrap items-center gap-3 text-sm"
    >
      <span
        :id="id"
        class="badge badge-success badge-soft"
      >Set</span>
      <span class="text-muted">
        updated {{ status?.updatedAt ? dateTime(status.updatedAt) : '' }}<template v-if="status?.updatedBy"> by {{ status.updatedBy }}</template>
      </span>
      <AgButton
        variant="outline"
        size="sm"
        @click="replace"
      >
        Replace
      </AgButton>
    </div>
    <input
      v-else
      :id="id"
      v-model="value"
      type="password"
      autocomplete="off"
      spellcheck="false"
      class="input w-full font-mono"
      :placeholder="placeholder"
    >
    <p
      v-if="hint"
      class="text-xs text-muted"
    >
      {{ hint }}
    </p>
  </div>
</template>
