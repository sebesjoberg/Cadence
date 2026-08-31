import { api } from './client'
import type { ScheduleResponse, ScheduleWriteRequest } from './types'

/**
 * Pauses or resumes one job. `Enabled` is read by the tick loop alone, so a paused job stops
 * claiming occurrences while a manual trigger still runs it -- the same shape the cluster-wide
 * Schedule switch has, narrowed to a single job.
 *
 * The read is not optional. `version` and `settings` both carry absent-semantics on the server and
 * they resolve oppositely -- an absent version is refused, absent settings are preserved -- so this
 * loads the row and sends real values for both rather than depending on either rule.
 */
export async function setJobEnabled(jobName: string, enabled: boolean): Promise<ScheduleResponse> {
  const path = `/jobs/${encodeURIComponent(jobName)}/schedule`
  const current = await api.get<ScheduleResponse>(path)

  const write: ScheduleWriteRequest = {
    cronExpression: current.cronExpression,
    timeZoneId: current.timeZoneId,
    enabled,
    overlap: current.overlap,
    maxDuration: current.maxDuration,
    settings: current.settings,
    version: current.version,
  }

  return api.put<ScheduleResponse>(path, write)
}

/** Where a machine starts this job: the API tree, which records the run as `Api`, not `Manual`. */
export function machineTriggerUrl(jobName: string): string {
  return `${window.location.origin}/cadence/api/jobs/${encodeURIComponent(jobName)}/trigger`
}
