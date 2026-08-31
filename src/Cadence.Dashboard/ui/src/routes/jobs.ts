import { createRoute } from '@tanstack/react-router'
import { JobDetail } from '../screens/JobDetail'
import { Jobs } from '../screens/Jobs'
import { rootRoute } from './__root'

export const jobsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: Jobs,
})

export const jobDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/jobs/$name',
  component: JobDetail,
})
