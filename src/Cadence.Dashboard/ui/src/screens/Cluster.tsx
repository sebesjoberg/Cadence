import { Alert, Stack, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import type { ProblemError } from '../api/problem'
import type { InstancesResponse } from '../api/types'
import { InstancesTable } from '../components/InstancesTable'
import { StorageHealth } from '../components/StorageHealth'

function instancesErrorMessage(error: ProblemError): string {
  // Same fallback shape as the run screens: the server's own prose reaches the operator verbatim,
  // and its absence is a generic line rather than the framework's bare statusText.
  if (error.detail) return error.detail
  if (error.type) return error.title
  return 'Could not load the registered instances.'
}

export function Cluster() {
  const { data, error, isPending } = useQuery<InstancesResponse, ProblemError>({
    queryKey: ['instances'],
    queryFn: () => api.get<InstancesResponse>('/instances'),
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
  })

  if (error) {
    return (
      <Stack gap="xs">
        <Title order={3}>Cluster</Title>
        <Alert color="red" title="Could not load instances">
          {instancesErrorMessage(error)}
        </Alert>
      </Stack>
    )
  }

  return (
    <Stack gap="md">
      <Title order={3}>Cluster</Title>

      {isPending || !data ? (
        <Text c="dimmed" size="sm">
          Loading…
        </Text>
      ) : (
        <InstancesTable instances={data.instances} heartbeatTimeout={data.heartbeatTimeout} />
      )}

      <StorageHealth />
    </Stack>
  )
}
