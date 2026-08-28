import { createRoute } from '@tanstack/react-router'
import type { JSX } from 'react'
import { Cluster } from '../screens/Cluster'
import { JobDetail } from '../screens/JobDetail'
import { Jobs } from '../screens/Jobs'
import { RunDetail } from '../screens/RunDetail'
import { Runs } from '../screens/Runs'
import { Tokens } from '../screens/Tokens'
import { rootRoute } from './__root'

const child = (path: string, component: () => JSX.Element) =>
  createRoute({ getParentRoute: () => rootRoute, path, component })

export const routeTree = rootRoute.addChildren([
  child('/', Jobs),
  child('/jobs/$name', JobDetail),
  child('/runs', Runs),
  child('/runs/$id', RunDetail),
  child('/cluster', Cluster),
  child('/tokens', Tokens),
])
