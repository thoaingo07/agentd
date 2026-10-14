<script setup lang="ts">
import { computed } from 'vue'
import { useSessionStore } from '../../stores/session'

// "Prompts": what agentd sent the agent for this job, idea or review, on the Prompts page. Admins only, like the page.
const props = defineProps<{ source: string; sourceId: number }>()
const session = useSessionStore()
const isAdmin = computed(() => session.user?.roles.includes('Admin') ?? false)
</script>

<template>
  <RouterLink
    v-if="isAdmin"
    :to="{ name: 'prompt-conversation', params: { source: props.source, sourceId: props.sourceId } }"
    class="link"
    title="What agentd sent the agent, with its rules and the reply"
    data-testid="prompts-link"
  >
    Prompts
  </RouterLink>
</template>
