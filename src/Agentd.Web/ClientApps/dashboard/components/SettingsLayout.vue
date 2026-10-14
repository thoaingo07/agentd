<script setup lang="ts">
import { computed } from 'vue'
import { useSessionStore } from '../stores/session'
import { settingsAreas } from '../settingsAreas'

// Settings pages: a vertical menu beside the page (on top on narrow screens). Admins get the configuration areas and the
// system log and prompts; everyone else only has the general page, so there's no menu.
const session = useSessionStore()
const isAdmin = computed(() => session.user?.roles.includes('Admin') ?? false)
</script>

<template>
  <div
    class="mx-auto grid w-full max-w-6xl gap-6"
    :class="isAdmin ? 'md:grid-cols-[13rem_minmax(0,1fr)]' : 'max-w-3xl'"
  >
    <nav
      v-if="isAdmin"
      aria-label="Settings"
      class="md:sticky md:top-4 md:self-start"
    >
      <ul class="menu w-full rounded-box border border-base-300 bg-base-100">
        <li>
          <RouterLink
            :to="{ name: 'settings' }"
            exact-active-class="menu-active"
          >
            General
          </RouterLink>
        </li>
        <li class="menu-title">
          Configuration
        </li>
        <li
          v-for="area in settingsAreas"
          :key="area.name"
        >
          <RouterLink
            :to="{ name: 'settings-area', params: { area: area.name } }"
            exact-active-class="menu-active"
          >
            {{ area.title }}
          </RouterLink>
        </li>
        <li>
          <RouterLink
            :to="{ name: 'system-log' }"
            exact-active-class="menu-active"
          >
            System log
          </RouterLink>
        </li>
        <li>
          <RouterLink
            :to="{ name: 'prompts' }"
            active-class="menu-active"
          >
            Prompts
          </RouterLink>
        </li>
      </ul>
    </nav>
    <div class="min-w-0">
      <RouterView />
    </div>
  </div>
</template>
