import { Alert, Group, Stack, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { ProblemError } from '../api/problem'
import type { RunDetailResponse } from '../api/types'
import { Crumbs } from '../components/Crumbs'
import { RunLog } from '../components/RunLog'
import { runDetailRoute } from '../routes/runs'

function runErrorMessage(error: ProblemError): string {
  // A real problem document (ProblemMapper always stamps a `type` URN) carries its own prose
  // verbatim to the operator. Its absence -- the empty-bodied 400/404 design-plan 13.2 describes,
  // such as a malformed run id that never reaches ProblemMapper -- falls back to a generic line
  // rather than the framework's bare statusText, which client.ts substitutes for a missing title.
  if (error.detail) return error.detail
  if (error.type) return error.title
  return error.status === 404 ? 'This run does not exist.' : 'Could not load this run.'
}

export function RunDetail() {
  const { id } = runDetailRoute.useParams()

  const { data, error, isPending } = useQuery<RunDetailResponse, ProblemError>({
    queryKey: ['runs', id],
    queryFn: () => api.get<RunDetailResponse>(`/runs/${id}`),
    // A bad or missing id is not a transient fault: retrying the same lookup only delays the
    // message. Transient (5xx) failures still get the default client's one retry.
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
  })

  if (error) {
    return (
      <Stack gap="xs">
        <Crumbs parent={{ label: 'Runs', to: '/runs' }} current={id} />
        <Title order={3}>Run</Title>
        <Alert color="red" title="Could not load this run">
          {runErrorMessage(error)}
        </Alert>
      </Stack>
    )
  }

  if (isPending || !data) {
    return (
      <Text c="dimmed" size="sm">
        Loading…
      </Text>
    )
  }

  const { run, log } = data

  return (
    <Stack gap="md">
      <Crumbs parent={{ label: 'Runs', to: '/runs' }} current={`${run.jobName} · ${id}`} />
      <Title order={3}>{run.jobName}</Title>

      <Group gap="xl">
        <Text size="sm">Status: {run.status}</Text>
        <Text size="sm">Trigger: {run.trigger}</Text>
        <Text size="sm">Instance: {run.instanceId}</Text>
        <Text size="sm">Started: {new Date(run.startedAtUtc).toLocaleString()}</Text>
        {run.completedAtUtc && (
          <Text size="sm">Completed: {new Date(run.completedAtUtc).toLocaleString()}</Text>
        )}
      </Group>

      {run.error && (
        <Alert color="red" title="Run error">
          {run.error}
        </Alert>
      )}

      <Title order={5}>Progress</Title>
      <RunLog entries={log} />
    </Stack>
  )
}
