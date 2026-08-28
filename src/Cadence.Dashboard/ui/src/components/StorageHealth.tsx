import { Badge, Group, Stack, Table, Text, Title } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import type { StorageHealthResponse } from '../api/types'

const COLORS: Record<string, string> = {
  Healthy: 'green',
  Degraded: 'yellow',
  Unhealthy: 'red',
}

/**
 * Every registered storage check, with what it had to say about itself. Shares HealthDot's
 * query key, so the nav dot and this panel read one cached answer.
 */
export function StorageHealth() {
  const { data, isPending } = useQuery({
    queryKey: ['health', 'storage'],
    queryFn: () => api.get<StorageHealthResponse>('/health/storage'),
    refetchInterval: 15_000,
  })

  if (isPending || !data) {
    return (
      <Text c="dimmed" size="sm">
        Loading…
      </Text>
    )
  }

  return (
    <Stack gap="xs">
      <Group gap="xs">
        <Title order={5}>Storage</Title>
        <Badge color={COLORS[data.status] ?? 'gray'}>{data.status}</Badge>
      </Group>

      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Check</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th>Description</Table.Th>
            <Table.Th>Error</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {data.checks.length === 0 ? (
            <Table.Tr>
              <Table.Td colSpan={4}>
                <Text c="dimmed" size="sm">
                  No storage checks are registered.
                </Text>
              </Table.Td>
            </Table.Tr>
          ) : (
            data.checks.map((check) => (
              <Table.Tr key={check.name}>
                <Table.Td>{check.name}</Table.Td>
                <Table.Td>
                  <Badge color={COLORS[check.status] ?? 'gray'}>{check.status}</Badge>
                </Table.Td>
                <Table.Td>{check.description ?? '—'}</Table.Td>
                <Table.Td>
                  {check.error ? (
                    <Text size="sm" c="red">
                      {check.error}
                    </Text>
                  ) : (
                    '—'
                  )}
                </Table.Td>
              </Table.Tr>
            ))
          )}
        </Table.Tbody>
      </Table>
    </Stack>
  )
}
