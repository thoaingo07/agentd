<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useJobsStore } from '../stores/jobs'

// /jobs/:id (old links, chat links, Run work item) opens the job's work item with that run picked.
const props = defineProps<{ id: number }>()
const jobs = useJobsStore()
const router = useRouter()
const missing = ref(false)

onMounted(async () => {
  const known = jobs.byId.get(props.id) ?? jobs.details.get(props.id)?.job
  const workItemId = known?.workItemId ?? (await jobs.refresh(props.id).then(() => jobs.details.get(props.id)?.job.workItemId, () => undefined))
  if (workItemId === undefined) {
    missing.value = true
    return
  }

  await router.replace({ name: 'work-item', params: { id: workItemId }, query: { run: String(props.id) } })
})
</script>

<template>
  <p class="text-sm text-muted">
    {{ missing ? `agentd has no job #${id}.` : `Opening job #${id}…` }}
  </p>
</template>
