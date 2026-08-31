import { Box, Group, Text, Tooltip } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import type { StorageHealthResponse } from '../api/types'

const COLORS: Record<string, string> = {
  Healthy: 'green',
  Degraded: 'yellow',
  Unhealthy: 'red',
}

/** The storage tier's own verdict, polled: a dashboard reading a store that is down says so. */
export function HealthDot() {
  const { data, isError } = useQuery({
    queryKey: ['health', 'storage'],
    queryFn: () => api.get<StorageHealthResponse>('/health/storage'),
    refetchInterval: 15_000,
  })

  const status = data?.status ?? (isError ? 'Unreachable' : 'Checking')
  const color = COLORS[status] ?? 'gray'

  const detail = data?.checks
    .map((check) => `${check.name}: ${check.status}${check.error ? ` — ${check.error}` : ''}`)
    .join('\n')

  return (
    <Tooltip label={detail || status} multiline maw={360}>
      <Group gap={6} wrap="nowrap" role="status" aria-label="Storage health">
        <Box w={10} h={10} bg={`var(--mantine-color-${color}-6)`} style={{ borderRadius: '50%' }} />
        <Text size="sm" c="dimmed">
          {status}
        </Text>
      </Group>
    </Tooltip>
  )
}
