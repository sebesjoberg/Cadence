import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

const RUN_ID = '8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33'

async function renderRunDetail(id: string) {
  installBoot()

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: [`/cadence/runs/${id}`] }))

  render(<RouterProvider router={router} />)

  return router
}

describe('/runs/$id', () => {
  beforeEach(() => {
    vi.resetModules()
    server.use(
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({ status: 'Healthy', checks: [] }),
      ),
    )
  })

  // Fails without the implementation: there was no run-detail screen, so nothing rendered the log
  // and nothing preserved the entries' order.
  it('renders progress entries in order and the run error verbatim', async () => {
    server.use(
      http.get(`/cadence/ui/runs/${RUN_ID}`, () =>
        HttpResponse.json({
          run: {
            runId: RUN_ID,
            jobName: 'invoice-sync',
            status: 'Failed',
            trigger: 'Schedule',
            instanceId: 'worker-1',
            scheduledForUtc: null,
            startedAtUtc: '2026-08-28T00:00:00Z',
            completedAtUtc: '2026-08-28T00:01:00Z',
            duration: '00:01:00',
            error: 'invoice service unreachable after 3 retries',
          },
          log: [
            { timestampUtc: '2026-08-28T00:00:01Z', message: 'first' },
            { timestampUtc: '2026-08-28T00:00:02Z', message: 'second' },
            { timestampUtc: '2026-08-28T00:00:03Z', message: 'third' },
          ],
        }),
      ),
    )

    await renderRunDetail(RUN_ID)

    const entries = await screen.findAllByTestId('run-log-entry')

    expect(entries.map((entry) => entry.textContent)).toEqual([
      expect.stringContaining('first'),
      expect.stringContaining('second'),
      expect.stringContaining('third'),
    ])

    expect(
      await screen.findByText('invoice service unreachable after 3 retries'),
    ).toBeInTheDocument()
  })

  // Fails without the implementation: no error branch existed, so the server's own diagnosis was
  // never shown -- or would have been flattened into a generic failure message.
  it('shows the problem document detail verbatim on a failed request', async () => {
    server.use(
      http.get(`/cadence/ui/runs/${RUN_ID}`, () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:run-not-found',
            title: 'Run not found',
            detail: `no run with id '${RUN_ID}' exists in this store`,
          },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    await renderRunDetail(RUN_ID)

    expect(
      await screen.findByText(`no run with id '${RUN_ID}' exists in this store`),
    ).toBeInTheDocument()
  })

  // Fails without the implementation: same missing error branch, and this exercises the
  // design-plan §13.2 boundary -- an empty-bodied 400/404 has no `detail` to fall back on.
  it('falls back to a generic message when the failure carries no problem document', async () => {
    server.use(
      http.get(`/cadence/ui/runs/${RUN_ID}`, () => new HttpResponse(null, { status: 404 })),
    )

    await renderRunDetail(RUN_ID)

    expect(await screen.findByText('This run does not exist.')).toBeInTheDocument()
  })
})
