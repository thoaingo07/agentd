import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ESLint } from 'eslint'
import {
  AgButton,
  AgCollapsible,
  AgMeter,
  AgModal,
  AgStateBadge,
  AgTabs,
  AgToast,
  AgToggleGroup,
} from '../ClientApps/shared/components/ui'
import { jobStates } from '../ClientApps/shared/api/types'

afterEach(() => {
  document.body.innerHTML = ''
  vi.useRealTimers()
})

describe('AgButton', () => {
  it('renders its slot with the variant and merges the class prop last', () => {
    const w = mount(AgButton, { props: { variant: 'outline', size: 'sm' }, attrs: { class: 'w-full' }, slots: { default: 'Retry' } })
    const button = w.get('button')
    expect(button.text()).toBe('Retry')
    expect(button.classes()).toEqual(expect.arrayContaining(['btn', 'btn-outline', 'btn-sm', 'w-full']))
  })

  it('stays focusable but inert while loading', async () => {
    const onClick = vi.fn()
    const w = mount(AgButton, { props: { loading: true }, attrs: { onClick }, slots: { default: 'Save' } })
    const button = w.get('button')
    expect(button.attributes('aria-busy')).toBe('true')
    expect(button.attributes('disabled')).toBeUndefined()
    expect(button.attributes('aria-disabled')).toBe('true')
    expect(w.find('.loading-spinner').exists()).toBe(true)
    await button.trigger('click')
    expect(onClick).not.toHaveBeenCalled()
  })
})

describe('AgCollapsible', () => {
  it('toggles from a native button trigger (Enter and Space come with the button)', async () => {
    const w = mount(AgCollapsible, { props: { title: 'tool: Bash' }, slots: { default: 'dotnet test' }, attachTo: document.body })
    const trigger = w.get('button')
    expect(trigger.attributes('aria-expanded')).toBe('false')
    await trigger.trigger('click')
    expect(trigger.attributes('aria-expanded')).toBe('true')
    expect(trigger.attributes()).toHaveProperty('data-panel-open')
    expect(w.text()).toContain('dotnet test')
  })
})

describe('AgTabs', () => {
  const tabs = [
    { value: 'transcript', label: 'Transcript', keepMounted: true },
    { value: 'diff', label: 'Diff' },
  ]

  it('marks the active tab and moves focus with the arrow keys', async () => {
    const w = mount(AgTabs, {
      props: { tabs, modelValue: 'transcript' },
      slots: { transcript: 'events…', diff: 'patch…' },
      attachTo: document.body,
    })
    await flushPromises()   // the tab list registers its items after mount
    const [first, second] = w.findAll('[role="tab"]')
    expect(first!.attributes()).toHaveProperty('data-active')
    ;(first!.element as HTMLElement).focus()
    await first!.trigger('keydown', { key: 'ArrowRight' })
    await flushPromises()
    expect(document.activeElement).toBe(second!.element)
  })

  it('updates v-model when a tab is chosen', async () => {
    const w = mount(AgTabs, { props: { tabs, modelValue: 'transcript' }, attachTo: document.body })
    await w.findAll('[role="tab"]')[1]!.trigger('click')
    expect(w.emitted('update:modelValue')?.at(-1)).toEqual(['diff'])
  })
})

describe('AgToggleGroup', () => {
  it('emits the pressed values', async () => {
    const w = mount(AgToggleGroup, {
      props: { label: 'Event types', options: [{ value: 'tools', label: 'Tools' }, { value: 'text', label: 'Text' }], modelValue: ['text'] },
    })
    const [tools, text] = w.findAll('button')
    expect(text!.attributes()).toHaveProperty('data-pressed')
    await tools!.trigger('click')
    expect(w.emitted('update:modelValue')?.at(-1)?.[0]).toEqual(expect.arrayContaining(['text', 'tools']))
  })
})

describe('AgMeter', () => {
  it('exposes the value accessibly', () => {
    const w = mount(AgMeter, { props: { label: 'Slots', value: 2, max: 4 } })
    const meter = w.get('[role="meter"]')
    expect(meter.attributes('aria-valuenow')).toBe('2')
    expect(meter.attributes('aria-valuemax')).toBe('4')
    expect(w.text()).toContain('Slots')
  })
})

describe('AgModal', () => {
  it('opens with showModal and closes on Esc (cancel)', async () => {
    const showModal = vi.fn(function (this: HTMLDialogElement) {
      this.setAttribute('open', '')
    })
    HTMLDialogElement.prototype.showModal = showModal
    HTMLDialogElement.prototype.close = vi.fn(function (this: HTMLDialogElement) {
      this.removeAttribute('open')
    })
    const w = mount(AgModal, { props: { title: 'Cancel job?', open: false }, slots: { default: 'The agent stops.' }, attachTo: document.body })
    await w.setProps({ open: true })
    expect(showModal).toHaveBeenCalled()

    await w.get('dialog').trigger('cancel')
    expect(w.emitted('update:open')?.at(-1)).toEqual([false])
  })
})

describe('AgToast', () => {
  it('dismisses itself after the timeout', () => {
    vi.useFakeTimers()
    const w = mount(AgToast, { props: { toast: { id: 3, kind: 'error', message: 'Cancel failed' } } })
    expect(w.get('[role="alert"]').text()).toContain('Cancel failed')
    vi.advanceTimersByTime(5000)
    expect(w.emitted('dismiss')).toEqual([[3]])
  })
})

describe('AgStateBadge', () => {
  it.each(jobStates)('%s has a label and an icon, never color alone', (state) => {
    const w = mount(AgStateBadge, { props: { state } })
    expect(w.text().trim().length).toBeGreaterThan(0)
    expect(w.find('svg').exists() || w.find('.animate-pulse').exists()).toBe(true)
  })
})

describe('lint', () => {
  it('blocks base-ui-vue outside shared/components/ui', async () => {
    const eslint = new ESLint({ cwd: process.cwd() })
    const code = "import { Button } from 'base-ui-vue'\nexport const b = Button\n"
    const [view] = await eslint.lintText(code, { filePath: 'ClientApps/dashboard/views/Bad.ts' })
    const [ui] = await eslint.lintText(code, { filePath: 'ClientApps/shared/components/ui/Good.ts' })
    expect(view!.messages.map((m) => m.ruleId)).toContain('no-restricted-imports')
    expect(ui!.messages.map((m) => m.ruleId)).not.toContain('no-restricted-imports')
  })
})
