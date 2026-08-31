import { Alert, Button, Stack, Text } from '@mantine/core'
import { useState } from 'react'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import type { TriggerResponse } from '../api/types'

interface TriggerButtonProps {
  jobName: string
  onTriggered?: () => void
}

/** Starts one run by hand. Whatever the server says about a refusal is what is shown. */
export function TriggerButton({ jobName, onTriggered }: TriggerButtonProps) {
  const [busy, setBusy] = useState(false)
  const [started, setStarted] = useState<TriggerResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const trigger = async () => {
    setBusy(true)
    setStarted(null)
    setError(null)

    try {
      // No body: the route takes none, and 13.2 keeps it that way so the trigger cannot widen
      // into "start the job with arbitrary input".
      const run = await api.post<TriggerResponse>(
        `/jobs/${encodeURIComponent(jobName)}/trigger`,
        null,
      )

      setStarted(run)
      onTriggered?.()
    } catch (failure) {
      setError(problemMessage(failure, 'The trigger request did not reach the server.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack gap="xs" maw={420}>
      <Button onClick={() => void trigger()} loading={busy}>
        Trigger
      </Button>

      {started && (
        <Alert color="green" title="Run started">
          <Text size="sm">
            Run {started.runId} on {started.instanceId}.
          </Text>
        </Alert>
      )}

      {error && (
        <Alert color="red" title="No run was started">
          {error}
        </Alert>
      )}
    </Stack>
  )
}
