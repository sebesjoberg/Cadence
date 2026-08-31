import { ActionIcon, Group, NativeSelect, Tooltip } from '@mantine/core'
import { useIsFetching, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'

const KEY = 'cadence.refreshSeconds'

const CHOICES = [
  { value: '0', label: 'Manual' },
  { value: '5', label: '5s' },
  { value: '15', label: '15s' },
  { value: '30', label: '30s' },
  { value: '60', label: '60s' },
]

// Per-browser convenience, so it is fine that it does not survive a cleared profile or a private
// window. Wrapped because a browser set to block site data throws on the accessor itself.
function readStored(): string {
  try {
    const stored = window.localStorage.getItem(KEY)
    return CHOICES.some((choice) => choice.value === stored) ? stored! : '0'
  } catch {
    return '0'
  }
}

/**
 * Refresh now, or on an interval. Invalidating every query is what keeps this one control correct
 * for screens it knows nothing about -- no screen opts in, and none can be forgotten.
 *
 * Deliberately not a live feed: v0.4 has no streaming surface, and polling the reads the dashboard
 * already makes costs nothing the operator was not already spending by reloading the page.
 */
export function RefreshControl() {
  const queryClient = useQueryClient()
  const fetching = useIsFetching() > 0
  const [seconds, setSeconds] = useState(readStored)

  useEffect(() => {
    const period = Number(seconds)
    if (period === 0) return

    const timer = window.setInterval(() => {
      void queryClient.invalidateQueries()
    }, period * 1000)

    return () => window.clearInterval(timer)
  }, [seconds, queryClient])

  const choose = (value: string) => {
    setSeconds(value)
    try {
      window.localStorage.setItem(KEY, value)
    } catch {
      // A browser that refuses storage still gets the interval for this session.
    }
  }

  return (
    <Group gap="xs" wrap="nowrap">
      <Tooltip label="Refresh now">
        <ActionIcon
          variant="subtle"
          aria-label="Refresh now"
          loading={fetching}
          onClick={() => void queryClient.invalidateQueries()}
        >
          ⟳
        </ActionIcon>
      </Tooltip>

      <NativeSelect
        size="xs"
        aria-label="Auto refresh"
        data={CHOICES}
        value={seconds}
        onChange={(event) => choose(event.currentTarget.value)}
      />
    </Group>
  )
}
