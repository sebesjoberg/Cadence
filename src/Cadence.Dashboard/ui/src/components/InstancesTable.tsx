import { Badge, Table, Text } from '@mantine/core'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import { parseTimeSpanMs } from '../api/timespan'
import type { InstanceResponse } from '../api/types'

function formatInstant(value: string): string {
  return new Date(value).toLocaleString()
}

interface InstanceRow extends InstanceResponse {
  stale: boolean
}

const columnHelper = createColumnHelper<InstanceRow>()

const columns = [
  columnHelper.accessor('instanceId', { header: 'Instance' }),
  columnHelper.accessor('machineName', { header: 'Machine' }),
  columnHelper.accessor('processId', { header: 'PID' }),
  columnHelper.accessor('assemblyVersion', {
    header: 'Version',
    cell: (info) => info.getValue() ?? '—',
  }),
  columnHelper.accessor('startedAtUtc', {
    header: 'Started',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor('lastHeartbeatUtc', {
    header: 'Last heartbeat',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor('stale', {
    header: 'Status',
    cell: (info) =>
      info.getValue() ? <Badge color="red">Stale</Badge> : <Badge color="green">Live</Badge>,
  }),
]

interface InstancesTableProps {
  instances: InstanceResponse[]
  heartbeatTimeout: string
}

/**
 * Every registered instance, stale ones included: dropping a dead instance would hide exactly
 * what the operator opened this screen to see. Staleness is drawn at the same line the janitor
 * uses, since heartbeatTimeout is its own threshold.
 */
export function InstancesTable({ instances, heartbeatTimeout }: InstancesTableProps) {
  const timeoutMs = parseTimeSpanMs(heartbeatTimeout)
  const now = Date.now()

  const rows: InstanceRow[] = instances.map((instance) => ({
    ...instance,
    stale: now - new Date(instance.lastHeartbeatUtc).getTime() > timeoutMs,
  }))

  // TanStack Table's return is not meant to be memoized by callers, which is what this rule
  // otherwise guards against.
  // oxlint-disable-next-line react/incompatible-library
  const table = useReactTable({
    data: rows,
    columns,
    getCoreRowModel: getCoreRowModel(),
    getRowId: (row) => row.instanceId,
  })

  return (
    <Table.ScrollContainer minWidth={700}>
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
                  No instances are registered.
                </Text>
              </Table.Td>
            </Table.Tr>
          ) : (
            table.getRowModel().rows.map((row) => (
              <Table.Tr key={row.id}>
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
