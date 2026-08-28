import { MantineProvider } from '@mantine/core'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { restoreLocation, stubLocation } from '../test/location'
import { server } from '../test/server'
import { CreateTokenModal } from './CreateTokenModal'

// Modal reads theme through Mantine's context, so a real app tree renders it under
// MantineProvider (see routes/__root.tsx) -- the brief's own snippet omits it, but the component
// throws without it.
function renderModal(onClose: () => void = () => {}) {
  render(
    <MantineProvider>
      <CreateTokenModal opened onClose={onClose} />
    </MantineProvider>,
  )
}

describe('CreateTokenModal', () => {
  // Fails without the implementation: the component does not exist, so nothing renders the
  // secret, and there is no "Done" affordance that could ever make it disappear again.
  it('shows the secret once and never again', async () => {
    server.use(
      http.post('/cadence/ui/tokens', () =>
        HttpResponse.json(
          {
            id: 'a',
            name: 'ci',
            fingerprint: 'ab12',
            scope: 'Operate',
            createdAtUtc: '2026-08-28T10:00:00Z',
            expiresAtUtc: null,
            token: 'SECRET-VALUE',
          },
          { status: 201 },
        ),
      ),
    )

    renderModal()
    await userEvent.type(screen.getByLabelText(/name/i), 'ci')
    await userEvent.click(screen.getByRole('button', { name: /create/i }))

    expect(await screen.findByText('SECRET-VALUE')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /done/i }))

    expect(screen.queryByText('SECRET-VALUE')).not.toBeInTheDocument()
  })

  // Fails without the implementation: a real problem document's `detail` was not surfaced, so a
  // caller submitting an invalid name would see nothing that explains the refusal.
  it('shows the server-written detail verbatim on a validation failure', async () => {
    server.use(
      http.post('/cadence/ui/tokens', () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:invalid-token-name',
            title: 'Invalid token name',
            detail: 'the token name must not be empty and may not exceed 200 characters',
          },
          { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderModal()
    await userEvent.type(screen.getByLabelText(/name/i), 'x')
    await userEvent.click(screen.getByRole('button', { name: /create/i }))

    expect(
      await screen.findByText('the token name must not be empty and may not exceed 200 characters'),
    ).toBeInTheDocument()
  })

  describe('the stale-session case', () => {
    beforeEach(() => {
      stubLocation()
    })

    afterEach(() => {
      restoreLocation()
    })

    // Fails without the implementation: a stale-session 401 carries no `detail` at all, and
    // without special-casing UnauthenticatedError it would render as a generic or misleading
    // permissions error rather than naming the re-authentication the server actually asked for.
    it('names the stale-session case instead of rendering a permissions error', async () => {
      server.use(
        http.post(
          '/cadence/ui/tokens',
          () =>
            new HttpResponse(null, {
              status: 401,
              headers: { 'WWW-Authenticate': 'CadenceCookie' },
            }),
        ),
      )

      renderModal()
      await userEvent.type(screen.getByLabelText(/name/i), 'ci')
      await userEvent.click(screen.getByRole('button', { name: /create/i }))

      expect(await screen.findByText(/too old to authorise this/i)).toBeInTheDocument()
    })
  })
})
