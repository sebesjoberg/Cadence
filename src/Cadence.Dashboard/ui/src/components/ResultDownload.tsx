import { Alert, Button, Group, Stack, Text } from '@mantine/core'
import { useState } from 'react'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import type { JobResultResponse } from '../api/types'

interface ResultDownloadProps {
  runId: string
  result: JobResultResponse
}

/** Renders a byte count the way an operator reads one, not the way a computer stores one. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`

  const units = ['kB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }

  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`
}

/** Collects what a run produced and hands it to the browser as a file. */
export function ResultDownload({ runId, result }: ResultDownloadProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const collect = async () => {
    setBusy(true)
    setError(null)

    try {
      const { blob, fileName } = await api.download(`/runs/${runId}/result`)

      // An object URL is a document-lifetime reference; without the revoke the blob stays resident
      // until the tab closes, which on this screen means every result an operator has looked at.
      const url = URL.createObjectURL(blob)

      try {
        const anchor = document.createElement('a')
        anchor.href = url
        anchor.download = fileName ?? result.fileName ?? `run-${runId}`
        anchor.click()
      } finally {
        URL.revokeObjectURL(url)
      }
    } catch (failure) {
      setError(problemMessage(failure, 'The result could not be collected.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack gap="xs" maw={520}>
      <Group gap="md">
        <Button onClick={() => void collect()} loading={busy} variant="light">
          Download {result.fileName ?? 'result'}
        </Button>
        <Text size="sm" c="dimmed">
          {formatBytes(result.length)} · {result.contentType.split(';')[0]}
        </Text>
      </Group>

      <Text size="xs" c="dimmed">
        Available until {new Date(result.expiresAtUtc).toLocaleString()}. The run itself is kept
        longer than what it produced.
      </Text>

      {error && (
        <Alert color="red" title="Nothing was downloaded">
          {error}
        </Alert>
      )}
    </Stack>
  )
}
