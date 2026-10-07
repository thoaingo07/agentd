<script setup lang="ts">
import { computed } from 'vue'
import { useSetupStore } from '../../shared/setup/stores/setup'
import { settingsAreas } from '../settingsAreas'

// Settings → <area>: the setup wizard's steps on /api/settings (Admins, the normal session and antiforgery).
const props = defineProps<{ area: string }>()
const setup = useSetupStore()
setup.base = '/api/settings'
const current = computed(() => settingsAreas.find((a) => a.name === props.area))
</script>

<template>
  <section class="mx-auto grid max-w-3xl gap-4">
    <nav
      aria-label="Breadcrumb"
      class="breadcrumbs text-sm"
    >
      <ul>
        <li>
          <RouterLink :to="{ name: 'settings' }">
            Settings
          </RouterLink>
        </li>
        <li aria-current="page">
          {{ current?.title ?? area }}
        </li>
      </ul>
    </nav>
    <h1 class="sr-only">
      {{ current?.title ?? 'Settings' }}
    </h1>
    <div
      v-if="setup.restartRequired"
      class="alert alert-info text-sm"
      role="status"
    >
      Saved. The daemon reads these settings when it starts: run <code>agentd daemon restart</code> to use them.
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
