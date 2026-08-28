// Mirrors Cadence.Storage.PauseScope, a [Flags] enum whose two switches are independent on
// purpose: during an incident the usual thing to want is automatic work stopped while one job can
// still be started by hand. Kept out of any component so the banner and its control can share it
// without importing each other.

export type PauseScopeName = 'None' | 'Schedule' | 'Triggers' | 'All'

/** What a caller may ask the pause write to move to. */
export const PAUSE_SCOPES: readonly PauseScopeName[] = ['None', 'Schedule', 'Triggers', 'All']

// The server writes the enum's own ToString, which names All rather than the pair. Splitting on
// commas anyway costs nothing and keeps the read honest if a member is ever added.
function flags(scope: string): string[] {
  return scope.split(',').map((flag) => flag.trim())
}

/** Whether the tick loop is claiming no occurrences. */
export function pausesSchedule(scope: string): boolean {
  return flags(scope).some((flag) => flag === 'Schedule' || flag === 'All')
}

/** Whether manual and API runs are being refused. */
export function pausesTriggers(scope: string): boolean {
  return flags(scope).some((flag) => flag === 'Triggers' || flag === 'All')
}

/** The scope naming exactly the switches asked for -- the only place the two are recombined. */
export function scopeFrom(schedule: boolean, triggers: boolean): PauseScopeName {
  if (schedule && triggers) return 'All'
  if (schedule) return 'Schedule'
  if (triggers) return 'Triggers'
  return 'None'
}
