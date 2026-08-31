/** An RFC 9457 document, as `ProblemMapper` writes it. */
export interface Problem {
  type?: string
  title?: string
  detail?: string
  status?: number
}

/**
 * A refusal the server explained.
 *
 * `detail` is the server's own prose and is carried verbatim all the way to the operator: it names
 * the cause -- a job missing from this replica, a schedule someone else moved -- that a generic
 * failure message would throw away.
 */
export class ProblemError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail: string,
    readonly type: string,
  ) {
    super(detail || title)
    this.name = 'ProblemError'
  }
}

/** Nobody is signed in. The browser is already on its way to the sign-in route. */
export class UnauthenticatedError extends Error {
  constructor(readonly stale: boolean) {
    super(stale ? 'The session is too old to authorise this; signing in again.' : 'Not signed in.')
    this.name = 'UnauthenticatedError'
  }
}
