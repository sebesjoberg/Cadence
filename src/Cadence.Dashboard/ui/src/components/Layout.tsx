import { AppShell, Anchor, Group, Stack, Title } from '@mantine/core'
import { Link } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { bootstrap } from '../bootstrap'
import { HealthDot } from './HealthDot'

// A link is rendered only where the route behind it was mounted, which is what the capability
// flags record: no writable token store means no /tokens tree to reach.
const LINKS = [
  { to: '/', label: 'Jobs', capability: null },
  { to: '/runs', label: 'Runs', capability: null },
  { to: '/cluster', label: 'Cluster', capability: null },
  { to: '/tokens', label: 'Tokens', capability: 'tokens' },
] as const

export function Layout({ children }: { children: ReactNode }) {
  const visible = LINKS.filter(
    (link) => link.capability === null || bootstrap.capabilities[link.capability],
  )

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 200, breakpoint: 'xs' }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={4}>{bootstrap.title}</Title>
          <HealthDot />
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="md">
        <Stack gap="xs" component="nav">
          {visible.map((link) => (
            <Anchor key={link.to} component={Link} to={link.to} size="sm">
              {link.label}
            </Anchor>
          ))}
        </Stack>
      </AppShell.Navbar>

      <AppShell.Main>{children}</AppShell.Main>
    </AppShell>
  )
}
