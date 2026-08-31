import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

function runsPage(overrides: { limit?: number; offset?: number } = {}) {
  return {
    runs: [
      {
        runId: '8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33',
        jobName: 'invoice-sync',
        status: 'Failed',
        trigger: 'Schedule',
        instanceId: 'worker-1',
        scheduledForUtc: null,
        startedAtUtc: '2026-08-28T00:00:00Z',
        completedAtUtc: '2026-08-28T00:01:00Z',
        duration: '00:01:00',
        error: 'invoice service unreachable',
      },
    ],
    limit: overrides.limit ?? 50,
    offset: overrides.offset ?? 0,
  }
}

async function renderRoute(path: string) {
  installBoot()

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: [`/cadence${path}`] }))

  render(<RouterProvider router={router} />)

  return router
}

describe('/runs search params', () => {
  beforeEach(() => {
    vi.resetModules()
    server.use(
      http.get('/cadence/ui/runs', () => HttpResponse.json(runsPage())),
      http.get('/cadence/ui/instances', () =>
        HttpResponse.json({
          instances: [
            {
              instanceId: 'worker-a',
              machineName: 'host-a',
              processId: 1,
              assemblyVersion: '0.4.0',
              startedAtUtc: '2026-08-28T09:00:00Z',
              lastHeartbeatUtc: '2026-08-28T10:00:00Z',
            },
          ],
          heartbeatTimeout: '00:02:00',
        }),
      ),
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({ status: 'Healthy', checks: [] }),
      ),
    )
  })

  // Fails without the implementation: neither the /runs route nor its validateSearch existed, so
  // there was nothing to parse `?status=Failed` into and no status field to assert against.
  it('round-trips a filter through the URL', async () => {
    const router = await renderRoute('/runs?status=Failed&job=invoice-sync')

    await screen.findByText('invoice-sync')

    expect(screen.getByLabelText(/status/i)).toHaveValue('Failed')

    await userEvent.selectOptions(screen.getByLabelText(/status/i), 'Succeeded')

    expect(router.state.location.search).toMatchObject({ status: 'Succeeded', job: 'invoice-sync' })
  })

  // Fails without the implementation: with no validateSearch, `instance` never reaches the filter
  // inputs and the URL is never restored into visible field values.
  it('restores every filter from the URL on initial load', async () => {
    await renderRoute('/runs?status=Failed&instance=worker-1&job=invoice-sync')

    await screen.findByText('invoice-sync')

    expect(screen.getByLabelText(/status/i)).toHaveValue('Failed')
    expect(screen.getByLabelText(/instance/i)).toHaveValue('worker-1')
    expect(screen.getByLabelText(/job/i)).toHaveValue('invoice-sync')
  })

  // Fails without the implementation: Math.min(...) never runs, so an over-cap limit would be sent
  // to the server (and, before this route existed, there was no search state to clamp at all).
  it("clamps a limit above the server's 500-row cap", async () => {
    const router = await renderRoute('/runs?limit=999999')

    await screen.findByText('invoice-sync')

    expect(router.state.location.search).toMatchObject({ limit: 500 })
  })

  // Fails without the implementation: no default limit/offset were ever assigned by validateSearch.
  it('defaults limit and offset when neither is in the URL', async () => {
    const router = await renderRoute('/runs')

    await screen.findByText('invoice-sync')

    expect(router.state.location.search).toMatchObject({ limit: 50, offset: 0 })
  })

  // Fails without the implementation: there is no error path, so a request that answers with an
  // empty-bodied 400 (design-plan 13.2) would render nothing rather than a message.
  it('does not assume every failed request carries a problem document', async () => {
    server.use(http.get('/cadence/ui/runs', () => new HttpResponse(null, { status: 400 })))

    await renderRoute('/runs?limit=abc')

    expect(await screen.findByText('Could not load runs')).toBeInTheDocument()
  })
})
