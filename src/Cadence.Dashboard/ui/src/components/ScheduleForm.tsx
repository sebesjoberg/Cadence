import {
  Alert,
  Button,
  Group,
  NativeSelect,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from '@mantine/core'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { problemMessage } from '../api/message'
import { ProblemError } from '../api/problem'
import { OVERLAP_POLICIES, isTimeSpan, timeZoneIds } from '../api/schedule'
import type { JobDetailResponse, ScheduleResponse, ScheduleWriteRequest } from '../api/types'

interface ScheduleFormProps {
  jobName: string
  detail: JobDetailResponse
  readOnly: boolean
}

interface FormState {
  cronExpression: string
  timeZoneId: string
  enabled: boolean
  /** Empty means the job's own declared policy stands, which is what a null override is. */
  overlap: string
  /** Empty means no limit. */
  maxDuration: string
  settings: Record<string, string>
  version: number
}

const OVERLAP_DATA = [
  { value: '', label: 'The declared policy' },
  ...OVERLAP_POLICIES.map((policy) => ({ value: policy, label: policy })),
]

const MALFORMED_DURATION = 'Not a duration. Use a form like 00:10:00, or leave it empty.'

function fromSchedule(schedule: ScheduleResponse): FormState {
  return {
    cronExpression: schedule.cronExpression,
    timeZoneId: schedule.timeZoneId,
    enabled: schedule.enabled,
    overlap: schedule.overlap ?? '',
    maxDuration: schedule.maxDuration ?? '',
    settings: schedule.settings,
    version: schedule.version,
  }
}

// What the job declares in code. Read off the job detail rather than the schedule route, because
// the deployment that registered no writable source did not mount that route's GET either.
function fromDetail(detail: JobDetailResponse): FormState {
  return {
    cronExpression: detail.job.cron ?? '',
    timeZoneId: detail.job.timeZone ?? 'UTC',
    enabled: detail.job.enabled,
    overlap: detail.overlap ?? '',
    maxDuration: detail.maxDuration ?? '',
    settings: detail.settings,
    version: 0,
  }
}

/** One job's schedule, editable where the container registered somewhere to write it. */
export function ScheduleForm({ jobName, detail, readOnly }: ScheduleFormProps) {
  return readOnly ? (
    <Stack gap="sm">
      <ScheduleFields state={fromDetail(detail)} disabled />
      <Text c="dimmed" size="sm">
        This deployment registered no writable schedule source, so schedules are read-only here.
      </Text>
    </Stack>
  ) : (
    <EditableSchedule jobName={jobName} />
  )
}

interface Notice {
  conflict: boolean
  message: string
}

function EditableSchedule({ jobName }: { jobName: string }) {
  const path = `/jobs/${encodeURIComponent(jobName)}/schedule`
  const queryClient = useQueryClient()

  const [notice, setNotice] = useState<Notice | null>(null)
  const [saved, setSaved] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)

  const { data, error, refetch } = useQuery<ScheduleResponse, ProblemError>({
    queryKey: ['schedule', jobName],
    queryFn: () => api.get<ScheduleResponse>(path),
    // A refused read is not a transient fault, and a refetch on window focus would replace what
    // somebody is halfway through typing. This editor asks for fresh data explicitly instead.
    retry: (failureCount, queryError) => queryError.status >= 500 && failureCount < 1,
    refetchOnWindowFocus: false,
  })

  const save = async (request: ScheduleWriteRequest) => {
    setBusy(true)
    setNotice(null)
    setSaved(null)

    try {
      const stored = await api.put<ScheduleResponse>(path, request)

      // The stored row, not the one just sent: the version the next edit has to carry is the
      // server's. Writing it into the cache is what re-seeds the editor below.
      queryClient.setQueryData(['schedule', jobName], stored)
      queryClient.invalidateQueries({ queryKey: ['jobs'] })
      setSaved(stored.version)
    } catch (failure) {
      const conflict = failure instanceof ProblemError && failure.status === 409

      setNotice({
        conflict,
        message: problemMessage(failure, 'This schedule could not be saved.'),
      })

      if (conflict) {
        // The version the editor held is spent. Reloading is what the server's own prose asks
        // for, and it leaves what is on screen equal to what is stored.
        await refetch()
      }
    } finally {
      setBusy(false)
    }
  }

  if (error && !data) {
    return (
      <Alert color="red" title="Could not load this schedule">
        {problemMessage(error, 'This schedule could not be loaded.')}
      </Alert>
    )
  }

  if (!data) {
    return (
      <Text c="dimmed" size="sm">
        Loading…
      </Text>
    )
  }

  return (
    <Stack gap="sm">
      {/* Keyed on the version, so every answer that moves it -- a save, or the reload a conflict
          forces -- re-seeds the fields from what is actually stored. */}
      <ScheduleEditor
        key={data.version}
        initial={data}
        busy={busy}
        onSave={(request) => void save(request)}
        onReload={() => void refetch()}
      />

      {notice && (
        <Alert
          color="red"
          title={notice.conflict ? 'Someone else changed this schedule' : 'Not saved'}
        >
          {notice.message}
        </Alert>
      )}

      {saved !== null && (
        <Alert color="green" title="Schedule saved">
          <Text size="sm">Now at version {saved}.</Text>
        </Alert>
      )}
    </Stack>
  )
}

