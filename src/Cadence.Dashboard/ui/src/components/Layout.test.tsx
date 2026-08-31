import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DashboardBoot } from '../bootstrap'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

async function renderShell(boot?: Partial<DashboardBoot>) {
  installBoot(boot)

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/cadence/'] }))

  render(<RouterProvider router={router} />)
}

describe('shell', () => {
  beforeEach(() => {
    vi.resetModules()
    server.use(
      // The shell is rendered at '/', which is the overview -- so these two are the screen's, not
      // the shell's. Declared here because the suite refuses an unhandled request.
      http.get('/cadence/ui/jobs', () => HttpResponse.json([])),
      http.get('/cadence/ui/pause', () =>
        HttpResponse.json({ scope: 'None', reason: null, setBy: null, setAtUtc: null }),
      ),
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({
          status: 'Degraded',
          checks: [
            {
              name: 'cadence-sql',
              status: 'Degraded',
              description: null,
              error: 'timeout',
              duration: '00:00:05',
            },
          ],
        }),
      ),
    )
  })

  it('names the deployment and reports what storage says', async () => {
    await renderShell({ title: 'Payments scheduler' })

    expect(await screen.findByText('Payments scheduler')).toBeInTheDocument()
    expect(await screen.findByText('Degraded')).toBeInTheDocument()
  })

  it('offers tokens only where the container registered a writable store', async () => {
    await renderShell({ capabilities: { scheduleWrite: true, tokens: true } })

    expect(await screen.findByRole('link', { name: 'Tokens' })).toBeInTheDocument()
  })

  it('hides tokens where it was not registered', async () => {
    await renderShell({ capabilities: { scheduleWrite: true, tokens: false } })

    expect(await screen.findByRole('link', { name: 'Runs' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Tokens' })).not.toBeInTheDocument()
  })
})
