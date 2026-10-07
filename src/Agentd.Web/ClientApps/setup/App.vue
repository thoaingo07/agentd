<script setup lang="ts">
import { onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { AgCspProvider } from '../shared/components/ui'
import { steps } from './router'
import { useSetupStore } from '../shared/setup/stores/setup'

const setup = useSetupStore()
const route = useRoute()
onMounted(() => void setup.loadSession())
const index = () => steps.findIndex((s) => s.name === route.name)
</script>

<template>
  <!-- Strict CSP: base-ui-vue must not inject <style> elements. -->
  <AgCspProvider disable-style-elements>
    <main class="mx-auto grid max-w-4xl gap-6 p-6">
      <header class="grid gap-1">
        <h1 class="text-2xl font-semibold">
          Set up agentd
        </h1>
        <p class="text-sm text-muted">
          Each step saves its settings on this server. Secrets are stored encrypted and never shown again.
        </p>
      </header>

      <p
        v-if="setup.session === 'loading'"
        class="loading loading-spinner"
        aria-label="Loading"
      />

      <section
        v-else-if="setup.session === 'missing'"
        class="alert alert-warning grid gap-2"
        role="alert"
      >
        <p class="font-medium">
          This page needs the one-time setup link.
        </p>
        <p class="text-sm">
          Open the link the daemon printed when it started (it's also in <code>agentd daemon logs</code>), or run
          <code>agentd setup-link</code> on the server for a new one. If agentd is already set up, its settings are in the
          <a
            href="/"
            class="link"
          >web UI</a>.
        </p>
      </section>

      <div
        v-else
        class="grid gap-6 md:grid-cols-[12rem_1fr]"
      >
        <nav aria-label="Setup steps">
          <ol class="steps steps-vertical">
            <li
              v-for="(step, i) in steps"
              :key="step.name"
              class="step"
              :class="{ 'step-primary': i <= index() }"
            >
              <RouterLink
                :to="step.path"
                class="link-hover"
                :aria-current="step.name === route.name ? 'step' : undefined"
              >
                {{ step.title }}
              </RouterLink>
            </li>
          </ol>
        </nav>
        <div class="grid content-start gap-4">
          <div
            v-if="setup.restartRequired"
            class="alert alert-info text-sm"
            role="status"
          >
            Saved. The daemon reads these settings when it starts: run <code>agentd daemon restart</code> when you're done.
          </div>
          <RouterView />
        </div>
      </div>
    </main>
  </AgCspProvider>
</template>
