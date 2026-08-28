import type { RouterHistory } from '@tanstack/react-router'
import { RouterProvider, createRouter } from '@tanstack/react-router'
import { routeTree } from './routes'

// CadenceApiDefaults.BasePath, on the client. The server mounts every route under it and the shell
// bakes it into the bundle's asset URLs, so a router that disagreed would write links to nowhere.
const BASEPATH = '/cadence'

/** Takes a history so a test can drive the route tree without a browser. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, basepath: BASEPATH, ...(history ? { history } : {}) })
}

export const router = createAppRouter()

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

export function App() {
  return <RouterProvider router={router} />
}
