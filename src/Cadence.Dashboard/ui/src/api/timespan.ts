// System.Text.Json writes a TimeSpan using the constant ("c") format: "[-][d.]hh:mm:ss[.fffffff]".
// InstancesResponse.heartbeatTimeout and StorageCheckResponse.duration both arrive this way.
const PATTERN = /^(-)?(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?$/

/** Parses a .NET TimeSpan wire string into milliseconds. */
export function parseTimeSpanMs(value: string): number {
  const match = PATTERN.exec(value)

  if (!match) {
    throw new Error(`not a TimeSpan: '${value}'`)
  }

  const [, negative, days, hours, minutes, seconds, fraction] = match
  const wholeSeconds =
    ((Number(days ?? 0) * 24 + Number(hours)) * 60 + Number(minutes)) * 60 + Number(seconds)
  const totalMs = wholeSeconds * 1000 + (fraction ? Number(`0.${fraction}`) * 1000 : 0)

  return negative ? -totalMs : totalMs
}
