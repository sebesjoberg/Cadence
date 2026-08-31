import { MantineProvider } from '@mantine/core'
import { Notifications } from '@mantine/notifications'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Outlet, createRootRoute } from '@tanstack/react-router'
import { Layout } from '../components/Layout'

// An operator arriving at a screen -- by navigating to it, or by coming back to the tab -- is
// asking what is true now, so nothing is treated as fresh on arrival. A stale window would make
// both refetches silently conditional, which reads as a dashboard that ignores you. The cached
// answer still paints immediately while the refetch runs behind it, so this costs no flicker.
export const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: true, retry: 1, staleTime: 0 } },
})

function Shell() {
  return (
    <QueryClientProvider client={queryClient}>
      <MantineProvider defaultColorScheme="auto">
        <Notifications />
        <Layout>
          <Outlet />
        </Layout>
      </MantineProvider>
    </QueryClientProvider>
  )
}

export const rootRoute = createRootRoute({ component: Shell })
