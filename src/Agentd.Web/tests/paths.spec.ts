import { describe, expect, it } from 'vitest'
import { fileName, folder } from '../ClientApps/shared/utils/paths'

describe('paths', () => {
  it('splits a long path into the name and its nearest folders', () => {
    const path = 'src/Sysmin/ERMSys.Sysmin.Web/erm/libs/web/data-sync/feature/dashboard/jest.config.ts'
    expect(fileName(path)).toBe('jest.config.ts')
    expect(folder(path)).toBe('…/data-sync/feature/dashboard')
  })

  it('keeps short folders whole and a root file without one', () => {
    expect(folder('src/app/main.ts')).toBe('src/app')
    expect([fileName('README.md'), folder('README.md')]).toEqual(['README.md', ''])
  })
})
