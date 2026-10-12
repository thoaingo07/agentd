// File lists show the name first, then the nearest folders: long paths in one repo share the same prefix.
export const fileName = (path: string): string => path.slice(path.lastIndexOf('/') + 1)

export function folder(path: string, depth = 3): string {
  const parts = path.split('/').slice(0, -1)
  return parts.length > depth ? `…/${parts.slice(-depth).join('/')}` : parts.join('/')
}
