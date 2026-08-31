import type { Problem } from './problem'
import { ProblemError, UnauthenticatedError } from './problem'

export { ProblemError, UnauthenticatedError } from './problem'

// Fixed at compile time, exactly as CadenceApiDefaults fixes them: the bundle ships prebuilt
// inside a NuGet package, so there is no consumer build in which to bake a prefix.
const BASE = '/cadence'
const UI = `${BASE}/ui`
const LOGIN = `${BASE}/api/auth/login`
const SESSION_HEADER = 'X-Cadence-Session'
const COOKIE_SCHEME = 'CadenceCookie'
const PROBLEM_JSON = 'application/problem+json'

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const sends = body !== undefined && body !== null

  const response = await fetch(`${UI}${path}`, {
    method,
    credentials: 'same-origin',
    // SessionHeaderFilter demands this on every method, not only the writes: a cross-site form can
    // issue a request the ticket cookie authenticates, but it cannot set a header.
    headers: sends
      ? { [SESSION_HEADER]: '1', 'Content-Type': 'application/json' }
      : { [SESSION_HEADER]: '1' },
    body: sends ? JSON.stringify(body) : undefined,
  })

  if (response.status === 401) {
    // The challenge header separates the two cases. With it, auth_time is stale and one
    // re-authentication fixes it, which is why the token screen does not dead-end; without it,
    // there is simply no session yet.
    const stale = response.headers.get('WWW-Authenticate')?.includes(COOKIE_SCHEME) ?? false

    window.location.assign(stale ? `${LOGIN}?prompt=login` : LOGIN)

    throw new UnauthenticatedError(stale)
  }

  if (!response.ok) {
    throw await problemFrom(response)
  }

  return await readJson<T>(response)
}

/** A downloaded result: the bytes, and the name the server suggested for them. */
export interface Download {
  blob: Blob
  fileName: string | null
}

// Buffered into a Blob rather than pointed at with a plain <a href>, because SessionHeaderFilter
// demands X-Cadence-Session on every method and an anchor cannot set a header. Exempting the route
// would be the convenient fix and the wrong one: the header is what stops a cross-site page from
// issuing requests the ticket cookie authenticates. MaxResultBytes bounds what this holds.
async function download(path: string): Promise<Download> {
  const response = await fetch(`${UI}${path}`, {
    method: 'GET',
    credentials: 'same-origin',
    headers: { [SESSION_HEADER]: '1' },
  })

  if (response.status === 401) {
    const stale = response.headers.get('WWW-Authenticate')?.includes(COOKIE_SCHEME) ?? false

    window.location.assign(stale ? `${LOGIN}?prompt=login` : LOGIN)

    throw new UnauthenticatedError(stale)
  }

  if (!response.ok) {
    throw await problemFrom(response)
  }

  return {
    blob: await response.blob(),
    fileName: fileNameFrom(response.headers.get('Content-Disposition')),
  }
}

/** Reads the filename out of a Content-Disposition header, preferring the encoded form. */
export function fileNameFrom(header: string | null): string | null {
  if (!header) return null

  // filename* wins where both are present: it is the one that survived non-ASCII characters.
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(header)
  if (encoded) {
    try {
      return decodeURIComponent(encoded[1])
    } catch {
      // A malformed escape sequence is not worth failing a download over; fall through to plain.
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(header)

  return plain ? plain[1] : null
}

async function problemFrom(response: Response): Promise<ProblemError> {
  const document: Problem = response.headers.get('Content-Type')?.includes(PROBLEM_JSON)
    ? await response.json().catch(() => ({}))
    : {}

  return new ProblemError(
    response.status,
    document.title ?? response.statusText,
    document.detail ?? '',
    document.type ?? '',
  )
}

async function readJson<T>(response: Response): Promise<T> {
  if (response.status === 204) {
    return undefined as T
  }

  const text = await response.text()

  return (text === '' ? undefined : JSON.parse(text)) as T
}

/** The dashboard's only way out to the network. */
export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T>(path: string, body: unknown) => request<T>('POST', path, body),
  put: <T>(path: string, body: unknown) => request<T>('PUT', path, body),
  delete: <T>(path: string) => request<T>('DELETE', path),
  download,
}
