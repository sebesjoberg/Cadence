import { ActionIcon, Code, CopyButton, Group, Stack, Text, Tooltip } from '@mantine/core'
import { machineTriggerUrl } from '../api/jobActions'

/**
 * How a machine starts this job. It is on the dashboard because this is where a person already is
 * when they decide to automate something, and where they mint the token to do it with.
 *
 * The route belongs to the machine tree, which a deployment may not have mounted -- the dashboard
 * cannot know from here, so the requirement is stated rather than probed.
 */
export function MachineTrigger({ jobName }: { jobName: string }) {
  const url = machineTriggerUrl(jobName)

  return (
    <Stack gap={4}>
      <Text fw={500} size="sm">
        Trigger from a machine
      </Text>

      <Group gap="xs" wrap="nowrap">
        <Code style={{ overflowX: 'auto', whiteSpace: 'nowrap' }}>POST {url}</Code>

        <CopyButton value={url}>
          {({ copied, copy }) => (
            <Tooltip label={copied ? 'Copied' : 'Copy URL'}>
              <ActionIcon variant="subtle" color={copied ? 'green' : 'gray'} onClick={copy}>
                {copied ? '✓' : '⧉'}
              </ActionIcon>
            </Tooltip>
          )}
        </CopyButton>
      </Group>

      <Text size="xs" c="dimmed">
        Needs <Code>MapCadenceApi()</Code> mounted and an <Code>operate</Code>-scoped bearer token —
        mint one on the Tokens screen. Runs started this way are recorded as <Code>Api</Code>, not{' '}
        <Code>Manual</Code>, which is what keeps &quot;someone clicked&quot; separable from
        &quot;something called us&quot; in the history.
      </Text>
    </Stack>
  )
}
