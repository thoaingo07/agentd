<script setup lang="ts">
// The review page's left nav (like Plannotator's file tree): the changed files as a tree with their change letter and
// +/- counts, findings and comments per file, a "viewed" checkbox, and how many you've viewed. Arrow keys move through
// it (Right/Left open and close folders); a filter narrows it by path. `bare`: just the tree (a guided chapter's files),
// with no header, filter or viewed checkboxes.
import { computed, nextTick, ref } from 'vue'
import type { DiffFile, FileStatus } from '../utils/diff'
import { buildFileTree, folderPaths, visibleRows, type TreeNode } from '../utils/fileTree'
import { isGenerated } from '../utils/generated'

export interface FileMarks {
  /** The worst finding on the file, if any. */
  severity: 'breaks' | 'performance' | null
  findings: number
  comments: number
}

const props = defineProps<{
  files: DiffFile[]
  active: string | null
  viewed: ReadonlySet<string>
  marks: (path: string) => FileMarks
  bare?: boolean
  label?: string
}>()
const emit = defineEmits<{ select: [path: string]; toggleViewed: [path: string] }>()

const filter = ref('')
const collapsed = ref(new Set<string>())
const listEl = ref<HTMLElement | null>(null)

type Row = TreeNode<DiffFile & { path: string }>
const shown = computed(() => {
  const q = filter.value.trim().toLowerCase()
  return props.files.filter((f) => !q || f.newPath.toLowerCase().includes(q)).map((f) => ({ ...f, path: f.newPath }))
})
const tree = computed(() => buildFileTree(shown.value))
const rows = computed<Row[]>(() => visibleRows(tree.value, filter.value.trim() ? new Set() : collapsed.value))
const viewedCount = computed(() => props.files.filter((f) => props.viewed.has(f.newPath)).length)
const allCollapsed = computed(() => folderPaths(tree.value).every((p) => collapsed.value.has(p)))

const letter: Record<FileStatus, { text: string; tone: string; title: string }> = {
  added: { text: 'A', tone: 'font-semibold text-success', title: 'Added' },
  deleted: { text: 'D', tone: 'font-semibold text-error', title: 'Deleted' },
  renamed: { text: 'R', tone: 'font-semibold text-info', title: 'Renamed' },
  modified: { text: 'M', tone: 'text-muted/60', title: 'Modified' },
}

function toggle(path: string): void {
  const next = new Set(collapsed.value)
  if (!next.delete(path)) next.add(path)
  collapsed.value = next
}

function collapseAll(): void {
  collapsed.value = allCollapsed.value ? new Set() : new Set(folderPaths(tree.value))
}

/** Up/Down move between rows, Right/Left open and close folders (or step in and out), Enter/Space open a file. */
async function onKey(e: KeyboardEvent, i: number): Promise<void> {
  const row = rows.value[i]!
  let to: number | null = null
  if (e.key === 'ArrowDown') to = Math.min(i + 1, rows.value.length - 1)
  else if (e.key === 'ArrowUp') to = Math.max(i - 1, 0)
  else if (e.key === 'Home') to = 0
  else if (e.key === 'End') to = rows.value.length - 1
  else if (e.key === 'ArrowRight' && row.kind === 'folder') {
    if (collapsed.value.has(row.path)) toggle(row.path)
    else to = i + 1
  } else if (e.key === 'ArrowLeft') {
    if (row.kind === 'folder' && !collapsed.value.has(row.path)) toggle(row.path)
    else to = rows.value.findLastIndex((r, j) => j < i && r.kind === 'folder' && r.depth < row.depth)
  } else if ((e.key === 'Enter' || e.key === ' ') && row.kind === 'file') emit('select', row.path)
  else return

  e.preventDefault()
  if (to === null || to < 0) return
  await nextTick()
  listEl.value?.querySelectorAll<HTMLElement>('[role=treeitem]')[to]?.focus()
}

const indent = (depth: number) => ({ paddingLeft: `${0.25 + depth * 0.5}rem` })
</script>

