import { MantineProvider } from '@mantine/core'
import { Notifications } from '@mantine/notifications'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Outlet, createRootRoute } from '@tanstack/react-router'
import { Layout } from '../components/Layout'

// An operator returning to the tab is asking what is true now, so regaining focus refetches.
export const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: true, retry: 1, staleTime: 5_000 } },
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
