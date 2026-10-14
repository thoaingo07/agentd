// The review page's file tree (like Plannotator's): folders first, then files, each sorted by name; a folder with one
// folder inside is shown as one row ("src/Agentd.Web/ClientApps"), and every folder sums its files' +/- counts.

export interface TreeFile {
  path: string
  additions: number
  deletions: number
}

export interface TreeNode<F extends TreeFile = TreeFile> {
  kind: 'folder' | 'file'
  /** The file's name, or the folder's (merged) name. */
  name: string
  /** The file's path, or the folder's full path. */
  path: string
  depth: number
  file?: F
  children: TreeNode<F>[]
  additions: number
  deletions: number
}

interface Trie<F> {
  folders: Map<string, Trie<F>>
  files: F[]
}

export function buildFileTree<F extends TreeFile>(files: readonly F[]): TreeNode<F>[] {
  const root: Trie<F> = { folders: new Map(), files: [] }
  for (const f of files) {
    const parts = f.path.split('/').filter(Boolean)
    let at = root
    for (const part of parts.slice(0, -1)) {
      if (!at.folders.has(part)) at.folders.set(part, { folders: new Map(), files: [] })
      at = at.folders.get(part)!
    }
    at.files.push(f)
  }

  return nodes(root, '', 0)
}

function nodes<F extends TreeFile>(trie: Trie<F>, parent: string, depth: number): TreeNode<F>[] {
  const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name)
  const folders = [...trie.folders].map(([name, child]) => {
    // Merge a chain of lone folders into one row.
    let label = name
    let inner = child
    while (inner.files.length === 0 && inner.folders.size === 1) {
      const [nextName, next] = [...inner.folders][0]!
      label = `${label}/${nextName}`
      inner = next
    }

    const path = parent ? `${parent}/${label}` : label
    const children = nodes(inner, path, depth + 1)
    return { kind: 'folder' as const, name: label, path, depth, children, additions: sum(children, 'additions'), deletions: sum(children, 'deletions') }
  })
  const files = trie.files.map((f) => ({
    kind: 'file' as const, name: f.path.slice(f.path.lastIndexOf('/') + 1), path: f.path, depth, file: f, children: [], additions: f.additions, deletions: f.deletions,
  }))
  return [...folders.sort(byName), ...files.sort(byName)]
}

const sum = (list: TreeNode[], key: 'additions' | 'deletions') => list.reduce((n, c) => n + c[key], 0)

/** The rows on screen, top to bottom: a collapsed folder hides what's inside it. */
export function visibleRows<F extends TreeFile>(tree: TreeNode<F>[], collapsed: ReadonlySet<string>): TreeNode<F>[] {
  const out: TreeNode<F>[] = []
  const walk = (list: TreeNode<F>[]) => {
    for (const n of list) {
      out.push(n)
      if (n.kind === 'folder' && !collapsed.has(n.path)) walk(n.children)
    }
  }
  walk(tree)
  return out
}

/** Every folder's path (Collapse all). */
export function folderPaths(tree: TreeNode[]): string[] {
  return tree.flatMap((n) => (n.kind === 'folder' ? [n.path, ...folderPaths(n.children)] : []))
}
