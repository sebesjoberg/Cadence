import { Grid, NativeSelect, TextInput } from '@mantine/core'
import { RUN_STATUSES } from '../api/runStatus'
import type { RunStatus } from '../api/runStatus'

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
        <TextInput
          label="Instance"
          placeholder="All instances"
          value={value.instance ?? ''}
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
