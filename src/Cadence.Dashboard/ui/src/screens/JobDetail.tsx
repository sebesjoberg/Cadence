import { Stack, Text, Title } from '@mantine/core'

export function JobDetail() {
  return (
    <Stack gap="xs">
      <Title order={3}>Job</Title>
      <Text c="dimmed" size="sm">
        One job, its schedule and its recent runs arrive with task 10.
      </Text>
    </Stack>
  )
}
