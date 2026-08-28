import { Alert, Button, Group, Modal, NativeSelect, Stack, Text, Textarea } from '@mantine/core'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import type { PauseScopeName } from '../api/pause'
import { pausesSchedule, pausesTriggers, scopeFrom } from '../api/pause'
import type { ProblemError } from '../api/problem'
import type { PauseRequest, PauseResponse } from '../api/types'

// None is absent: the switches are reopened by the banner's own buttons, which know which of the
// two to leave closed. Offering it here would be a second way to do that, and a worse one.
const CLOSABLE = [
  { value: 'Schedule', label: 'Scheduling only' },
  { value: 'Triggers', label: 'Manual triggers only' },
  { value: 'All', label: 'Everything' },
]

function setAt(state: PauseResponse | undefined): string {
  return state?.setAtUtc ? new Date(state.setAtUtc).toLocaleString() : 'an unrecorded time'
}

/**
 * The cluster-wide pause switches: what is closed, why, who closed it -- and the controls to move
 * them. Scheduling and triggers are moved separately throughout, because during an incident the
 * usual thing to want is automatic work stopped while one job can still be run by hand.
 */
export function PauseBanner() {
  const queryClient = useQueryClient()
  const [opened, setOpened] = useState(false)
  const [scope, setScope] = useState<PauseScopeName>('Schedule')
  const [reason, setReason] = useState('')

  const { data } = useQuery<PauseResponse, ProblemError>({
    queryKey: ['pause'],
    queryFn: () => api.get<PauseResponse>('/pause'),
  })

  const move = useMutation<void, ProblemError, PauseRequest>({
    // No setBy: PauseEndpoints records the authenticated principal, and an audit field a caller
    // can write is an audit field a caller can forge.
    mutationFn: (request) => api.put<void>('/pause', request),
    onSuccess: () => {
      setOpened(false)
      setReason('')
      queryClient.invalidateQueries({ queryKey: ['pause'] })
      queryClient.invalidateQueries({ queryKey: ['jobs'] })
    },
  })

  const schedule = pausesSchedule(data?.scope ?? 'None')
  const triggers = pausesTriggers(data?.scope ?? 'None')

  // A partial resume keeps the reason the remaining switch was closed for; a full one has nothing
  // left to explain.
  const resume = (next: PauseScopeName) =>
    move.mutate({ scope: next, reason: next === 'None' ? null : (data?.reason ?? null) })

  // The Operate policy answers 403 with no body at all, so that case needs a line of its own
  // rather than the generic one.
  const failure = move.error
    ? problemMessage(
        move.error,
        move.error.status === 403
          ? 'This sign-in may read the dashboard but not move the pause switches.'
          : 'The pause switches could not be moved.',
      )
    : null

  return (
    <Stack gap="xs">
      {schedule || triggers ? (
        <Alert color="yellow" title={title(schedule, triggers)}>
          <Stack gap="xs">
            <Text size="sm">{data?.reason || 'No reason was given.'}</Text>
            <Text size="sm" c="dimmed">
              Set by {data?.setBy || 'an unrecorded caller'} at {setAt(data)}.
            </Text>
            <Text size="sm">{stillRunning(schedule, triggers)}</Text>

            <Group gap="xs">
              {schedule && (
                <Button
                  size="xs"
                  variant="default"
                  loading={move.isPending}
                  onClick={() => resume(scopeFrom(false, triggers))}
                >
                  Resume scheduling
                </Button>
              )}
              {triggers && (
                <Button
                  size="xs"
                  variant="default"
                  loading={move.isPending}
                  onClick={() => resume(scopeFrom(schedule, false))}
                >
                  Resume triggers
                </Button>
              )}
              {schedule && triggers && (
                <Button size="xs" loading={move.isPending} onClick={() => resume('None')}>
                  Resume everything
                </Button>
              )}
              <Button size="xs" variant="subtle" onClick={() => setOpened(true)}>
                Pause…
              </Button>
            </Group>
          </Stack>
        </Alert>
      ) : (
        <Group gap="sm">
          <Text size="sm" c="dimmed">
            Scheduling and manual triggers are running.
          </Text>
          <Button size="xs" variant="default" onClick={() => setOpened(true)}>
            Pause…
          </Button>
        </Group>
      )}

      {failure && (
        <Alert color="red" title="The pause switches were not moved">
          {failure}
        </Alert>
      )}

      <Modal opened={opened} onClose={() => setOpened(false)} title="Pause">
        <Stack gap="md">
          <NativeSelect
            label="What to pause"
            description="Scheduling and manual triggers are independent switches."
            data={CLOSABLE}
            value={scope}
            onChange={(event) => setScope(event.currentTarget.value as PauseScopeName)}
          />

          <Textarea
            label="Reason"
            description="Shown to whoever finds the switches closed."
            value={reason}
            onChange={(event) => setReason(event.currentTarget.value)}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              Cancel
            </Button>
            <Button
              loading={move.isPending}
              disabled={reason.trim() === ''}
              onClick={() => move.mutate({ scope, reason: reason.trim() })}
            >
              Pause
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  )
}

function title(schedule: boolean, triggers: boolean): string {
  if (schedule && triggers) return 'Scheduling and manual triggers are paused'

  return schedule ? 'Scheduling is paused' : 'Manual triggers are paused'
}

function stillRunning(schedule: boolean, triggers: boolean): string {
  if (schedule && triggers) return 'Nothing starts on any instance.'

  return schedule
    ? 'Manual triggers still run, so one job can still be started by hand.'
    : 'The schedule still runs, so occurrences are still claimed.'
}
