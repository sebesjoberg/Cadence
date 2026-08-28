import { Alert, Button, Group, Stack, Text, Title } from '@mantine/core'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import type { ProblemError } from '../api/problem'
import type { ApiTokenResponse } from '../api/types'
import { CreateTokenModal } from '../components/CreateTokenModal'
import { TokensTable } from '../components/TokensTable'

function tokensErrorMessage(error: ProblemError): string {
  if (error.detail) return error.detail
  if (error.type) return error.title
  return 'Could not load tokens.'
}

export function Tokens() {
  const [modalOpened, setModalOpened] = useState(false)
  const queryClient = useQueryClient()

  const { data, error, isPending } = useQuery<ApiTokenResponse[], ProblemError>({
    queryKey: ['tokens'],
    queryFn: () => api.get<ApiTokenResponse[]>('/tokens'),
  })

  const revoke = useMutation({
    mutationFn: (id: string) => api.delete<void>(`/tokens/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['tokens'] }),
  })

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Title order={3}>Tokens</Title>
        <Button onClick={() => setModalOpened(true)}>New token</Button>
      </Group>

      {error && (
        <Alert color="red" title="Could not load tokens">
          {tokensErrorMessage(error)}
        </Alert>
      )}

      {isPending || !data ? (
        <Text c="dimmed" size="sm">
          Loading…
        </Text>
      ) : (
        <TokensTable
          tokens={data}
          revokingId={revoke.isPending ? (revoke.variables ?? null) : null}
          onRevoke={(id, name) => {
            if (window.confirm(`Revoke the token '${name}'?`)) {
              revoke.mutate(id)
            }
          }}
        />
      )}

      <CreateTokenModal
        opened={modalOpened}
        onClose={() => {
          setModalOpened(false)
          queryClient.invalidateQueries({ queryKey: ['tokens'] })
        }}
      />
    </Stack>
  )
}
