import { ref, watch, type Ref } from 'vue'

/** A resizable pane's width in px, kept per browser (`agentd.pane.<key>`); out of range or unreadable falls back to the default. */
export function usePaneWidth(key: string, initial: number, min: number, max: number): Ref<number> {
  const storageKey = `agentd.pane.${key}`
  const clamp = (n: number) => Math.min(max, Math.max(min, Math.round(n)))
  let saved: number | null = null
  try {
    const raw = window.localStorage.getItem(storageKey)
    saved = raw === null ? null : Number(raw)
  } catch {
    // private window or blocked storage: the default width
  }

  const width = ref(saved !== null && Number.isFinite(saved) ? clamp(saved) : initial)
  watch(width, (w) => {
    const c = clamp(w)
    if (c !== w) {
      width.value = c
      return
    }

    try {
      window.localStorage.setItem(storageKey, String(c))
    } catch {
      // kept until the page closes
    }
  })
  return width
}
