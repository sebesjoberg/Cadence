import { MantineProvider } from '@mantine/core'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '../test/server'
import { RunFilters } from './RunFilters'
import type { RunFiltersValue } from './RunFilters'

function instances(ids: string[]) {
  return http.get('/cadence/ui/instances', () =>
    HttpResponse.json({
      instances: ids.map((id) => ({
        instanceId: id,
        machineName: `host-${id}`,
        processId: 1,
        assemblyVersion: '0.4.0',
        startedAtUtc: '2026-08-28T09:00:00Z',
        lastHeartbeatUtc: '2026-08-28T10:00:00Z',
      })),
      heartbeatTimeout: '00:02:00',
    }),
  )
}

function mount(value: RunFiltersValue) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <RunFilters value={value} onChange={() => {}} />
      </QueryClientProvider>
    </MantineProvider>,
  )
}

function options(): string[] {
  const select = screen.getByLabelText('Instance') as HTMLSelectElement
  return [...select.options].map((option) => option.value)
}

describe('RunFilters instance dropdown', () => {
  it('offers the instances the cluster reports', async () => {
    server.use(instances(['worker-a', 'worker-b']))
    mount({})

    await waitFor(() => expect(options()).toContain('worker-b'))
    expect(options()).toEqual(['', 'worker-a', 'worker-b'])
  })

  it('keeps an instance the cluster no longer reports, so a shared deep link still resolves', async () => {
    server.use(instances(['worker-a']))
    mount({ instance: 'worker-gone' })

    // The janitor purges a dead instance's row, but its runs remain and still name it. Dropping
    // the id would silently widen someone's link from one instance to all of them.
    await waitFor(() => expect(options()).toContain('worker-a'))
    expect(options()).toContain('worker-gone')
    expect((screen.getByLabelText('Instance') as HTMLSelectElement).value).toBe('worker-gone')
  })

  it('still filters when the instance lookup fails', async () => {
    server.use(http.get('/cadence/ui/instances', () => new HttpResponse(null, { status: 500 })))
    mount({ instance: 'worker-a' })

    await waitFor(() => expect(options()).toContain('worker-a'))
    expect((screen.getByLabelText('Instance') as HTMLSelectElement).value).toBe('worker-a')
  })
})
