import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BranchSearch from '../ClientApps/shared/review/BranchSearch.vue'

// Search beyond the diff (review-sessions.md §2): file names and text at the review's head commit.
let asked: string[]

beforeEach(() => {
  asked = []
  vi.useFakeTimers()
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    asked.push(url.pathname + url.search)
    if (url.pathname === '/api/reviews/7/search' && url.searchParams.get('kind') === 'file') {
      return new Response(JSON.stringify({ files: ['src/Sync.cs', 'src/data/Reader.cs'], hits: [] }))
    }
    if (url.pathname === '/api/reviews/7/search') {
      return new Response(JSON.stringify({ files: [], hits: [{ path: 'src/data/Reader.cs', line: 3, snippet: 'var rows = <b>ReadAll(t);', matchStart: 14, matchLength: 8 }] }))
    }
    if (url.pathname === '/api/reviews/7/file') return new Response(JSON.stringify({ path: 'src/data/Reader.cs', text: 'class Reader {\n  // load\n  var rows = ReadAll(t);\n}', truncated: false, binary: false }))
    return new Response('{}', { status: 404 })
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function typed(w: ReturnType<typeof mount>, text: string): Promise<void> {
  await w.get('input[type=search]').setValue(text)
  await vi.advanceTimersByTimeAsync(260)
  await flushPromises()
}

describe('branch search', () => {
  it('finds files by name; a changed one opens its diff, another opens read-only at the commit', async () => {
    const w = mount(BranchSearch, { props: { sessionId: 7, changed: ['src/Sync.cs'] }, attachTo: document.body })

    await typed(w, 'sync')
    expect(asked.at(-1)).toBe('/api/reviews/7/search?q=sync&kind=file&limit=50')
    const rows = w.findAll('[data-testid=search-file]')
    expect(rows.map((r) => r.text())).toEqual([expect.stringContaining('Sync.cs'), expect.stringContaining('Reader.cs')])
    expect(rows[0]!.text()).toContain('changed')

    await rows[0]!.trigger('click')
    expect(w.emitted('openDiff')).toEqual([['src/Sync.cs']])

    await rows[1]!.trigger('click')
    await flushPromises()
    expect(asked.at(-1)).toBe('/api/reviews/7/file?path=src%2Fdata%2FReader.cs')
    expect(w.get('[data-testid=search-viewer]').text()).toContain('read-only')
    w.unmount()
  })

  it('finds text with the match marked as text (never HTML) and opens the file at its line', async () => {
    const w = mount(BranchSearch, { props: { sessionId: 7, changed: [] }, attachTo: document.body })
    await w.findAll('[role=radio]').find((b) => b.text() === 'Text')!.trigger('click')
    await w.get('input[type=checkbox]').setValue(true)

    await typed(w, 'ReadAll(')
    expect(asked.at(-1)).toBe('/api/reviews/7/search?q=ReadAll%28&kind=text&limit=50&ignoreCase=false')
    const hit = w.get('[data-testid=search-hit]')
    expect(hit.text()).toContain('src/data/Reader.cs:3')
    expect(hit.get('mark').text()).toBe('ReadAll(')
    expect(hit.find('b').exists(), 'the snippet is text').toBe(false)

    await hit.trigger('click')
    await flushPromises()
    const lines = w.findAll('.ag-code-line')
    expect(lines).toHaveLength(4)
    expect(lines[2]!.classes()).toContain('is-hit')
    w.unmount()
  })

  it('"/" focuses the box, Esc clears it, and an empty box asks nothing', async () => {
    const w = mount(BranchSearch, { props: { sessionId: 7, changed: [] }, attachTo: document.body })

    window.dispatchEvent(new KeyboardEvent('keydown', { key: '/' }))
    expect(document.activeElement).toBe(w.get('input[type=search]').element)
    await typed(w, '   ')
    expect(asked).toEqual([])

    await typed(w, 'sync')
    await w.get('input[type=search]').trigger('keydown', { key: 'Escape' })
    expect((w.get('input[type=search]').element as HTMLInputElement).value).toBe('')
    expect(w.find('[aria-label="Search results"]').exists()).toBe(false)
    w.unmount()
  })
})