interface ScheduleEditorProps {
  initial: ScheduleResponse
  busy: boolean
  onSave: (request: ScheduleWriteRequest) => void
  onReload: () => void
}

function ScheduleEditor({ initial, busy, onSave, onReload }: ScheduleEditorProps) {
  const [state, setState] = useState<FormState>(() => fromSchedule(initial))
  const [malformed, setMalformed] = useState(false)

  const trimmed = state.maxDuration.trim()

  const submit = () => {
    if (trimmed !== '' && !isTimeSpan(trimmed)) {
      // Caught here only because an unparseable duration never reaches the handler that would
      // have written prose about it -- the body fails to bind, and the 400 carries no document.
      setMalformed(true)

      return
    }

    setMalformed(false)

    onSave({
      cronExpression: state.cronExpression,
      timeZoneId: state.timeZoneId,
      enabled: state.enabled,
      overlap: state.overlap === '' ? null : state.overlap,
      maxDuration: trimmed === '' ? null : trimmed,

      // Both explicit, always. Their absent-semantics resolve oppositely -- an absent version is
      // refused with 409, an absent settings object silently preserves -- and neither rule is
      // visible in a schema, so neither absence may be left to happen by accident. Both are
      // values this editor is holding from the read.
      settings: state.settings,
      version: state.version,
    })
  }

  return (
    <Stack gap="sm">
      <ScheduleFields
        state={state}
        disabled={busy}
        maxDurationError={malformed ? MALFORMED_DURATION : undefined}
        onChange={(change) => setState((previous) => ({ ...previous, ...change }))}
      />

      <Group>
        <Button onClick={submit} loading={busy}>
          Save
        </Button>
        <Button variant="default" disabled={busy} onClick={onReload}>
          Reload
        </Button>
      </Group>
    </Stack>
  )
}

interface ScheduleFieldsProps {
  state: FormState
  disabled: boolean
  maxDurationError?: string
  onChange?: (change: Partial<FormState>) => void
}

function ScheduleFields({ state, disabled, maxDurationError, onChange }: ScheduleFieldsProps) {
  const settings = Object.entries(state.settings)

  return (
    <Stack gap="sm" maw={520}>
      <TextInput
        label="Cron expression"
        description="Five fields, or six to include seconds."
        value={state.cronExpression}
        disabled={disabled}
        onChange={(event) => onChange?.({ cronExpression: event.currentTarget.value })}
      />

      <NativeSelect
        label="Timezone"
        data={timeZoneIds(state.timeZoneId)}
        value={state.timeZoneId}
        disabled={disabled}
        onChange={(event) => onChange?.({ timeZoneId: event.currentTarget.value })}
      />

      <Switch
        label="Enabled"
        checked={state.enabled}
        disabled={disabled}
        onChange={(event) => onChange?.({ enabled: event.currentTarget.checked })}
      />

      <NativeSelect
        label="Overlap"
        data={OVERLAP_DATA}
        value={state.overlap}
        disabled={disabled}
        onChange={(event) => onChange?.({ overlap: event.currentTarget.value })}
      />

      <TextInput
        label="Maximum duration"
        placeholder="00:10:00"
        description="Empty for no limit."
        value={state.maxDuration}
        disabled={disabled}
        error={maxDurationError}
        onChange={(event) => onChange?.({ maxDuration: event.currentTarget.value })}
      />

      <Stack gap={4}>
        <Text size="sm" fw={500}>
          Settings
        </Text>
        {settings.length === 0 ? (
          <Text c="dimmed" size="sm">
            None.
          </Text>
        ) : (
          <Table withTableBorder>
            <Table.Tbody>
              {settings.map(([key, value]) => (
                <Table.Tr key={key}>
                  <Table.Td>{key}</Table.Td>
                  <Table.Td>{value}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Text c="dimmed" size="xs">
          Carried through a save unchanged. Editing them is not part of this screen.
        </Text>
      </Stack>
    </Stack>
  )
}