<template>
  <nav
    :aria-label="label ?? 'Files'"
    class="grid min-h-0 content-start gap-2 text-xs"
    data-testid="file-tree"
  >
    <div
      v-if="!bare"
      class="flex items-center gap-2"
    >
      <span class="font-semibold">Files</span>
      <span
        class="text-muted tabular-nums"
        data-testid="viewed-count"
      >{{ viewedCount }}/{{ files.length }} viewed</span>
      <button
        type="button"
        class="ml-auto rounded px-1.5 py-0.5 text-muted hover:bg-base-200 hover:text-base-content"
        :title="allCollapsed ? 'Expand all folders' : 'Collapse all folders'"
        @click="collapseAll"
      >
        {{ allCollapsed ? 'Expand all' : 'Collapse all' }}
      </button>
    </div>
    <progress
      v-if="!bare"
      class="progress progress-primary h-1"
      :value="viewedCount"
      :max="Math.max(files.length, 1)"
      aria-label="Files viewed"
    />
    <input
      v-if="!bare"
      v-model="filter"
      type="search"
      class="input input-xs w-full"
      placeholder="Filter files"
      aria-label="Filter files"
    >
    <ul
      ref="listEl"
      role="tree"
      aria-label="Changed files"
      class="grid gap-px font-mono text-[12px]"
    >
      <li
        v-for="(row, i) in rows"
        :key="`${row.kind}:${row.path}`"
        role="treeitem"
        :tabindex="(row.kind === 'file' && row.path === active) || (!rows.some((r) => r.path === active) && i === 0) ? 0 : -1"
        :aria-expanded="row.kind === 'folder' ? !collapsed.has(row.path) : undefined"
        :aria-selected="row.kind === 'file' ? row.path === active : undefined"
        :aria-level="row.depth + 1"
        :title="row.path"
        class="group flex min-w-0 cursor-pointer items-center gap-1.5 rounded py-1 pr-1.5 outline-none focus-visible:ring-2 focus-visible:ring-primary"
        :class="row.kind === 'file' && row.path === active ? 'bg-primary/10 text-base-content' : 'hover:bg-base-200'"
        :style="indent(row.depth)"
        :data-file="row.kind === 'file' ? row.path : undefined"
        @click="row.kind === 'folder' ? toggle(row.path) : emit('select', row.path)"
        @keydown="onKey($event, i)"
      >
        <template v-if="row.kind === 'folder'">
          <svg
            class="size-3 shrink-0 text-muted transition-transform"
            :class="collapsed.has(row.path) && !filter.trim() ? '' : 'rotate-90'"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            aria-hidden="true"
          ><path
            stroke-linecap="round"
            stroke-linejoin="round"
            d="M9 5l7 7-7 7"
          /></svg>
          <span class="truncate text-muted">{{ row.name }}</span>
        </template>
        <template v-else>
          <input
            v-if="!bare"
            type="checkbox"
            class="checkbox checkbox-xs shrink-0 rounded-sm"
            :class="viewed.has(row.path) || row.path === active ? '' : 'opacity-0 group-hover:opacity-100 focus:opacity-100'"
            :checked="viewed.has(row.path)"
            :aria-label="`Viewed ${row.name}`"
            tabindex="-1"
            data-testid="viewed"
            @click.stop="emit('toggleViewed', row.path)"
          >
          <span
            class="w-2.5 shrink-0 text-center text-[10px]"
            :class="letter[row.file!.status].tone"
            :title="row.file!.status === 'renamed' ? `Renamed from ${row.file!.oldPath}` : letter[row.file!.status].title"
          >{{ letter[row.file!.status].text }}</span>
          <span
            class="truncate"
            :class="[viewed.has(row.path) ? 'text-muted line-through decoration-base-content/20' : '', isGenerated(row.path) ? 'italic text-muted' : '']"
          >{{ row.name }}</span>
          <span
            v-if="marks(row.path).findings"
            class="shrink-0 rounded-full px-1.5 text-[10px] font-semibold leading-4 text-white"
            :class="marks(row.path).severity === 'breaks' ? 'bg-error' : 'bg-warning'"
            :title="`${marks(row.path).findings} finding${marks(row.path).findings === 1 ? '' : 's'}`"
            data-testid="finding-badge"
          >{{ marks(row.path).findings }}</span>
          <span
            v-if="marks(row.path).comments"
            class="shrink-0 text-[10px] text-muted"
            :title="`${marks(row.path).comments} comment${marks(row.path).comments === 1 ? '' : 's'}`"
          >💬{{ marks(row.path).comments }}</span>
        </template>
        <span class="ml-auto inline-flex shrink-0 gap-1 whitespace-nowrap pl-1 text-[10px] tabular-nums">
          <span
            v-if="row.additions"
            class="text-success"
          >+{{ row.additions }}</span>
          <span
            v-if="row.deletions"
            class="text-error"
          >-{{ row.deletions }}</span>
        </span>
      </li>
    </ul>
    <p
      v-if="!rows.length"
      class="text-muted"
    >
      No files match.
    </p>
  </nav>
</template>
