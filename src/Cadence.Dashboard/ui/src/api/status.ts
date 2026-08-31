/**
 * How a run status is coloured. A leaf module, like runStatus.ts, so a component importing it
 * never reaches back into a routing module -- that cycle has been broken once already.
 */
const STATUS_COLORS: Record<string, string> = {
  Running: 'blue',
  Succeeded: 'green',
  Failed: 'red',
  TimedOut: 'orange',
  Aborted: 'gray',
  Skipped: 'gray',
  Lost: 'red',
}

/** The badge colour for a status, falling back to grey for one this build does not know. */
export function statusColor(status: string): string {
  return STATUS_COLORS[status] ?? 'gray'
}
