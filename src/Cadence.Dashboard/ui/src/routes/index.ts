import { createRoute } from '@tanstack/react-router'
import type { JSX } from 'react'
import { bootstrap } from '../bootstrap'
import { Cluster } from '../screens/Cluster'
import { JobDetail } from '../screens/JobDetail'
import { Jobs } from '../screens/Jobs'
import { Tokens } from '../screens/Tokens'
import { rootRoute } from './__root'
import { runDetailRoute, runsRoute } from './runs'

const child = (path: string, component: () => JSX.Element) =>
  createRoute({ getParentRoute: () => rootRoute, path, component })

// No writable token store means no /tokens tree to reach -- the route itself is absent, not
// merely its nav link, so a direct navigation cannot land on a screen the container never wired.
const tokensRoute = bootstrap.capabilities.tokens ? [child('/tokens', Tokens)] : []

export const routeTree = rootRoute.addChildren([
  child('/', Jobs),
  child('/jobs/$name', JobDetail),
  runsRoute,
  runDetailRoute,
  child('/cluster', Cluster),
  ...tokensRoute,
])
