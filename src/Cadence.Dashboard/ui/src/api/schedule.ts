import { parseTimeSpanMs } from './timespan'

// Mirrors Cadence.Scheduling.OverlapPolicy. Naming neither leaves the job's declaration standing.
export type OverlapPolicy = 'Skip' | 'AllowConcurrent'

export const OVERLAP_POLICIES: readonly OverlapPolicy[] = ['Skip', 'AllowConcurrent']

/**
 * The zones this browser knows, with `current` guaranteed present: the stored id came from the
 * server's host, and dropping it would silently rewrite the schedule on the next save.
 */
export function timeZoneIds(current: string): string[] {
  const supported =
    typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['UTC']

  return supported.includes(current) ? [...supported] : [current, ...supported]
}

/**
 * Whether a maximum duration is a TimeSpan at all. Only the unparseable case: a non-positive one
 * is refused by the server with prose naming the field, which beats anything written here.
 */
export function isTimeSpan(value: string): boolean {
  try {
    parseTimeSpanMs(value)
    return true
  } catch {
    return false
  }
}
