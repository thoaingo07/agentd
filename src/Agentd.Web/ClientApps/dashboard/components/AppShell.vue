<script setup lang="ts">
import ThemeToggle from './ThemeToggle.vue'
</script>

<template>
  <div class="min-h-screen bg-base-100 text-base-content">
    <header class="navbar border-b border-base-300 bg-base-100 px-4 lg:px-6">
      <div class="flex flex-1 items-center gap-6">
        <RouterLink
          to="/"
          class="flex items-center gap-2 font-semibold"
        >
          <span
            class="size-3 rounded-full bg-primary"
            aria-hidden="true"
          />
          agentd
        </RouterLink>
        <nav class="flex gap-4 text-sm">
          <RouterLink
            to="/"
            class="border-b-2 border-transparent py-1 text-muted hover:text-base-content"
            active-class="!border-primary !text-base-content"
          >
            Dashboard
          </RouterLink>
        </nav>
      </div>
      <div class="flex items-center gap-4">
        <span
          class="flex items-center gap-2 text-xs text-muted"
          title="Live connection arrives in Phase 3"
        >
          <span
            class="size-2 rounded-full bg-base-300"
            aria-hidden="true"
          />
          Offline
        </span>
        <ThemeToggle />
      </div>
    </header>
    <main class="p-4 lg:p-6">
      <slot />
    </main>
  </div>
</template>
