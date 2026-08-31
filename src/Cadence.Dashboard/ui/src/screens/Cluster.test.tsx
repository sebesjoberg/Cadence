import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

async function renderCluster() {
  installBoot()

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/cadence/cluster'] }))

  render(<RouterProvider router={router} />)
}

describe('/cluster', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  // Fails without the implementation: InstancesTable did not exist, so a heartbeat older than the
  // response's own heartbeatTimeout was never compared against anything, and nothing marked --
  // let alone kept listing -- the dead instance. StorageHealth did not exist either, so a check's
  // description and error message had nowhere to render.
  it("marks a stale instance by the response's own heartbeat timeout and still lists it, and renders storage check detail", async () => {
    server.use(
      http.get('/cadence/ui/instances', () =>
        HttpResponse.json({
          instances: [
            {
              instanceId: 'worker-1',
              machineName: 'host-a',
              processId: 111,
              assemblyVersion: '0.4.0',
              startedAtUtc: '2026-08-01T00:00:00Z',
              lastHeartbeatUtc: '2000-01-01T00:00:00Z',
            },
            {
              instanceId: 'worker-2',
              machineName: 'host-b',
              processId: 222,
              assemblyVersion: '0.4.0',
              startedAtUtc: '2026-08-01T00:00:00Z',
              lastHeartbeatUtc: new Date().toISOString(),
            },
          ],
          heartbeatTimeout: '00:00:30',
        }),
      ),
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({
          status: 'Degraded',
          checks: [
            {
              name: 'cadence-sql',
              status: 'Degraded',
              description: 'primary store',
              error: 'timeout after 5s',
              duration: '00:00:05',
            },
          ],
        }),
      ),
    )

    await renderCluster()

    await screen.findByText('worker-1')
    expect(screen.getByText('worker-2')).toBeInTheDocument()

    expect(screen.getByText('Stale')).toBeInTheDocument()
    expect(screen.getByText('Live')).toBeInTheDocument()

    expect(screen.getByText('cadence-sql')).toBeInTheDocument()
    expect(screen.getByText('primary store')).toBeInTheDocument()
    expect(screen.getByText('timeout after 5s')).toBeInTheDocument()
  })
})
