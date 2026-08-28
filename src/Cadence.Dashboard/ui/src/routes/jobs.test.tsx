import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render, screen } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DashboardBoot } from '../bootstrap'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

const JOB = {
  name: 'invoice-sync',
  cron: '0 3 * * *',
  timeZone: 'Europe/Stockholm',
  enabled: true,
  allowedTriggers: 'Schedule, Manual',
  nextOccurrenceUtc: '2026-08-29T01:00:00Z',
  lastRun: {
    runId: '8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33',
    jobName: 'invoice-sync',
    status: 'Failed',
    trigger: 'Schedule',
    instanceId: 'worker-1',
    scheduledForUtc: '2026-08-28T01:00:00Z',
    startedAtUtc: '2026-08-28T01:00:00Z',
    completedAtUtc: '2026-08-28T01:01:00Z',
    duration: '00:01:00',
    error: 'invoice service unreachable',
  },
}

const DETAIL = { job: JOB, overlap: 'Skip', maxDuration: null, settings: {}, recentRuns: [] }

async function renderRoute(path: string, boot?: Partial<DashboardBoot>) {
  installBoot(boot)

  const { createAppRouter } = await import('../app')
  const router = createAppRouter(createMemoryHistory({ initialEntries: [`/cadence${path}`] }))

  render(<RouterProvider router={router} />)
}

describe('the job screens', () => {
  beforeEach(() => {
    vi.resetModules()
    server.use(
      http.get('/cadence/ui/jobs', () => HttpResponse.json([JOB])),
      http.get('/cadence/ui/jobs/invoice-sync', () => HttpResponse.json(DETAIL)),
      http.get('/cadence/ui/pause', () =>
        HttpResponse.json({ scope: 'None', reason: null, setBy: null, setAtUtc: null }),
      ),
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({ status: 'Healthy', checks: [] }),
      ),
    )
  })

  // Fails without the implementation: the overview was a stub reading "Job list and schedule
  // editing arrive with task 10", so none of these columns existed.
  it('lists every column the overview owes an operator', async () => {
    await renderRoute('/')

    expect(await screen.findByText('invoice-sync')).toBeInTheDocument()
    expect(screen.getByText('0 3 * * *')).toBeInTheDocument()
    expect(screen.getByText('Europe/Stockholm')).toBeInTheDocument()
    // By cell rather than by text: 'Enabled' is also a column header, and the assertion is about
    // the row.
    expect(screen.getByRole('cell', { name: 'Enabled' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Failed' })).toBeInTheDocument()

    // Rendered through the browser's locale, so the assertion is on the instant rather than on a
    // format this test would otherwise be pinning by hand.
    const next = new Date('2026-08-29T01:00:00Z').toLocaleString()
    const started = new Date('2026-08-28T01:00:00Z').toLocaleString()

    expect(screen.getByText(next)).toBeInTheDocument()
    expect(screen.getByText(started)).toBeInTheDocument()
  })

  // Fails without the implementation: the detail screen was a stub, so there was no form to be
  // read-only and nothing that could have refrained from calling a route the container never
  // mounted -- scheduleWrite is false exactly when IWritableScheduleSource is absent, and then
  // GET /jobs/{name}/schedule is not mapped either.
  it('reads the declared schedule off the job detail when writing is not a capability', async () => {
    let schedules = 0

    server.use(
      http.get('/cadence/ui/jobs/invoice-sync/schedule', () => {
        schedules += 1

        return HttpResponse.json({
          jobName: 'invoice-sync',
          cronExpression: '0 3 * * *',
          timeZoneId: 'Europe/Stockholm',
          enabled: true,
          overlap: null,
          maxDuration: null,
          settings: {},
          version: 1,
        })
      }),
    )

    await renderRoute('/jobs/invoice-sync', {
      capabilities: { scheduleWrite: false, tokens: false },
    })

    // The read-only note only exists once the route resolved and the form chose that branch, so
    // waiting for it is what keeps this from passing before the router has matched anything.
    expect(await screen.findByText(/schedules are read-only here/i)).toBeInTheDocument()

    expect(screen.getByLabelText(/cron expression/i)).toBeDisabled()
    expect(screen.queryByRole('button', { name: /^save/i })).not.toBeInTheDocument()
    expect(schedules).toBe(0)
  })

  // Fails without the implementation: same stub, from the other side -- nothing loaded a schedule
  // and nothing offered to write one back.
  it('loads the stored schedule where writing is a capability', async () => {
    server.use(
      http.get('/cadence/ui/jobs/invoice-sync/schedule', () =>
        HttpResponse.json({
          jobName: 'invoice-sync',
          cronExpression: '15 4 * * *',
          timeZoneId: 'Europe/Stockholm',
          enabled: true,
          overlap: null,
          maxDuration: null,
          settings: {},
          version: 3,
        }),
      ),
    )

    await renderRoute('/jobs/invoice-sync', {
      capabilities: { scheduleWrite: true, tokens: false },
    })

    expect(await screen.findByDisplayValue('15 4 * * *')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^save/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /trigger/i })).toBeInTheDocument()
  })
})
