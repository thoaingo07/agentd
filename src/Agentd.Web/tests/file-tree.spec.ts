import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { buildFileTree, folderPaths, visibleRows } from '../ClientApps/shared/utils/fileTree'
import ReviewFileTree, { type FileMarks } from '../ClientApps/shared/review/ReviewFileTree.vue'
import type { DiffFile } from '../ClientApps/shared/utils/diff'

// The review page's left nav (review-sessions.md §2, reading aids): changed files as a tree, like Plannotator's.
const file = (newPath: string, additions: number, deletions: number, status: DiffFile['status'] = 'modified'): DiffFile =>
  ({ oldPath: status === 'renamed' ? `old/${newPath}` : newPath, newPath, status, binary: false, hunks: [], additions, deletions })
const files = [
  file('src/Agentd.Web/ClientApps/review/Tree.vue', 40, 0, 'added'),
  file('src/Agentd.Web/ClientApps/review/View.vue', 12, 3),
  file('src/Agentd.Application/Reviews/Guide.cs', 5, 9),
  file('docs/reviews.md', 2, 2),
  file('README.md', 1, 0),
]
const none: FileMarks = { severity: null, findings: 0, comments: 0 }

describe('file tree', () => {
  it('puts folders first, merges lone folders into one row and sums their counts', () => {
    const tree = buildFileTree(files.map((f) => ({ ...f, path: f.newPath })))

    expect(tree.map((n) => n.name)).toEqual(['docs', 'src', 'README.md'])
    const src = tree[1]!
    expect([src.additions, src.deletions]).toEqual([57, 12])
    expect(src.children.map((n) => n.name)).toEqual(['Agentd.Application/Reviews', 'Agentd.Web/ClientApps/review'])
    expect(src.children[1]!.children.map((n) => [n.name, n.depth])).toEqual([['Tree.vue', 2], ['View.vue', 2]])
    expect(folderPaths(tree)).toEqual(['docs', 'src', 'src/Agentd.Application/Reviews', 'src/Agentd.Web/ClientApps/review'])
    expect(visibleRows(tree, new Set(['src'])).map((n) => n.name)).toEqual(['docs', 'reviews.md', 'src', 'README.md'])
  })

  it('shows each file with its change letter, counts, findings and comments, and how many are viewed', async () => {
    const marks = (path: string): FileMarks => (path.endsWith('View.vue') ? { severity: 'breaks', findings: 2, comments: 1 } : none)
    const w = mount(ReviewFileTree, { props: { files, active: 'src/Agentd.Web/ClientApps/review/View.vue', viewed: new Set(['README.md']), marks } })

    expect(w.get('[data-testid=viewed-count]').text()).toBe('1/5 viewed')
    const view = w.get('[data-file="src/Agentd.Web/ClientApps/review/View.vue"]')
    expect(view.attributes('aria-selected')).toBe('true')
    expect(view.text()).toContain('M')
    expect(view.text()).toContain('+12')
    expect(view.text()).toContain('-3')
    expect(view.get('[data-testid=finding-badge]').text()).toBe('2')
    expect(view.get('[data-testid=finding-badge]').classes()).toContain('bg-error')
    expect(view.text()).toContain('💬1')
    expect(w.get('[data-file="src/Agentd.Web/ClientApps/review/Tree.vue"]').text()).toContain('A')
    expect((w.get('[data-file="README.md"] [data-testid=viewed]').element as HTMLInputElement).checked).toBe(true)

    await w.get('[data-file="docs/reviews.md"]').trigger('click')
    await w.get('[data-file="docs/reviews.md"] [data-testid=viewed]').trigger('click')
    expect(w.emitted('select')).toEqual([['docs/reviews.md']])
    expect(w.emitted('toggleViewed')).toEqual([['docs/reviews.md']])
  })

  it('folders open and close, Collapse all folds every one, and the filter narrows by path', async () => {
    const w = mount(ReviewFileTree, { props: { files, active: null, viewed: new Set<string>(), marks: () => none } })
    const names = () => w.findAll('[role=treeitem]').map((r) => r.text().replace(/[+-]\d+/g, '').trim())

    await w.findAll('[role=treeitem]').find((r) => r.text().startsWith('src'))!.trigger('click')
    expect(names()).toEqual(['docs', 'Mreviews.md', 'src', 'MREADME.md'])
    await w.findAll('button').find((b) => b.text() === 'Collapse all')!.trigger('click')
    expect(names()).toEqual(['docs', 'src', 'MREADME.md'])

    await w.get('input[aria-label="Filter files"]').setValue('guide')
    expect(names(), 'one path left: its folders merge').toEqual(['src/Agentd.Application/Reviews', 'MGuide.cs'])
    await w.get('input[aria-label="Filter files"]').setValue('nothing')
    expect(w.text()).toContain('No files match.')
  })

  it('arrow keys move through the rows, Left and Right close and open folders, Enter opens a file', async () => {
    const w = mount(ReviewFileTree, { props: { files, active: null, viewed: new Set<string>(), marks: () => none }, attachTo: document.body })
    const rows = () => w.findAll('[role=treeitem]')

    await rows()[0]!.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(rows()[1]!.element)
    await rows()[0]!.trigger('keydown', { key: 'ArrowLeft' })
    expect(rows()[0]!.attributes('aria-expanded')).toBe('false')
    await rows()[0]!.trigger('keydown', { key: 'ArrowRight' })
    expect(rows()[0]!.attributes('aria-expanded')).toBe('true')
    await rows()[1]!.trigger('keydown', { key: 'Enter' })
    expect(w.emitted('select')).toEqual([['docs/reviews.md']])
    w.unmount()
  })
})
