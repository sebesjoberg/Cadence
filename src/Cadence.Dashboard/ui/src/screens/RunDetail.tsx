import { Stack, Text, Title } from '@mantine/core'

export function RunDetail() {
  return (
    <Stack gap="xs">
      <Title order={3}>Run</Title>
      <Text c="dimmed" size="sm">
        One run and its log arrive with task 11.
      </Text>
    </Stack>
  )
}
