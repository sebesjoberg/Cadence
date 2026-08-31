import { Alert, Badge, Group, Stack, Text, Title } from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import type { ProblemError } from '../api/problem'
import type { JobDetailResponse } from '../api/types'
import { bootstrap } from '../bootstrap'
import { Crumbs } from '../components/Crumbs'
import { MachineTrigger } from '../components/MachineTrigger'
import { RunsTable } from '../components/RunsTable'
import { ScheduleForm } from '../components/ScheduleForm'
import { TriggerButton } from '../components/TriggerButton'
import { jobDetailRoute } from '../routes/jobs'

function formatInstant(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

export function JobDetail() {
  const { name } = jobDetailRoute.useParams()
  const navigate = jobDetailRoute.useNavigate()
  const queryClient = useQueryClient()

  const { data, error, isPending } = useQuery<JobDetailResponse, ProblemError>({
    queryKey: ['jobs', name],
    queryFn: () => api.get<JobDetailResponse>(`/jobs/${encodeURIComponent(name)}`),
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
  })

  if (error) {
    return (
      <Stack gap="xs">
        <Crumbs parent={{ label: 'Jobs', to: '/' }} current={name} />
        <Title order={3}>{name}</Title>
        <Alert color="red" title="Could not load this job">
          {problemMessage(
            error,
            error.status === 404 ? 'This job is not registered here.' : 'Could not load this job.',
          )}
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

  const { job } = data

  return (
    <Stack gap="md">
      <Crumbs parent={{ label: 'Jobs', to: '/' }} current={job.name} />
      <Group justify="space-between" align="flex-start">
        <Stack gap={4}>
          <Title order={3}>{job.name}</Title>
          <Group gap="xs">
            <Badge color={job.enabled ? 'green' : 'gray'}>
              {job.enabled ? 'Enabled' : 'Disabled'}
            </Badge>
            <Text size="sm">Next occurrence: {formatInstant(job.nextOccurrenceUtc)}</Text>
            <Text size="sm" c="dimmed">
              Accepts: {job.allowedTriggers}
            </Text>
          </Group>
        </Stack>

        <TriggerButton
          jobName={name}
          // The run just started is not in the detail this screen is holding.
          onTriggered={() => queryClient.invalidateQueries({ queryKey: ['jobs'] })}
        />
      </Group>

      <MachineTrigger jobName={job.name} />

      <Title order={5}>Schedule</Title>
      <ScheduleForm jobName={name} detail={data} readOnly={!bootstrap.capabilities.scheduleWrite} />

      <Title order={5}>Recent runs</Title>
      <RunsTable
        runs={data.recentRuns}
        onSelectRun={(runId) => navigate({ to: '/runs/$id', params: { id: runId } })}
      />
    </Stack>
  )
}
