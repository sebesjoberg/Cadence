// Transcribed field for field from src/Cadence.Api/CadenceApiResponses.cs. Guid and DateTimeOffset
// arrive as strings; so does TimeSpan, which System.Text.Json writes as "hh:mm:ss".

export interface JobSummaryResponse {
  name: string
  cron: string | null
  timeZone: string | null
  enabled: boolean
  allowedTriggers: string
  nextOccurrenceUtc: string | null
  lastRun: RunSummaryResponse | null
}

export interface JobDetailResponse {
  job: JobSummaryResponse
  overlap: string | null
  maxDuration: string | null
  settings: Record<string, string>
  recentRuns: RunSummaryResponse[]
}

export interface RunSummaryResponse {
  runId: string
  jobName: string
  status: string
  trigger: string
  instanceId: string
  scheduledForUtc: string | null
  startedAtUtc: string
  completedAtUtc: string | null
  duration: string | null
  error: string | null
}

export interface RunDetailResponse {
  run: RunSummaryResponse
  log: LogEntryResponse[]
  result: JobResultResponse | null
}

export interface JobResultResponse {
  contentType: string
  fileName: string | null
  length: number
  createdAtUtc: string
  expiresAtUtc: string
}

export interface LogEntryResponse {
  timestampUtc: string
  message: string
}

export interface RunPageResponse {
  runs: RunSummaryResponse[]
  limit: number
  offset: number
}

export interface TriggerResponse {
  runId: string
  jobName: string
  instanceId: string
}

export interface PauseResponse {
  scope: string
  reason: string | null
  setBy: string | null
  setAtUtc: string | null
}

export interface PauseRequest {
  scope: string
  reason: string | null
}

export interface StorageHealthResponse {
  status: string
  checks: StorageCheckResponse[]
}

export interface StorageCheckResponse {
  name: string
  status: string
  description: string | null
  error: string | null
  duration: string
}

export interface ApiTokenResponse {
  id: string
  name: string
  fingerprint: string
  scope: string
  createdAtUtc: string
  createdBy: string | null
  expiresAtUtc: string | null
}

export interface ApiTokenCreatedResponse {
  id: string
  name: string
  fingerprint: string
  scope: string
  createdAtUtc: string
  expiresAtUtc: string | null
  token: string
}

export interface ApiTokenRequest {
  name: string | null
  scope: string | null
  expiresAtUtc: string | null
}

export interface AuthMeResponse {
  kind: string
  name: string | null
  subject: string | null
  scope: string | null
}

export interface ScheduleResponse {
  jobName: string
  cronExpression: string
  timeZoneId: string
  enabled: boolean
  overlap: string | null
  maxDuration: string | null
  settings: Record<string, string>
  version: number
}

export interface InstancesResponse {
  instances: InstanceResponse[]
  heartbeatTimeout: string
}

export interface InstanceResponse {
  instanceId: string
  machineName: string
  processId: number
  assemblyVersion: string | null
  startedAtUtc: string
  lastHeartbeatUtc: string
}

export interface ScheduleWriteRequest {
  cronExpression: string
  timeZoneId: string
  enabled: boolean
  overlap: string | null
  maxDuration: string | null
  settings: Record<string, string> | null
  version: number
}
