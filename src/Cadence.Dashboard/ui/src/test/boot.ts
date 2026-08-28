import type { DashboardBoot } from '../bootstrap'

/** Installs a bootstrap object shaped like the one DashboardShell writes into the document. */
export function installBoot(boot?: Partial<DashboardBoot>) {
  window.__cadence = {
    title: 'Cadence Test',
    capabilities: { scheduleWrite: true, tokens: true },
    ...boot,
  }
}
