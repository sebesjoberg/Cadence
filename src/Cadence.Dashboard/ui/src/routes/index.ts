import { createRoute } from '@tanstack/react-router'
import type { JSX } from 'react'
import { bootstrap } from '../bootstrap'
import { Cluster } from '../screens/Cluster'
import { Tokens } from '../screens/Tokens'
import { rootRoute } from './__root'
import { jobDetailRoute, jobsRoute } from './jobs'
import { runDetailRoute, runsRoute } from './runs'

const child = (path: string, component: () => JSX.Element) =>
  createRoute({ getParentRoute: () => rootRoute, path, component })

// No writable token store means no /tokens tree to reach -- the route itself is absent, not
// merely its nav link, so a direct navigation cannot land on a screen the container never wired.
const tokensRoute = bootstrap.capabilities.tokens ? [child('/tokens', Tokens)] : []

export const routeTree = rootRoute.addChildren([
  jobsRoute,
  jobDetailRoute,
  runsRoute,
  runDetailRoute,
  child('/cluster', Cluster),
  ...tokensRoute,
])
