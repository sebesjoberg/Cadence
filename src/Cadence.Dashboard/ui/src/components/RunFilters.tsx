import { Grid, NativeSelect, TextInput } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { RUN_STATUSES } from '../api/runStatus'
import type { RunStatus } from '../api/runStatus'
import type { InstancesResponse } from '../api/types'

export interface RunFiltersValue {
  job?: string
  status?: RunStatus
  instance?: string
  from?: string
  to?: string
}

interface RunFiltersProps {
  value: RunFiltersValue
  onChange: (patch: RunFiltersValue) => void
}

const STATUS_DATA = [
  { value: '', label: 'All statuses' },
  ...RUN_STATUSES.map((status) => ({ value: status, label: status })),
]

/** Job, status, instance and a date range -- every field RunQuery can filter a run history by. */
export function RunFilters({ value, onChange }: RunFiltersProps) {
  // A failed lookup leaves the dropdown at whatever the URL asked for rather than blocking the
  // filters: the run list is still readable without knowing which instances are alive.
  const { data } = useQuery<InstancesResponse>({
    queryKey: ['instances'],
    queryFn: () => api.get<InstancesResponse>('/instances'),
    retry: false,
  })

  const known = data?.instances.map((instance) => instance.instanceId) ?? []
  const selected = value.instance

  // An instance the janitor has since purged still names real runs, so a shared deep link keeps
  // working: the id is kept as an option even when the cluster no longer reports it.
  const ids = selected && !known.includes(selected) ? [selected, ...known] : known

  return (
    <Grid align="end">
      <Grid.Col span={{ base: 12, sm: 3 }}>
        <TextInput
          label="Job"
          placeholder="All jobs"
          value={value.job ?? ''}
          onChange={(event) => onChange({ job: event.currentTarget.value || undefined })}
        />
      </Grid.Col>

      <Grid.Col span={{ base: 12, sm: 2 }}>
        <NativeSelect
          label="Status"
          data={STATUS_DATA}
          value={value.status ?? ''}
          onChange={(event) =>
            onChange({ status: (event.currentTarget.value || undefined) as RunStatus | undefined })
          }
        />
      </Grid.Col>

      <Grid.Col span={{ base: 12, sm: 3 }}>
        <NativeSelect
          label="Instance"
          data={[
            { value: '', label: 'All instances' },
            ...ids.map((id) => ({ value: id, label: id })),
          ]}
          value={selected ?? ''}
          onChange={(event) => onChange({ instance: event.currentTarget.value || undefined })}
        />
      </Grid.Col>

      <Grid.Col span={{ base: 6, sm: 2 }}>
        <TextInput
          type="date"
          label="From"
          value={value.from ?? ''}
          onChange={(event) => onChange({ from: event.currentTarget.value || undefined })}
        />
      </Grid.Col>

      <Grid.Col span={{ base: 6, sm: 2 }}>
        <TextInput
          type="date"
          label="To"
          value={value.to ?? ''}
          onChange={(event) => onChange({ to: event.currentTarget.value || undefined })}
        />
      </Grid.Col>
    </Grid>
  )
}
