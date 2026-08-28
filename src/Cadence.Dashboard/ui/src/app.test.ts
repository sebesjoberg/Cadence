import { createMemoryHistory } from '@tanstack/react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { installBoot } from './test/boot'

async function matchedRouteFor(path: string) {
  const { createAppRouter } = await import('./app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: [path] }))

  await router.load()

  return router.state.matches.at(-1)?.routeId
}

describe('routing', () => {
  beforeEach(() => {
    vi.resetModules()
    installBoot()
  })

  it.each([
    ['/cadence/', '/'],
    ['/cadence/jobs/nightly-close', '/jobs/$name'],
    ['/cadence/runs', '/runs'],
    ['/cadence/runs/8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33', '/runs/$id'],
    ['/cadence/cluster', '/cluster'],
    ['/cadence/tokens', '/tokens'],
  ])('resolves %s under the fixed basepath', async (path, routeId) => {
    await expect(matchedRouteFor(path)).resolves.toBe(routeId)
  })

  it('strips the basepath before matching, so the path params are the job and run ids', async () => {
    const { createAppRouter } = await import('./app')
    const router = createAppRouter(
      createMemoryHistory({ initialEntries: ['/cadence/jobs/nightly-close'] }),
    )

    await router.load()

    expect(router.state.matches.at(-1)?.params).toEqual({ name: 'nightly-close' })
  })
})
