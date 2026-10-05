<script setup lang="ts">
defineProps<{ title: string; task: string }>()
</script>

<template>
  <section class="mx-auto max-w-3xl">
    <h1 class="text-xl font-semibold">
      {{ title }}
    </h1>
    <p class="mt-2 text-sm text-muted">
      Coming in {{ task }}.
    </p>
  </section>
</template>
