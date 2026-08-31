import { Badge, Table, Text } from '@mantine/core'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import { statusColor } from '../api/status'
import type { RunSummaryResponse } from '../api/types'

function formatInstant(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

const columnHelper = createColumnHelper<RunSummaryResponse>()

const columns = [
  columnHelper.accessor('jobName', { header: 'Job' }),
  columnHelper.accessor('status', {
    header: 'Status',
    cell: (info) => <Badge color={statusColor(info.getValue())}>{info.getValue()}</Badge>,
  }),
  columnHelper.accessor('trigger', { header: 'Trigger' }),
  columnHelper.accessor('instanceId', { header: 'Instance' }),
  columnHelper.accessor('startedAtUtc', {
    header: 'Started',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor('completedAtUtc', {
    header: 'Completed',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor('duration', { header: 'Duration', cell: (info) => info.getValue() ?? '—' }),
  columnHelper.accessor('error', {
    header: 'Error',
    cell: (info) =>
      info.getValue() ? (
        <Text size="sm" c="red" truncate maw={280}>
          {info.getValue()}
        </Text>
      ) : (
        '—'
      ),
  }),
]

interface RunsTableProps {
  runs: RunSummaryResponse[]
  onSelectRun: (runId: string) => void
}

/** The run history, newest first as the server sends it. A row opens that run's detail. */
export function RunsTable({ runs, onSelectRun }: RunsTableProps) {
  // TanStack Table is the plan's table for this list; useReactTable's return is not meant to be
  // memoized by callers, which is what this rule otherwise guards against.
  // oxlint-disable-next-line react/incompatible-library
  const table = useReactTable({
    data: runs,
    columns,
    getCoreRowModel: getCoreRowModel(),
    getRowId: (row) => row.runId,
  })

  return (
    <Table.ScrollContainer minWidth={800}>
      <Table striped highlightOnHover>
        <Table.Thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <Table.Tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => (
                <Table.Th key={header.id}>
                  {flexRender(header.column.columnDef.header, header.getContext())}
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
                  No runs match these filters.
                </Text>
              </Table.Td>
            </Table.Tr>
          ) : (
            table.getRowModel().rows.map((row) => (
              <Table.Tr
                key={row.id}
                onClick={() => onSelectRun(row.original.runId)}
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
