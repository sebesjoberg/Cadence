import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DashboardBoot } from '../bootstrap'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

async function renderTokensPath(boot?: Partial<DashboardBoot>) {
  installBoot(boot)

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/cadence/tokens'] }))

  render(<RouterProvider router={router} />)
}

describe('route tree', () => {
  beforeEach(() => {
    vi.resetModules()
    server.use(
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({ status: 'Healthy', checks: [] }),
      ),
      http.get('/cadence/ui/tokens', () => HttpResponse.json([])),
    )
  })

  it('mounts /tokens when the container registered a writable token store', async () => {
    await renderTokensPath({ capabilities: { scheduleWrite: true, tokens: true } })

    expect(await screen.findByRole('heading', { name: 'Tokens' })).toBeInTheDocument()
  })

  // Fails without the implementation: routes/index.ts added the /tokens child unconditionally,
  // so the route -- not just the nav link -- rendered the Tokens screen regardless of capability.
  it('does not mount /tokens when the capability is off', async () => {
    await renderTokensPath({ capabilities: { scheduleWrite: true, tokens: false } })

    // Router matching is async, so a bare queryByRole right after render would pass trivially
    // whether or not the route was ever removed. Waiting for the router's own not-found fallback
    // is what proves the tree settled with no match for the path.
    expect(await screen.findByText('Not Found')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Tokens' })).not.toBeInTheDocument()
  })
})
