import { beforeEach, describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import PaneResizer from '../ClientApps/shared/components/PaneResizer.vue'
import { usePaneWidth } from '../ClientApps/shared/utils/paneWidth'

// The left panes (the guided review's chapter, the All files tree) resize by their edge and keep the width per browser.
const Host = defineComponent({
  setup() {
    const width = usePaneWidth('test', 300, 200, 500)
    return () => h('div', { style: { width: `${width.value}px` }, 'data-width': width.value }, [
      h(PaneResizer, { modelValue: width.value, 'onUpdate:modelValue': (v: number) => (width.value = v), min: 200, max: 500, initial: 300, label: 'Resize' }),
    ])
  },
})

beforeEach(() => window.localStorage.clear())

/** jsdom has no PointerEvent: a mouse event under the pointer event's name carries clientX all the same. */
async function pointer(el: Element, type: string, clientX = 0): Promise<void> {
  el.dispatchEvent(new MouseEvent(type, { clientX, bubbles: true, cancelable: true }))
  await Promise.resolve()
}

describe('pane resizer', () => {
  it('drags to resize within its bounds and keeps the width', async () => {
    const w = mount(Host)
    const handle = w.get('[role=separator]')

    await pointer(handle.element, 'pointerdown', 100)
    await pointer(handle.element, 'pointermove', 160)
    await pointer(handle.element, 'pointerup')
    await w.vm.$nextTick()
    expect(w.get('div').attributes('data-width')).toBe('360')
    expect(window.localStorage.getItem('agentd.pane.test')).toBe('360')

    await pointer(handle.element, 'pointerdown', 0)
    await pointer(handle.element, 'pointermove', 900)
    await w.vm.$nextTick()
    expect(w.get('div').attributes('data-width'), 'never past the max').toBe('500')
    await pointer(handle.element, 'pointerup')
    await pointer(handle.element, 'pointermove', 0)
    await w.vm.$nextTick()
    expect(w.get('div').attributes('data-width'), 'released: moving does nothing').toBe('500')
  })

  it('arrow keys resize, Home and End go to the bounds, double-click gives back the default', async () => {
    const w = mount(Host)
    const handle = w.get('[role=separator]')
    expect(handle.attributes('aria-valuenow')).toBe('300')

    await handle.trigger('keydown', { key: 'ArrowRight' })
    expect(handle.attributes('aria-valuenow')).toBe('316')
    await handle.trigger('keydown', { key: 'ArrowLeft', shiftKey: true })
    expect(handle.attributes('aria-valuenow')).toBe('252')
    await handle.trigger('keydown', { key: 'Home' })
    expect(handle.attributes('aria-valuenow')).toBe('200')
    await handle.trigger('dblclick')
    expect(handle.attributes('aria-valuenow')).toBe('300')
  })

  it('a saved width comes back, and one out of range is brought inside it', () => {
    window.localStorage.setItem('agentd.pane.test', '420')
    expect(mount(Host).get('[role=separator]').attributes('aria-valuenow')).toBe('420')

    window.localStorage.setItem('agentd.pane.test', '9999')
    expect(mount(Host).get('[role=separator]').attributes('aria-valuenow')).toBe('500')
    window.localStorage.setItem('agentd.pane.test', 'wide')
    expect(mount(Host).get('[role=separator]').attributes('aria-valuenow')).toBe('300')
  })
})
