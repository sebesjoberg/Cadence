import { MantineProvider } from '@mantine/core'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '../test/server'
import { TriggerButton } from './TriggerButton'

const ROUTE = '/cadence/ui/jobs/invoice-sync/trigger'

function renderButton() {
  render(
    <MantineProvider>
      <TriggerButton jobName="invoice-sync" />
    </MantineProvider>,
  )
}

async function press() {
  await userEvent.click(screen.getByRole('button', { name: /trigger/i }))
}

function problem(status: number, type: string, title: string, detail: string) {
  return HttpResponse.json(
    { type, title, detail },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

describe('TriggerButton', () => {
  // Fails without the implementation: the component does not exist, so nothing posts the trigger
  // and the run id the 202 carries is never shown.
  it('shows the run id the server accepted', async () => {
    server.use(
      http.post(ROUTE, () =>
        HttpResponse.json(
          {
            runId: '8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33',
            jobName: 'invoice-sync',
            instanceId: 'worker-1',
          },
          { status: 202 },
        ),
      ),
    )

    renderButton()
    await press()

    expect(await screen.findByText(/8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33/)).toBeInTheDocument()
  })

  // Fails without the implementation: nothing renders the detail, and a generic "failed to
  // trigger" would destroy the only signal an operator gets that they are looking at a replica
  // which serves the dashboard and registers no jobs (design plan 13.6).
  it("renders the server's registered-job count verbatim", async () => {
    server.use(
      http.post(ROUTE, () =>
        problem(
          404,
          'urn:cadence:problem:job-not-found',
          'Job not found',
          "No job is registered under the name 'invoice-sync'. This replica has 0 registered job(s); a replica that hosts only the dashboard has none.",
        ),
      ),
    )

    renderButton()
    await press()

    expect(
      await screen.findByText(
        "No job is registered under the name 'invoice-sync'. This replica has 0 registered job(s); a replica that hosts only the dashboard has none.",
      ),
    ).toBeInTheDocument()
  })

  // Fails without the implementation: same reason, on the refusal an operator meets while the
  // trigger switch is closed -- the prose says which switch, and a generic message would not.
  it('renders a paused refusal verbatim rather than a generic failure', async () => {
    server.use(
      http.post(ROUTE, () =>
        problem(
          409,
          'urn:cadence:problem:scheduler-paused',
          'Triggers are paused',
          "Triggers are paused cluster-wide: 'storage migration'.",
        ),
      ),
    )

    renderButton()
    await press()

    expect(
      await screen.findByText("Triggers are paused cluster-wide: 'storage migration'."),
    ).toBeInTheDocument()
    expect(screen.queryByText(/failed to trigger/i)).not.toBeInTheDocument()
  })
})
