import { Stack, Text } from '@mantine/core'
import type { LogEntryResponse } from '../api/types'

/** Progress entries, rendered exactly in the order the server sent them -- oldest first. */
export function RunLog({ entries }: { entries: LogEntryResponse[] }) {
  if (entries.length === 0) {
    return (
      <Text c="dimmed" size="sm">
        No progress was reported.
      </Text>
    )
  }

  return (
    <Stack gap={4}>
      {entries.map((entry, index) => (
        <Text key={`${entry.timestampUtc}-${index}`} size="sm" data-testid="run-log-entry">
          <Text span c="dimmed">
            {new Date(entry.timestampUtc).toLocaleString()}
          </Text>{' '}
          — {entry.message}
        </Text>
      ))}
    </Stack>
  )
}
