import { MantineProvider } from '@mantine/core'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { fileNameFrom } from '../api/client'
import { server } from '../test/server'
import { ResultDownload, formatBytes } from './ResultDownload'

const RUN_ID = '8f1c0a2e-6f9a-4a1e-9d0e-2f6b9d4a1c33'

const RESULT = {
  contentType: 'text/csv; charset=utf-8',
  fileName: 'report.csv',
  length: 2048,
  createdAtUtc: '2026-08-28T00:00:00Z',
  expiresAtUtc: '2026-09-04T00:00:00Z',
}

describe('fileNameFrom', () => {
  it('reads a plain filename', () => {
    expect(fileNameFrom('attachment; filename="report.csv"')).toBe('report.csv')
  })

  // The header the server actually sends for a non-ASCII name. Reading only `filename=` here
  // would hand the browser the mangled ASCII fallback instead of the real name.
  it('prefers the encoded filename over the ASCII fallback', () => {
    expect(
      fileNameFrom("attachment; filename=rapport.csv; filename*=UTF-8''rapport%2C%20%C3%A5r.csv"),
    ).toBe('rapport, år.csv')
  })

  it('falls back to the plain filename when the encoded one is malformed', () => {
    expect(fileNameFrom("attachment; filename=\"ok.csv\"; filename*=UTF-8''%E0%A4%A")).toBe(
      'ok.csv',
    )
  })

  it('is null when the header is absent or carries no filename', () => {
    expect(fileNameFrom(null)).toBeNull()
    expect(fileNameFrom('inline')).toBeNull()
  })
})

describe('formatBytes', () => {
  it.each([
    [0, '0 B'],
    [512, '512 B'],
    [2048, '2.0 kB'],
    [1024 * 1024, '1.0 MB'],
    [45 * 1024 * 1024, '45 MB'],
  ])('renders %i as %s', (bytes, expected) => {
    expect(formatBytes(bytes)).toBe(expected)
  })
})

function renderDownload() {
  render(
    <MantineProvider>
      <ResultDownload runId={RUN_ID} result={RESULT} />
    </MantineProvider>,
  )
}

describe('ResultDownload', () => {
  it('names the file the server suggested, not the one the run detail carried', async () => {
    // The two can differ -- a store may have recorded one name and served another -- and the
    // response header is the authority, because it is what the bytes arrived with.
    server.use(
      http.get(`/cadence/ui/runs/${RUN_ID}/result`, () =>
        HttpResponse.text('customer,rows\n', {
          headers: { 'Content-Disposition': 'attachment; filename="served.csv"' },
        }),
      ),
    )

    const click = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(click)

    const created: string[] = []
    const revoked: string[] = []
    URL.createObjectURL = vi.fn(() => {
      created.push('blob:stub')
      return 'blob:stub'
    })
    URL.revokeObjectURL = vi.fn((url: string) => void revoked.push(url))

    renderDownload()

    await userEvent.click(screen.getByRole('button', { name: /download report\.csv/i }))

    expect(click).toHaveBeenCalledOnce()

    // Revoked, not merely created: an object URL lives as long as the document, so a screen an
    // operator downloads from repeatedly would otherwise hold every blob it ever made.
    expect(revoked).toEqual(created)
  })

  it('shows the server problem when the result has gone', async () => {
    server.use(
      http.get(`/cadence/ui/runs/${RUN_ID}/result`, () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:result-not-found',
            title: 'No result to collect',
            detail: 'its result has passed Retention.ResultMaxAge and been swept',
          },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    renderDownload()

    await userEvent.click(screen.getByRole('button', { name: /download report\.csv/i }))

    expect(
      await screen.findByText(/its result has passed Retention\.ResultMaxAge/),
    ).toBeInTheDocument()
  })
})
