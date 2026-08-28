// Mirrors Cadence.Storage.PauseScope. The two switches are independent on purpose: during an
// incident the usual thing to want is automatic work stopped while one job can still be run by hand.

export type PauseScopeName = 'None' | 'Schedule' | 'Triggers' | 'All'

export const PAUSE_SCOPES: readonly PauseScopeName[] = ['None', 'Schedule', 'Triggers', 'All']

// ToString names All rather than the pair, but splitting on commas keeps this honest if a
// member is ever added.
function flags(scope: string): string[] {
  return scope.split(',').map((flag) => flag.trim())
}

export function pausesSchedule(scope: string): boolean {
  return flags(scope).some((flag) => flag === 'Schedule' || flag === 'All')
}

export function pausesTriggers(scope: string): boolean {
  return flags(scope).some((flag) => flag === 'Triggers' || flag === 'All')
}

/** The only place the two switches are recombined. */
export function scopeFrom(schedule: boolean, triggers: boolean): PauseScopeName {
  if (schedule && triggers) return 'All'
  if (schedule) return 'Schedule'
  if (triggers) return 'Triggers'
  return 'None'
}
