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

  // Fails against the task 9 stub, which had none of these columns.
  it('lists every column the overview owes an operator', async () => {
    await renderRoute('/')

    expect(await screen.findByText('invoice-sync')).toBeInTheDocument()
    expect(screen.getByText('0 3 * * *')).toBeInTheDocument()
    expect(screen.getByText('Europe/Stockholm')).toBeInTheDocument()
    // By cell, because 'Enabled' is also a column header.
    expect(screen.getByRole('cell', { name: 'Enabled' })).toBeInTheDocument()
    expect(screen.getByRole('cell', { name: 'Failed' })).toBeInTheDocument()

    // On the instant rather than on a locale format this test would otherwise pin by hand.
    const next = new Date('2026-08-29T01:00:00Z').toLocaleString()
    const started = new Date('2026-08-28T01:00:00Z').toLocaleString()

    expect(screen.getByText(next)).toBeInTheDocument()
    expect(screen.getByText(started)).toBeInTheDocument()
  })

  // Fails against the task 9 stub. The route's GET is unmounted alongside the capability, so
  // calling it would 404.
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

    // Only exists once the router matched and the form took that branch, so this cannot pass early.
    expect(await screen.findByText(/schedules are read-only here/i)).toBeInTheDocument()

    expect(screen.getByLabelText(/cron expression/i)).toBeDisabled()
    expect(screen.queryByRole('button', { name: /^save/i })).not.toBeInTheDocument()
    expect(schedules).toBe(0)
  })

  // The same stub from the other side: nothing loaded a schedule, nothing offered to write one.
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
