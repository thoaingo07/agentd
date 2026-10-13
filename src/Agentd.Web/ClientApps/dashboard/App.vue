<script setup lang="ts">
import UApp from '@nuxt/ui/components/App.vue'
import { AgCspProvider, AgTooltipProvider } from '../shared/components/ui'
import AppShell from './components/AppShell.vue'
</script>

<template>
  <!-- Strict CSP: base-ui-vue must not inject <style> elements (ScrollArea would). -->
  <AgCspProvider disable-style-elements>
    <AgTooltipProvider>
      <!-- Nuxt UI's providers (tooltips, overlays) for the screens that use its components (pilot, docs/ui §1). -->
      <UApp>
        <AppShell>
          <RouterView />
        </AppShell>
      </UApp>
    </AgTooltipProvider>
  </AgCspProvider>
</template>
