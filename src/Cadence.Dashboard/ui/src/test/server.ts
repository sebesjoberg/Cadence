import { setupServer } from 'msw/node'

/** No default handlers: every test declares the exchange it is about. */
export const server = setupServer()
