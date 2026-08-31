/** Mirrors DashboardCapabilities: each flag is a service the container was given, not a setting. */
export interface DashboardCapabilities {
  scheduleWrite: boolean
  tokens: boolean
}

/** Mirrors DashboardBoot, which DashboardShell serialises into the document at map time. */
export interface DashboardBoot {
  title: string
  capabilities: DashboardCapabilities
}

declare global {
  interface Window {
    __cadence?: DashboardBoot
  }
}

const boot = window.__cadence

if (boot === undefined) {
  throw new Error(
    'window.__cadence is missing. DashboardShell substitutes it into index.html at map time, so ' +
      'its absence means the shell transform broke or something other than Cadence.Dashboard is ' +
      'serving this bundle. Failing here beats rendering an untitled application.',
  )
}

export const bootstrap: DashboardBoot = boot
