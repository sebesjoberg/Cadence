import { Alert, Button, Group, Stack, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { ProblemError } from '../api/problem'
import type { RunPageResponse } from '../api/types'
import { RunFilters } from '../components/RunFilters'
import { RunsTable } from '../components/RunsTable'
import type { RunsSearch } from '../routes/runs'
import { runsRoute } from '../routes/runs'

function toQuery(search: RunsSearch): string {
  const params = new URLSearchParams()

  if (search.job) params.set('job', search.job)
  if (search.status) params.set('status', search.status)
  if (search.instance) params.set('instance', search.instance)
  if (search.from) params.set('from', search.from)
  if (search.to) params.set('to', search.to)
  params.set('limit', String(search.limit))
  params.set('offset', String(search.offset))

  return params.toString()
}

function runsErrorMessage(error: ProblemError): string {
  // Same fallback shape as the run-detail screen: a real problem document's prose is verbatim, its
  // absence (design-plan 13.2 -- an unparseable status/from/to/limit/offset answers an empty-bodied
  // 400) is a generic line, not the framework's bare statusText.
  if (error.detail) return error.detail
  if (error.type) return error.title
  return 'Failed to load runs.'
}

export function Runs() {
  const search = runsRoute.useSearch()
  const navigate = runsRoute.useNavigate()

  const { data, error, isPending } = useQuery<RunPageResponse, ProblemError>({
    queryKey: ['runs', search],
    queryFn: () => api.get<RunPageResponse>(`/runs?${toQuery(search)}`),
    // A bad filter is not a transient fault: retrying the same query params only delays the
    // message. Transient (5xx) failures still get the default client's one retry.
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
  })

  // Any filter change starts back at the first page -- an old offset over a narrower result set
  // could otherwise land past the end.
  const setFilters = (patch: Partial<Omit<RunsSearch, 'limit' | 'offset'>>) =>
    navigate({ search: (prev) => ({ ...prev, ...patch, offset: 0 }) })

  const page = (delta: number) =>
    navigate({ search: (prev) => ({ ...prev, offset: Math.max(0, prev.offset + delta) }) })

  return (
    <Stack gap="md">
      <Title order={3}>Runs</Title>

      <RunFilters value={search} onChange={setFilters} />

      {error && (
        <Alert color="red" title="Could not load runs">
          {runsErrorMessage(error)}
        </Alert>
      )}

      <RunsTable
        runs={data?.runs ?? []}
        onSelectRun={(runId) => navigate({ to: '/runs/$id', params: { id: runId } })}
      />

      <Group justify="space-between">
        <Text c="dimmed" size="sm">
          {isPending ? 'Loading…' : `${data?.runs.length ?? 0} run(s) from offset ${search.offset}`}
        </Text>
        <Group>
          <Button
            variant="default"
            disabled={search.offset === 0}
            onClick={() => page(-search.limit)}
          >
            Previous
          </Button>
          <Button
            variant="default"
            disabled={(data?.runs.length ?? 0) < search.limit}
            onClick={() => page(search.limit)}
          >
            Next
          </Button>
        </Group>
      </Group>
    </Stack>
  )
}
