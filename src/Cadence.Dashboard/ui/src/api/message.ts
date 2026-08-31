import { ProblemError, UnauthenticatedError } from './problem'

/**
 * What a refused request says to the operator. The server's prose is verbatim wherever it exists
 * -- 13.6's "0 registered job(s)" is the diagnosis for a dashboard-only replica. An empty-bodied
 * 4xx (13.2) falls back to the caller's line, not to the statusText client.ts substitutes.
 */
export function problemMessage(error: unknown, fallback: string): string {
  if (error instanceof UnauthenticatedError) {
    return error.message
  }

  if (error instanceof ProblemError) {
    if (error.detail) return error.detail
    if (error.type) return error.title
  }

  return fallback
}
