<script setup lang="ts">
import type { DiffLine } from '../../../shared/utils/diff'

defineProps<{ lines: DiffLine[]; numbers?: boolean }>()
const tint = { add: 'bg-success/10', del: 'bg-error/10', context: '' } as const
const mark = { add: '+', del: '-', context: ' ' } as const
</script>

<template>
  <table class="w-full border-collapse font-mono text-[12px] leading-5">
    <tbody>
      <tr
        v-for="(l, i) in lines"
        :key="i"
        :class="tint[l.kind]"
        :data-kind="l.kind"
      >
        <template v-if="numbers">
          <td class="w-10 select-none pr-2 text-right text-muted tabular-nums">
            {{ l.oldNo ?? '' }}
          </td>
          <td class="w-10 select-none pr-2 text-right text-muted tabular-nums">
            {{ l.newNo ?? '' }}
          </td>
        </template>
        <td
          class="w-4 select-none text-muted"
          aria-hidden="true"
        >
          {{ mark[l.kind] }}
        </td>
        <td class="whitespace-pre-wrap break-all">
          {{ l.text }}<span
            v-if="l.noNewline"
            class="ml-2 text-muted"
            title="No newline at end of file"
          >⏎̸</span>
        </td>
      </tr>
    </tbody>
  </table>
</template>
