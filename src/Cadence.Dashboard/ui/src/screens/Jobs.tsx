import { Alert, Paper, Stack, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import type { ProblemError } from '../api/problem'
import type { JobSummaryResponse } from '../api/types'
import { JobsTable } from '../components/JobsTable'
import { PauseBanner } from '../components/PauseBanner'
import { jobsRoute } from '../routes/jobs'

export function Jobs() {
  const navigate = jobsRoute.useNavigate()

  const { data, error, isPending } = useQuery<JobSummaryResponse[], ProblemError>({
    queryKey: ['jobs'],
    queryFn: () => api.get<JobSummaryResponse[]>('/jobs'),
    // A refused read is not a transient fault; 5xx still gets the client's one retry.
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
  })

  return (
    <Stack gap="md">
      <Title order={3}>Jobs</Title>

      <PauseBanner />

      {error && (
        <Alert color="red" title="Could not load jobs">
          {problemMessage(error, 'Could not load jobs.')}
        </Alert>
      )}

      {isPending || !data ? (
        <Text c="dimmed" size="sm">
          Loading…
        </Text>
      ) : (
        <Paper withBorder radius="md">
          <JobsTable
            jobs={data}
            onSelectJob={(name) => navigate({ to: '/jobs/$name', params: { name } })}
          />
        </Paper>
      )}
    </Stack>
  )
}
