import { ProblemError, UnauthenticatedError } from './problem'

/**
 * What a refused request says to the operator.
 *
 * The server's own prose is carried verbatim wherever it exists -- design plan 13.6's "0
 * registered job(s)" is the diagnosis for a dashboard-only replica, and a generic failure line
 * would throw the only signal away. A document with a `type` but no `detail` falls back to its
 * title; an empty-bodied 4xx (13.2) falls back to the caller's own line rather than to the
 * framework's bare statusText, which client.ts substitutes for a missing title.
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
