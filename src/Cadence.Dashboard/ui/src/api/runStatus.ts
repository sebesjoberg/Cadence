// Mirrors Cadence.Storage.RunStatus, not a CadenceApiResponses.cs record: RunSummaryResponse.Status
// is a plain string on the wire, but RunQuery.Statuses -- what the /runs filter sends back -- is
// restricted to these names. Kept out of routes/runs.ts so the filter component can import it
// without importing the route module itself, which would cycle back through screens/Runs.tsx.

export type RunStatus =
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'TimedOut'
  | 'Aborted'
  | 'Skipped'
  | 'Lost'

export const RUN_STATUSES: readonly RunStatus[] = [
  'Running',
  'Succeeded',
  'Failed',
  'TimedOut',
  'Aborted',
  'Skipped',
  'Lost',
]
