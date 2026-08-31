import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import type { JobDetailResponse, ScheduleResponse, ScheduleWriteRequest } from '../api/types'
import { server } from '../test/server'
import { ScheduleForm } from './ScheduleForm'

const ROUTE = '/cadence/ui/jobs/invoice-sync/schedule'

function schedule(overrides: Partial<ScheduleResponse> = {}): ScheduleResponse {
  return {
    jobName: 'invoice-sync',
    cronExpression: '0 3 * * *',
    timeZoneId: 'Europe/Stockholm',
    enabled: true,
    overlap: null,
    maxDuration: null,
    settings: { region: 'eu' },
    version: 7,
    ...overrides,
  }
}

const detail: JobDetailResponse = {
  job: {
    name: 'invoice-sync',
    cron: '0 3 * * *',
    timeZone: 'Europe/Stockholm',
    enabled: true,
    allowedTriggers: 'Schedule, Manual',
    nextOccurrenceUtc: '2026-08-29T01:00:00Z',
    lastRun: null,
  },
  overlap: 'Skip',
  maxDuration: null,
  settings: { region: 'eu' },
  recentRuns: [],
}

function renderForm(readOnly = false) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={client}>
      <MantineProvider>
        <ScheduleForm jobName="invoice-sync" detail={detail} readOnly={readOnly} />
      </MantineProvider>
    </QueryClientProvider>,
  )
}

function cronBox() {
  return screen.getByLabelText(/cron expression/i)
}

async function retype(text: string) {
  await userEvent.clear(cronBox())
  await userEvent.type(cronBox(), text)
}

describe('ScheduleForm', () => {
  // Fails without the implementation: nothing loads a version, so nothing can send one back.
  it('sends back the version and the settings it loaded, and adopts the version it is answered with', async () => {
    const bodies: ScheduleWriteRequest[] = []
    let version = 7

    server.use(
      http.get(ROUTE, () => HttpResponse.json(schedule({ version }))),
      http.put(ROUTE, async ({ request }) => {
        const body = (await request.json()) as ScheduleWriteRequest
        bodies.push(body)
        version += 1

        return HttpResponse.json(schedule({ version, cronExpression: body.cronExpression }))
      }),
    )

    renderForm()
    await waitFor(() => expect(cronBox()).toHaveValue('0 3 * * *'))

    await retype('0 4 * * *')
    await userEvent.click(screen.getByRole('button', { name: /^save/i }))
    await screen.findByText(/schedule saved/i)

    expect(bodies).toHaveLength(1)
    expect(bodies[0].version).toBe(7)
    // Explicit because the two absences resolve oppositely: 409 for one, silent preserve for the other.
    expect(bodies[0].settings).toEqual({ region: 'eu' })
    expect(bodies[0].cronExpression).toBe('0 4 * * *')

    // The round trip: the second save carries the version the first was answered with.
    await retype('0 5 * * *')
    await userEvent.click(screen.getByRole('button', { name: /^save/i }))

    await waitFor(() => expect(bodies).toHaveLength(2))
    expect(bodies[1].version).toBe(8)
  })

  // Fails without the implementation: nothing reloads, so every retry would 409 on a spent version.
  it('renders the conflict prose and reloads the schedule underneath it', async () => {
    let gets = 0

    server.use(
      http.get(ROUTE, () => {
        gets += 1

        return HttpResponse.json(
          gets === 1 ? schedule() : schedule({ version: 9, cronExpression: '30 5 * * *' }),
        )
      }),
      http.put(ROUTE, () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:schedule-conflict',
            title: 'Schedule was modified',
            detail:
              "The schedule for 'invoice-sync' moved since the editor loaded it. Reload it and reapply the change.",
          },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderForm()
    await waitFor(() => expect(cronBox()).toHaveValue('0 3 * * *'))

    await retype('0 4 * * *')
    await userEvent.click(screen.getByRole('button', { name: /^save/i }))

    expect(await screen.findByText(/moved since the editor loaded it/i)).toBeInTheDocument()

    await waitFor(() => expect(cronBox()).toHaveValue('30 5 * * *'))
    expect(gets).toBe(2)
  })

  // Fails without the implementation: the field the 400 names never reaches the operator.
  it("shows the server's prose for an invalid cron expression", async () => {
    server.use(
      http.get(ROUTE, () => HttpResponse.json(schedule())),
      http.put(ROUTE, () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:invalid-cron',
            title: 'Invalid cron expression',
            detail:
              "cronExpression: 'every tuesday' is not a cron expression. It needs 5 fields, or 6 to include seconds.",
          },
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderForm()
    await waitFor(() => expect(cronBox()).toHaveValue('0 3 * * *'))

    await retype('every tuesday')
    await userEvent.click(screen.getByRole('button', { name: /^save/i }))

    expect(
      await screen.findByText(
        "cronExpression: 'every tuesday' is not a cron expression. It needs 5 fields, or 6 to include seconds.",
      ),
    ).toBeInTheDocument()
  })

  // scheduleWrite is false exactly when IWritableScheduleSource is absent, and then the schedule
  // route's GET is not mapped either -- so the form must not call it.
  it('renders the declared schedule without loading one when writing is not a capability', async () => {
    let gets = 0

    server.use(
      http.get(ROUTE, () => {
        gets += 1

        return HttpResponse.json(schedule())
      }),
    )

    renderForm(true)

    expect(await screen.findByDisplayValue('0 3 * * *')).toBeInTheDocument()
    expect(cronBox()).toBeDisabled()
    expect(screen.queryByRole('button', { name: /^save/i })).not.toBeInTheDocument()
    expect(gets).toBe(0)
  })
})
