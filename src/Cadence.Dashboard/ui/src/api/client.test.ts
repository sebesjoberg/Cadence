import { HttpResponse, http } from 'msw'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { restoreLocation, stubLocation } from '../test/location'
import { server } from '../test/server'
import { api } from './client'
import { ProblemError, UnauthenticatedError } from './problem'

describe('api client', () => {
  let assign: ReturnType<typeof stubLocation>

  beforeEach(() => {
    assign = stubLocation()
  })

  afterEach(() => {
    restoreLocation()
  })

  it('sends the session header on every method', async () => {
    const seen: Record<string, string | null> = {}
    const record =
      (method: string) =>
      ({ request }: { request: Request }) => {
        seen[method] = request.headers.get('X-Cadence-Session')

        return new HttpResponse(null, { status: 204 })
      }

    server.use(
      http.get('/cadence/ui/probe', record('GET')),
      http.post('/cadence/ui/probe', record('POST')),
      http.put('/cadence/ui/probe', record('PUT')),
      http.delete('/cadence/ui/probe', record('DELETE')),
    )

    await api.get('/probe')
    await api.post('/probe', { scope: 'All' })
    await api.put('/probe', { scope: 'All' })
    await api.delete('/probe')

    expect(seen).toEqual({ GET: '1', POST: '1', PUT: '1', DELETE: '1' })
  })

  it('targets the operator tree and sends a json body', async () => {
    let body: unknown = null
    let contentType: string | null = null

    server.use(
      http.put('/cadence/ui/pause', async ({ request }) => {
        body = await request.json()
        contentType = request.headers.get('Content-Type')

        return new HttpResponse(null, { status: 204 })
      }),
    )

    await api.put('/pause', { scope: 'All', reason: 'deploy' })

    expect(body).toEqual({ scope: 'All', reason: 'deploy' })
    expect(contentType).toContain('application/json')
  })

  it('redirects to login on 401', async () => {
    server.use(http.get('/cadence/ui/jobs', () => new HttpResponse(null, { status: 401 })))

    await expect(api.get('/jobs')).rejects.toBeInstanceOf(UnauthenticatedError)
    expect(assign).toHaveBeenCalledWith('/cadence/api/auth/login')
  })

  it('asks for a fresh login when the ticket is stale', async () => {
    server.use(
      http.post(
        '/cadence/ui/tokens',
        () =>
          new HttpResponse(null, { status: 401, headers: { 'WWW-Authenticate': 'CadenceCookie' } }),
      ),
    )

    await expect(api.post('/tokens', {})).rejects.toBeInstanceOf(UnauthenticatedError)
    expect(assign).toHaveBeenCalledWith('/cadence/api/auth/login?prompt=login')
  })

  it('turns a problem document into a typed error', async () => {
    server.use(
      http.post('/cadence/ui/jobs/x/trigger', () =>
        HttpResponse.json(
          {
            type: 'urn:cadence:problem:job-not-found',
            title: 'Job not found',
            detail: "no job named 'x' is registered in this instance (0 jobs registered)",
          },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    const error = await api.post('/jobs/x/trigger', null).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ProblemError)
    expect(error).toMatchObject({
      status: 404,
      title: 'Job not found',
      type: 'urn:cadence:problem:job-not-found',
      detail: "no job named 'x' is registered in this instance (0 jobs registered)",
    })
    // The server's prose is the diagnosis, so it is also what the message carries.
    expect((error as Error).message).toContain('0 jobs registered')
    expect(assign).not.toHaveBeenCalled()
  })

  it('still raises a typed error when the failure carries no problem document', async () => {
    server.use(http.get('/cadence/ui/jobs', () => new HttpResponse('nope', { status: 503 })))

    const error = await api.get('/jobs').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ProblemError)
    expect(error).toMatchObject({ status: 503, detail: '' })
  })

  it('resolves undefined for an empty response', async () => {
    server.use(http.delete('/cadence/ui/tokens/1', () => new HttpResponse(null, { status: 204 })))

    await expect(api.delete('/tokens/1')).resolves.toBeUndefined()
  })

  it('parses a json response body', async () => {
    server.use(
      http.get('/cadence/ui/health/storage', () =>
        HttpResponse.json({ status: 'Healthy', checks: [] }),
      ),
    )

    await expect(api.get('/health/storage')).resolves.toEqual({ status: 'Healthy', checks: [] })
  })
})
