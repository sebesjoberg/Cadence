import { Button, Group, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { setJobEnabled } from '../api/jobActions'
import { problemMessage } from '../api/message'
import type { JobSummaryResponse, TriggerResponse } from '../api/types'
import { bootstrap } from '../bootstrap'

/**
 * The two things an operator does to a job, on the row rather than one navigation away. Pause is
 * absent for a trigger-only job: `Enabled` is read by the tick loop alone, so a job with no cron
 * has no occurrences to stop and the button would claim to do something it cannot.
 */
export function JobRowActions({ job }: { job: JobSummaryResponse }) {
  const queryClient = useQueryClient()
  const [triggering, setTriggering] = useState(false)
  const scheduled = job.cron !== null

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['jobs'] })

  const pause = useMutation({
    mutationFn: () => setJobEnabled(job.name, !job.enabled),
    onSuccess: (schedule) => {
      notifications.show({
        color: 'green',
        message: schedule.enabled
          ? `${job.name} is scheduled again.`
          : `${job.name} is paused. Its occurrences stop; a manual trigger still runs it.`,
      })
      refresh()
    },
    onError: (failure) => {
      notifications.show({
        color: 'red',
        title: 'The schedule was not changed',
        message: problemMessage(failure, 'The schedule was not changed.'),
      })
      // A 409 means the row moved underneath this click; refetching is what makes the next one work.
      refresh()
    },
  })

  const trigger = async () => {
    setTriggering(true)

    try {
      // No body: 13.2 keeps the trigger at "start the job as configured".
      const run = await api.post<TriggerResponse>(
        `/jobs/${encodeURIComponent(job.name)}/trigger`,
        null,
      )

      notifications.show({
        color: 'green',
        message: `Started run ${run.runId} on ${run.instanceId}.`,
      })
      refresh()
    } catch (failure) {
      notifications.show({
        color: 'red',
        title: 'No run was started',
        message: problemMessage(failure, 'No run was started.'),
      })
    } finally {
      setTriggering(false)
    }
  }

  return (
    <Group gap="xs" wrap="nowrap" onClick={(event) => event.stopPropagation()}>
      <Button size="compact-sm" variant="light" loading={triggering} onClick={() => void trigger()}>
        Trigger
      </Button>

      {scheduled && bootstrap.capabilities.scheduleWrite && (
        <Tooltip
          label={
            job.enabled
              ? 'Stop claiming occurrences. Manual triggers still run.'
              : 'Start claiming occurrences again.'
          }
        >
          <Button
            size="compact-sm"
            variant="subtle"
            color={job.enabled ? 'orange' : 'green'}
            loading={pause.isPending}
            onClick={() => pause.mutate()}
          >
            {job.enabled ? 'Pause' : 'Resume'}
          </Button>
        </Tooltip>
      )}
    </Group>
  )
}
