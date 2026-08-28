import { Stack, Text, Title } from '@mantine/core'

export function Jobs() {
  return (
    <Stack gap="xs">
      <Title order={3}>Jobs</Title>
      <Text c="dimmed" size="sm">
        Job list and schedule editing arrive with task 10.
      </Text>
    </Stack>
  )
}
