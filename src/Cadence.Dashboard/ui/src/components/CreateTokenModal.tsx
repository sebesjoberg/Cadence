import {
  Alert,
  Button,
  CopyButton,
  Group,
  Modal,
  NativeSelect,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import type { FormEvent } from 'react'
import { useState } from 'react'
import { api } from '../api/client'
import { ProblemError, UnauthenticatedError } from '../api/problem'
import type { ApiTokenCreatedResponse, ApiTokenRequest } from '../api/types'

const SCOPES = ['Read', 'Operate'] as const
type Scope = (typeof SCOPES)[number]

interface CreateTokenModalProps {
  opened: boolean
  onClose: () => void
}

function tokenErrorMessage(error: unknown): string {
  // The stale-session case carries no problem document at all -- client.ts has already started
  // the redirect by the time this runs -- so it gets its own branch rather than falling through
  // to a generic line that would misname it as a permissions failure.
  if (error instanceof UnauthenticatedError) return error.message
  if (error instanceof ProblemError) return error.detail || error.title
  return 'Failed to create the token.'
}

/**
 * Creates a token and shows its secret exactly once. The secret lives only in this component's
 * state, never the query cache -- a cached 201 body would let it reappear on a remount.
 */
export function CreateTokenModal({ opened, onClose }: CreateTokenModalProps) {
  const [name, setName] = useState('')
  const [scope, setScope] = useState<Scope>('Read')
  const [expiresAt, setExpiresAt] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [created, setCreated] = useState<ApiTokenCreatedResponse | null>(null)

  const reset = () => {
    setName('')
    setScope('Read')
    setExpiresAt('')
    setError(null)
    setCreated(null)
  }

  const handleClose = () => {
    reset()
    onClose()
  }

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    const body: ApiTokenRequest = {
      name,
      scope,
      expiresAtUtc: expiresAt ? new Date(expiresAt).toISOString() : null,
    }

    try {
      const response = await api.post<ApiTokenCreatedResponse>('/tokens', body)
      setCreated(response)
    } catch (err) {
      setError(err)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal opened={opened} onClose={handleClose} title="Create token" centered>
      {created ? (
        <Stack gap="md">
          <Alert color="yellow" title="Copy this secret now">
            It is shown only this once and cannot be retrieved after you close this dialog.
          </Alert>

          <Text ff="monospace" fw={700} style={{ wordBreak: 'break-all' }}>
            {created.token}
          </Text>

          <Group justify="flex-end">
            <CopyButton value={created.token}>
              {({ copied, copy }) => (
                <Button variant="default" onClick={copy}>
                  {copied ? 'Copied' : 'Copy'}
                </Button>
              )}
            </CopyButton>
            <Button onClick={handleClose}>Done</Button>
          </Group>
        </Stack>
      ) : (
        <form onSubmit={handleSubmit}>
          <Stack gap="md">
            {error !== null && (
              <Alert color="red" title="Could not create the token">
                {tokenErrorMessage(error)}
              </Alert>
            )}

            <TextInput
              label="Name"
              required
              value={name}
              onChange={(event) => setName(event.currentTarget.value)}
            />

            <NativeSelect
              label="Scope"
              data={[...SCOPES]}
              value={scope}
              onChange={(event) => setScope(event.currentTarget.value as Scope)}
            />

            <TextInput
              type="date"
              label="Expires"
              value={expiresAt}
              onChange={(event) => setExpiresAt(event.currentTarget.value)}
            />

            <Group justify="flex-end">
              <Button variant="default" type="button" onClick={handleClose}>
                Cancel
              </Button>
              <Button type="submit" loading={submitting}>
                Create token
              </Button>
            </Group>
          </Stack>
        </form>
      )}
    </Modal>
  )
}
