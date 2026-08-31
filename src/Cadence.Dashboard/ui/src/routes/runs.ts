import { createRoute } from '@tanstack/react-router'
import { RUN_STATUSES } from '../api/runStatus'
import type { RunStatus } from '../api/runStatus'
import { RunDetail } from '../screens/RunDetail'
import { Runs } from '../screens/Runs'
import { rootRoute } from './__root'

export type { RunStatus } from '../api/runStatus'

/** The /runs search params. Field names and semantics match RunQuery, one for one. */
export interface RunsSearch {
  job?: string
  status?: RunStatus
  instance?: string
  from?: string
  to?: string
  limit: number
  offset: number
}

const DEFAULT_LIMIT = 50

// RunEndpoints.MaxLimit: the server clamps rather than rejects an over-limit request, but the UI
// should never ask past its own cap either.
const MAX_LIMIT = 500

function asText(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

function asStatus(value: unknown): RunStatus | undefined {
  return typeof value === 'string' && (RUN_STATUSES as readonly string[]).includes(value)
    ? (value as RunStatus)
    : undefined
}

function asCount(value: unknown, fallback: number, max: number): number {
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed > 0 ? Math.min(Math.trunc(parsed), max) : fallback
}

export const runsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/runs',
  component: Runs,
  validateSearch: (search: Record<string, unknown>): RunsSearch => ({
    job: asText(search.job),
    status: asStatus(search.status),
    instance: asText(search.instance),
    from: asText(search.from),
    to: asText(search.to),
    limit: asCount(search.limit, DEFAULT_LIMIT, MAX_LIMIT),
    offset: asCount(search.offset, 0, Number.MAX_SAFE_INTEGER),
  }),
})

export const runDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/runs/$id',
  component: RunDetail,
})
