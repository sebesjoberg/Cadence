import { Badge, Table, Text, UnstyledButton } from '@mantine/core'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
} from '@tanstack/react-table'
import type { SortingState } from '@tanstack/react-table'
import { useState } from 'react'
import type { JobSummaryResponse } from '../api/types'

// Mirrors RunsTable's own map. Kept local for the same reason that one is: it is presentation for
// this table, not part of the run-status domain that api/runStatus.ts holds.
const STATUS_COLORS: Record<string, string> = {
  Running: 'blue',
  Succeeded: 'green',
  Failed: 'red',
  TimedOut: 'orange',
  Aborted: 'gray',
  Skipped: 'gray',
  Lost: 'red',
}

const ARROWS: Record<string, string> = { asc: ' ▲', desc: ' ▼' }

function formatInstant(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

const columnHelper = createColumnHelper<JobSummaryResponse>()

const columns = [
  columnHelper.accessor('name', { header: 'Job' }),
  columnHelper.accessor('cron', {
    header: 'Cron',
    enableSorting: false,
    // A trigger-only job has no expression at all, which is not the same as an empty one.
    cell: (info) => info.getValue() ?? 'Trigger only',
  }),
  columnHelper.accessor('timeZone', {
    header: 'Timezone',
    enableSorting: false,
    cell: (info) => info.getValue() ?? '—',
  }),
  columnHelper.accessor('enabled', {
    header: 'Enabled',
    enableSorting: false,
    cell: (info) => (
      <Badge color={info.getValue() ? 'green' : 'gray'}>
        {info.getValue() ? 'Enabled' : 'Disabled'}
      </Badge>
    ),
  }),
  columnHelper.accessor('nextOccurrenceUtc', {
    header: 'Next occurrence',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor((row) => row.lastRun?.status ?? '', {
    id: 'lastRunStatus',
    header: 'Last run',
    cell: (info) =>
      info.getValue() ? (
        <Badge color={STATUS_COLORS[info.getValue()] ?? 'gray'}>{info.getValue()}</Badge>
      ) : (
        'Never run'
      ),
  }),
  columnHelper.accessor((row) => row.lastRun?.startedAtUtc ?? null, {
    id: 'lastRunAt',
    header: 'Last run at',
    enableSorting: false,
    cell: (info) => formatInstant(info.getValue()),
  }),
]

interface JobsTableProps {
  jobs: JobSummaryResponse[]
  onSelectJob: (name: string) => void
}

/** Every registered job, as the overview shows it. A row opens that job. */
export function JobsTable({ jobs, onSelectJob }: JobsTableProps) {
  const [sorting, setSorting] = useState<SortingState>([])

  // TanStack Table is the plan's table for this list; useReactTable's return is not meant to be
  // memoized by callers, which is what this rule otherwise guards against.
  // oxlint-disable-next-line react/incompatible-library
  const table = useReactTable({
    data: jobs,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getRowId: (row) => row.name,
  })

  return (
    <Table.ScrollContainer minWidth={800}>
      <Table striped highlightOnHover>
        <Table.Thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <Table.Tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => (
                <Table.Th key={header.id}>
                  {header.column.getCanSort() ? (
                    <UnstyledButton
                      fz="sm"
                      fw={700}
                      onClick={header.column.getToggleSortingHandler()}
                    >
                      {flexRender(header.column.columnDef.header, header.getContext())}
                      {ARROWS[header.column.getIsSorted() || ''] ?? ''}
                    </UnstyledButton>
                  ) : (
                    flexRender(header.column.columnDef.header, header.getContext())
                  )}
                </Table.Th>
              ))}
            </Table.Tr>
          ))}
        </Table.Thead>

        <Table.Tbody>
          {table.getRowModel().rows.length === 0 ? (
            <Table.Tr>
              <Table.Td colSpan={columns.length}>
                <Text c="dimmed" size="sm">
                  This instance has no registered jobs.
                </Text>
              </Table.Td>
            </Table.Tr>
          ) : (
            table.getRowModel().rows.map((row) => (
              <Table.Tr
                key={row.id}
                onClick={() => onSelectJob(row.original.name)}
                style={{ cursor: 'pointer' }}
              >
                {row.getVisibleCells().map((cell) => (
                  <Table.Td key={cell.id}>
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </Table.Td>
                ))}
              </Table.Tr>
            ))
          )}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  )
}
