<script setup lang="ts">
// The branch diff: a file list and the selected file's hunks (GET /api/jobs/{id}/diff).
import { computed, ref, watch } from 'vue'
import type { Diff } from '../../../shared/api/types'
import { parseDiff } from '../../../shared/utils/diff'
import DiffLines from './DiffLines.vue'

const props = defineProps<{ diff: Diff }>()
const files = computed(() => (props.diff.unifiedDiff ? parseDiff(props.diff.unifiedDiff) : []))
const selected = ref(0)
watch(files, (f) => {
  if (selected.value >= f.length) selected.value = 0
})
const file = computed(() => files.value[selected.value])
const statusLabel = { added: 'A', deleted: 'D', renamed: 'R', modified: 'M' } as const
</script>

<template>
  <div class="grid gap-3">
    <p class="text-xs text-muted">
      <span class="font-mono">{{ diff.headRef }}</span> against <span class="font-mono">{{ diff.baseRef }}</span>
      · {{ diff.files.length }} file{{ diff.files.length === 1 ? '' : 's' }}
    </p>
    <div
      v-if="diff.truncated"
      class="alert alert-warning alert-soft text-sm"
    >
      The diff is too large to show here (over 2 MB). Changed files:
    </div>
    <ul
      v-if="diff.truncated"
      class="font-mono text-[13px]"
    >
      <li
        v-for="f in diff.files"
        :key="f"
      >
        {{ f }}
      </li>
    </ul>
    <p
      v-else-if="!files.length"
      class="text-sm text-muted"
    >
      No changes yet.
    </p>
    <div
      v-else
      class="grid gap-3 md:grid-cols-[16rem_1fr]"
    >
      <ul
        class="menu menu-sm h-fit w-full rounded-box border border-base-300 p-1"
        aria-label="Changed files"
      >
        <li
          v-for="(f, i) in files"
          :key="f.newPath + i"
        >
          <button
            type="button"
            :class="{ 'menu-active': i === selected }"
            :aria-current="i === selected || undefined"
            @click="selected = i"
          >
            <span class="font-mono text-[11px] text-muted">{{ statusLabel[f.status] }}</span>
            <span
              class="min-w-0 flex-1 truncate font-mono text-[12px]"
              :title="f.newPath"
            >{{ f.newPath }}</span>
            <span class="text-[11px] tabular-nums"><span class="text-success">+{{ f.additions }}</span> <span class="text-error">−{{ f.deletions }}</span></span>
          </button>
        </li>
      </ul>
      <div
        v-if="file"
        class="min-w-0 overflow-x-auto rounded-box border border-base-300"
      >
        <p class="border-b border-base-300 px-3 py-2 font-mono text-[13px]">
          <span v-if="file.status === 'renamed'">{{ file.oldPath }} → </span>{{ file.newPath }}
        </p>
        <p
          v-if="file.binary"
          class="p-3 text-sm text-muted"
        >
          Binary file.
        </p>
        <template
          v-for="(h, i) in file.hunks"
          :key="i"
        >
          <p class="bg-base-200 px-3 font-mono text-[12px] text-muted">
            {{ h.header }}
          </p>
          <DiffLines
            :lines="h.lines"
            numbers
          />
        </template>
      </div>
    </div>
  </div>
</template>
