import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { installBoot } from './test/boot'

describe('bootstrap', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  afterEach(() => {
    delete window.__cadence
  })

  it('throws at module load when the shell wrote no bootstrap', async () => {
    delete window.__cadence

    await expect(import('./bootstrap')).rejects.toThrow(/__cadence/)
  })

  it('exposes the title and capabilities the shell wrote', async () => {
    installBoot({
      title: 'Payments scheduler',
      capabilities: { scheduleWrite: false, tokens: true },
    })

    const { bootstrap } = await import('./bootstrap')

    expect(bootstrap.title).toBe('Payments scheduler')
    expect(bootstrap.capabilities).toEqual({ scheduleWrite: false, tokens: true })
  })
})
