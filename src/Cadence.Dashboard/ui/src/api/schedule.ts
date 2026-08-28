import { parseTimeSpanMs } from './timespan'

// Mirrors Cadence.Scheduling.OverlapPolicy. A schedule that names neither leaves the job's own
// declaration standing, which is what the empty option in the form means.
export type OverlapPolicy = 'Skip' | 'AllowConcurrent'

export const OVERLAP_POLICIES: readonly OverlapPolicy[] = ['Skip', 'AllowConcurrent']

/**
 * The zones this browser knows, with `current` guaranteed present: the stored id came from the
 * server's host and need not be one this browser lists, and dropping it from the options would
 * silently rewrite the schedule on the next save.
 */
export function timeZoneIds(current: string): string[] {
  const supported =
    typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['UTC']

  return supported.includes(current) ? [...supported] : [current, ...supported]
}

/**
 * Whether a maximum duration is a TimeSpan at all. Only the unparseable case is caught here: a
 * non-positive one is refused by the server with prose naming the field, and that prose is better
 * than anything this form could write.
 */
export function isTimeSpan(value: string): boolean {
  try {
    parseTimeSpanMs(value)
    return true
  } catch {
    return false
  }
}
