<script setup lang="ts">
import { AgCspProvider, AgTooltipProvider } from '../shared/components/ui'
import AppShell from './components/AppShell.vue'
</script>

<template>
  <!-- Strict CSP: base-ui-vue must not inject <style> elements (ScrollArea would). -->
  <AgCspProvider disable-style-elements>
    <AgTooltipProvider>
      <AppShell>
        <RouterView />
      </AppShell>
    </AgTooltipProvider>
  </AgCspProvider>
</template>
