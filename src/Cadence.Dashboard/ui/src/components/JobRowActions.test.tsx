import { MantineProvider } from '@mantine/core'
import { Notifications } from '@mantine/notifications'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import type { JobSummaryResponse, ScheduleResponse, ScheduleWriteRequest } from '../api/types'
import { installBoot } from '../test/boot'
import { server } from '../test/server'

installBoot()

// The module reads the bootstrap at import time, so it must be installed before this resolves.
const { JobRowActions } = await import('./JobRowActions')

function job(overrides: Partial<JobSummaryResponse> = {}): JobSummaryResponse {
  return {
    name: 'invoice-sync',
    cron: '0 3 * * *',
    timeZone: 'UTC',
    enabled: true,
    allowedTriggers: 'Schedule, Manual',
    nextOccurrenceUtc: '2026-08-29T01:00:00Z',
    lastRun: null,
    ...overrides,
  }
}

const stored: ScheduleResponse = {
  jobName: 'invoice-sync',
  cronExpression: '0 3 * * *',
  timeZoneId: 'UTC',
  enabled: true,
  overlap: null,
  maxDuration: null,
  settings: { region: 'eu' },
  version: 4,
}

function mount(summary: JobSummaryResponse) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <MantineProvider>
      <Notifications />
      <QueryClientProvider client={client}>
        <JobRowActions job={summary} />
      </QueryClientProvider>
    </MantineProvider>,
  )
}

describe('JobRowActions', () => {
  it('pauses a job by sending back the version and settings it loaded', async () => {
    const bodies: ScheduleWriteRequest[] = []

    server.use(
      http.get('/cadence/ui/jobs/invoice-sync/schedule', () => HttpResponse.json(stored)),
      http.put('/cadence/ui/jobs/invoice-sync/schedule', async ({ request }) => {
        const body = (await request.json()) as ScheduleWriteRequest
        bodies.push(body)
        return HttpResponse.json({ ...stored, enabled: body.enabled, version: 5 })
      }),
    )

    mount(job())
    await userEvent.click(screen.getByRole('button', { name: 'Pause' }))

    expect(await screen.findByText(/is paused/i)).toBeInTheDocument()

    // Both fields carry absent-semantics that resolve oppositely on the server, so neither is
    // left to a default: an omitted version is refused, omitted settings are silently preserved.
    expect(bodies[0].enabled).toBe(false)
    expect(bodies[0].version).toBe(4)
    expect(bodies[0].settings).toEqual({ region: 'eu' })
  })

  it('renders the server prose when a pause is refused', async () => {
    server.use(
      http.get('/cadence/ui/jobs/invoice-sync/schedule', () => HttpResponse.json(stored)),
      http.put('/cadence/ui/jobs/invoice-sync/schedule', () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:schedule-conflict',
            title: 'Schedule conflict',
            detail: 'This schedule was changed by someone else. Reload it and reapply the change.',
          },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    mount(job())
    await userEvent.click(screen.getByRole('button', { name: 'Pause' }))

    expect(await screen.findByText(/changed by someone else/i)).toBeInTheDocument()
  })

  it('offers no pause for a trigger-only job, which has no occurrences to stop', () => {
    mount(job({ cron: null, nextOccurrenceUtc: null, allowedTriggers: 'Api, Manual' }))

    expect(screen.getByRole('button', { name: 'Trigger' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /pause|resume/i })).not.toBeInTheDocument()
  })

  it("carries the server's refusal verbatim when a trigger is not accepted", async () => {
    server.use(
      http.post('/cadence/ui/jobs/invoice-sync/trigger', () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:job-not-found',
            title: 'Job not found',
            detail:
              "No job is registered under the name 'invoice-sync'. This replica has 0 registered job(s); a replica that hosts only the dashboard has none.",
          },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    mount(job())
    await userEvent.click(screen.getByRole('button', { name: 'Trigger' }))

    // 13.6's diagnosis for a misconfigured dashboard-only replica reaches the operator whole.
    expect(await screen.findByText(/0 registered job\(s\)/)).toBeInTheDocument()
  })
})
