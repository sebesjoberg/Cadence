import { Button, Table, Text } from '@mantine/core'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import type { ApiTokenResponse } from '../api/types'

function formatInstant(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

const columnHelper = createColumnHelper<ApiTokenResponse>()

interface RevokeCellProps {
  token: ApiTokenResponse
  revoking: boolean
  onRevoke: (id: string, name: string) => void
}

function RevokeCell({ token, revoking, onRevoke }: RevokeCellProps) {
  return (
    <Button
      variant="subtle"
      color="red"
      size="xs"
      loading={revoking}
      onClick={() => onRevoke(token.id, token.name)}
    >
      Revoke
    </Button>
  )
}

/** revokingId/onRevoke reach the revoke column through table meta, so columns stays a module-level
 * constant instead of a per-render nested component definition. */
interface TableMeta {
  revokingId: string | null
  onRevoke: (id: string, name: string) => void
}

const columns = [
  columnHelper.accessor('name', { header: 'Name' }),
  columnHelper.accessor('fingerprint', { header: 'Fingerprint' }),
  columnHelper.accessor('scope', { header: 'Scope' }),
  columnHelper.accessor('createdAtUtc', {
    header: 'Created',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.accessor('createdBy', {
    header: 'Created by',
    cell: (info) => info.getValue() ?? '—',
  }),
  columnHelper.accessor('expiresAtUtc', {
    header: 'Expires',
    cell: (info) => formatInstant(info.getValue()),
  }),
  columnHelper.display({
    id: 'revoke',
    header: '',
    cell: (info) => {
      const meta = info.table.options.meta as TableMeta
      return (
        <RevokeCell
          token={info.row.original}
          revoking={meta.revokingId === info.row.original.id}
          onRevoke={meta.onRevoke}
        />
      )
    },
  }),
]

interface TokensTableProps {
  tokens: ApiTokenResponse[]
  revokingId: string | null
  onRevoke: (id: string, name: string) => void
}

/** Every administered token. Carries no secret -- ApiTokenResponse never does. */
export function TokensTable({ tokens, revokingId, onRevoke }: TokensTableProps) {
  // TanStack Table's return is not meant to be memoized by callers, which is what this rule
  // otherwise guards against.
  // oxlint-disable-next-line react/incompatible-library
  const table = useReactTable({
    data: tokens,
    columns,
    getCoreRowModel: getCoreRowModel(),
    getRowId: (row) => row.id,
    meta: { revokingId, onRevoke } satisfies TableMeta,
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
                  No tokens have been created.
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
