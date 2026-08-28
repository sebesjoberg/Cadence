import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import type { PauseResponse } from '../api/types'
import { server } from '../test/server'
import { PauseBanner } from './PauseBanner'

const ROUTE = '/cadence/ui/pause'

const RUNNING: PauseResponse = { scope: 'None', reason: null, setBy: null, setAtUtc: null }

function paused(scope: string): PauseResponse {
  return {
    scope,
    reason: 'storage migration',
    setBy: 'alice@example.com',
    setAtUtc: '2026-08-28T09:00:00Z',
  }
}

function renderBanner() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })

  render(
    <QueryClientProvider client={client}>
      <MantineProvider>
        <PauseBanner />
      </MantineProvider>
    </QueryClientProvider>,
  )
}

/** Captures every pause write the component makes, and answers each one 204. */
function captureWrites() {
  const bodies: Record<string, unknown>[] = []

  server.use(
    http.put(ROUTE, async ({ request }) => {
      bodies.push((await request.json()) as Record<string, unknown>)

      return new HttpResponse(null, { status: 204 })
    }),
  )

  return bodies
}

describe('PauseBanner', () => {
  // Fails without the implementation: the component does not exist, so a closed switch is
  // invisible -- and with it the reason, the person who set it, and the fact that the other
  // switch is still open.
  it('names the closed switch, its reason and who set it, and says the other switch is open', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(paused('Schedule'))))

    renderBanner()

    expect(await screen.findByText(/storage migration/)).toBeInTheDocument()
    expect(screen.getByText(/alice@example\.com/)).toBeInTheDocument()
    expect(screen.getByText(/manual triggers still run/i)).toBeInTheDocument()

    expect(screen.getByRole('button', { name: 'Resume scheduling' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Resume triggers' })).not.toBeInTheDocument()
  })

  // Fails without the implementation: there is no write at all. The scope it sends is what proves
  // the two switches were not collapsed into one boolean -- reopening scheduling while triggers
  // stay closed has to ask for Triggers, not None.
  it('reopens one switch without reopening the other', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(paused('All'))))

    const bodies = captureWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Resume scheduling' }))

    await waitFor(() => expect(bodies).toHaveLength(1))
    expect(bodies[0].scope).toBe('Triggers')
  })

  // Fails without the implementation: nothing can close the switches, which is the deliverable
  // PUT /cadence/ui/pause is mounted for. setBy is asserted absent because PauseEndpoints takes
  // it from the authenticated principal -- an audit field a caller can write is one it can forge.
  it('closes a chosen switch with a reason, and never sends setBy', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(RUNNING)))

    const bodies = captureWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Pause…' }))

    await userEvent.selectOptions(await screen.findByLabelText(/what to pause/i), 'Schedule')
    await userEvent.type(await screen.findByLabelText(/reason/i), 'storage migration')
    await userEvent.click(await screen.findByRole('button', { name: 'Pause' }))

    await waitFor(() => expect(bodies).toHaveLength(1))
    expect(bodies[0]).toEqual({ scope: 'Schedule', reason: 'storage migration' })
    expect('setBy' in bodies[0]).toBe(false)
  })

  // Fails without the implementation: a refused write would be silent, and a 403 from the Operate
  // policy is exactly the case an operator needs told about rather than left guessing at.
  it("renders the server's refusal prose", async () => {
    server.use(
      http.get(ROUTE, () => HttpResponse.json(RUNNING)),
      http.put(ROUTE, () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:invalid-pause-scope',
            title: 'Unknown pause scope',
            detail: "'Sideways' is not a pause scope. Use None, Schedule, Triggers or All.",
          },
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Pause…' }))
    await userEvent.type(await screen.findByLabelText(/reason/i), 'storage migration')
    await userEvent.click(await screen.findByRole('button', { name: 'Pause' }))

    expect(
      await screen.findByText(
        "'Sideways' is not a pause scope. Use None, Schedule, Triggers or All.",
      ),
    ).toBeInTheDocument()
  })
})
