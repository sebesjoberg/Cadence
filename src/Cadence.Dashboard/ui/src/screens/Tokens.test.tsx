import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { server } from '../test/server'
import { Tokens } from './Tokens'

const TOKEN = {
  id: 't1',
  name: 'ci',
  fingerprint: 'ab12',
  scope: 'Operate',
  createdAtUtc: '2026-08-28T10:00:00Z',
  createdBy: null,
  expiresAtUtc: null,
}

function renderTokens() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <MantineProvider>
        <Tokens />
      </MantineProvider>
    </QueryClientProvider>,
  )
}

describe('Tokens', () => {
  beforeEach(() => {
    server.use(http.get('/cadence/ui/tokens', () => HttpResponse.json([TOKEN])))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  // Fails without the fix: the revoke mutation had no onError and revoke.error was never read in
  // the JSX, so a failed revoke left the operator with nothing -- not even the server's own
  // explanation -- believing a credential was gone when it was not.
  it("shows the server's detail verbatim when a revoke fails", async () => {
    server.use(
      http.delete('/cadence/ui/tokens/t1', () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:token-not-found',
            title: 'Token not found',
            detail: "no token with id 't1' exists in this store",
          },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderTokens()

    await screen.findByText('ci')
    await userEvent.click(screen.getByRole('button', { name: /revoke/i }))

    expect(
      await screen.findByText("no token with id 't1' exists in this store"),
    ).toBeInTheDocument()
  })
})
