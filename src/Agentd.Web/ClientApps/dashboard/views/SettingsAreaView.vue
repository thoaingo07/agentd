<script setup lang="ts">
import { computed } from 'vue'
import { useSetupStore } from '../../shared/setup/stores/setup'
import { settingsAreas } from '../settingsAreas'
import RestartButton from '../components/RestartButton.vue'

// Settings → <area>: the setup wizard's steps on /api/settings (Admins, the normal session and antiforgery).
const props = defineProps<{ area: string }>()
const setup = useSetupStore()
setup.base = '/api/settings'
const current = computed(() => settingsAreas.find((a) => a.name === props.area))
</script>

<template>
  <section class="grid max-w-3xl gap-4">
    <h1 class="sr-only">
      {{ current?.title ?? 'Settings' }}
    </h1>
    <div
      v-if="setup.restartRequired"
      class="alert alert-info grid gap-2 text-sm"
      role="status"
    >
      <p>Saved. agentd reads these settings when it starts: restart it to use them.</p>
      <RestartButton />
    </div>
    <component
      :is="current.component"
      v-if="current"
      :key="current.name"
    />
    <p v-else>
      No such settings page.
    </p>
  </section>
</template>
