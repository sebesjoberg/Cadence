import { createRoute } from '@tanstack/react-router'
import type { JSX } from 'react'
import { Cluster } from '../screens/Cluster'
import { JobDetail } from '../screens/JobDetail'
import { Jobs } from '../screens/Jobs'
import { Tokens } from '../screens/Tokens'
import { rootRoute } from './__root'
import { runDetailRoute, runsRoute } from './runs'

const child = (path: string, component: () => JSX.Element) =>
  createRoute({ getParentRoute: () => rootRoute, path, component })

export const routeTree = rootRoute.addChildren([
  child('/', Jobs),
  child('/jobs/$name', JobDetail),
  runsRoute,
  runDetailRoute,
  child('/cluster', Cluster),
  child('/tokens', Tokens),
])
