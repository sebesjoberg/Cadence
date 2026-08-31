import { AppShell, Container, Group, NavLink, Stack, Title } from '@mantine/core'
import { Link, useRouterState } from '@tanstack/react-router'
import type { ReactNode } from 'react'
import { bootstrap } from '../bootstrap'
import { ColorSchemeToggle } from './ColorSchemeToggle'
import { HealthDot } from './HealthDot'
import { RefreshControl } from './RefreshControl'

// A link is rendered only where the route behind it was mounted, which is what the capability
// flags record: no writable token store means no /tokens tree to reach.
const LINKS = [
  { to: '/', label: 'Jobs', capability: null },
  { to: '/runs', label: 'Runs', capability: null },
  { to: '/cluster', label: 'Cluster', capability: null },
  { to: '/tokens', label: 'Tokens', capability: 'tokens' },
] as const

// "/" would otherwise prefix-match every route and light up the whole nav.
function isActive(pathname: string, to: string): boolean {
  return to === '/' ? pathname === '/' : pathname.startsWith(to)
}

export function Layout({ children }: { children: ReactNode }) {
  const pathname = useRouterState({ select: (state) => state.location.pathname })

  const visible = LINKS.filter(
    (link) => link.capability === null || bootstrap.capabilities[link.capability],
  )

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 220, breakpoint: 'xs' }} padding="lg">
      <AppShell.Header>
        <Group h="100%" px="lg" justify="space-between">
          <Title order={4}>{bootstrap.title}</Title>
          <Group gap="md" wrap="nowrap">
            <RefreshControl />
            <ColorSchemeToggle />
            <HealthDot />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="sm">
        <Stack gap={4} component="nav">
          {visible.map((link) => (
            <NavLink
              key={link.to}
              component={Link}
              to={link.to}
              label={link.label}
              active={isActive(pathname, link.to)}
            />
          ))}
        </Stack>
      </AppShell.Navbar>

      <AppShell.Main>
        <Container size="xl" px={0}>
          {children}
        </Container>
      </AppShell.Main>
    </AppShell>
  )
}
