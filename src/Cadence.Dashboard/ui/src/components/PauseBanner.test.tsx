import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import type { PauseResponse } from '../api/types'
import { server } from '../test/server'
import { PauseBanner } from './PauseBanner'

const ROUTE = '/cadence/ui/pause'
const REFUSAL = "'Sideways' is not a pause scope. Use None, Schedule, Triggers or All."

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

function refuseWrites() {
  server.use(
    http.put(ROUTE, () =>
      HttpResponse.json(
        {
          type: 'urn:cadence:problem:invalid-pause-scope',
          title: 'Unknown pause scope',
          detail: REFUSAL,
        },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    ),
  )
}

describe('PauseBanner', () => {
  // Fails without the implementation: a closed switch is invisible, and with it the reason, the
  // person who set it, and the fact that the other switch is still open.
  it('names the closed switch, its reason and who set it, and says the other switch is open', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(paused('Schedule'))))

    renderBanner()

    expect(await screen.findByText(/storage migration/)).toBeInTheDocument()
    expect(screen.getByText(/alice@example\.com/)).toBeInTheDocument()
    expect(screen.getByText(/manual triggers still run/i)).toBeInTheDocument()

    expect(screen.getByRole('button', { name: 'Resume scheduling' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Resume triggers' })).not.toBeInTheDocument()
  })

  // The scope each row sends is what proves the two switches were not collapsed into one boolean:
  // reopening one while the other stays closed has to name the remainder, not None.
  it.each([
    ['All', 'Resume scheduling', 'Triggers'],
    ['All', 'Resume triggers', 'Schedule'],
    ['All', 'Resume everything', 'None'],
    ['Schedule', 'Resume scheduling', 'None'],
    ['Triggers', 'Resume triggers', 'None'],
  ])('reopens %s with %s and asks for %s', async (scope, button, expected) => {
    server.use(http.get(ROUTE, () => HttpResponse.json(paused(scope))))

    const bodies = captureWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: button }))

    await waitFor(() => expect(bodies).toHaveLength(1))
    expect(bodies[0].scope).toBe(expected)
  })

  // setBy is asserted absent because PauseEndpoints takes it from the authenticated principal.
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

  // Scoping the query to the dialog is what distinguishes "rendered somewhere" from "rendered
  // where the operator is looking": the refusal used to render behind the modal's own overlay.
  it('renders a refusal inside the dialog the operator is still in', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(RUNNING)))
    refuseWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Pause…' }))
    await userEvent.type(await screen.findByLabelText(/reason/i), 'storage migration')
    await userEvent.click(await screen.findByRole('button', { name: 'Pause' }))

    const dialog = await screen.findByRole('dialog')

    expect(await within(dialog).findByText(REFUSAL)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Pause' })).toBeInTheDocument()
  })

  it('renders a refused resume on the banner, where no dialog is open', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(paused('All'))))
    refuseWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Resume everything' }))

    expect(await screen.findByText(REFUSAL)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('forgets the chosen scope and the refusal when the dialog is cancelled', async () => {
    server.use(http.get(ROUTE, () => HttpResponse.json(RUNNING)))
    refuseWrites()

    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Pause…' }))
    await userEvent.selectOptions(await screen.findByLabelText(/what to pause/i), 'All')
    await userEvent.type(await screen.findByLabelText(/reason/i), 'storage migration')
    await userEvent.click(await screen.findByRole('button', { name: 'Pause' }))

    await screen.findByText(REFUSAL)
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Pause…' }))

    expect(await screen.findByLabelText(/what to pause/i)).toHaveValue('Schedule')
    expect(screen.getByLabelText(/reason/i)).toHaveValue('')
    expect(screen.queryByText(REFUSAL)).not.toBeInTheDocument()
  })
})
