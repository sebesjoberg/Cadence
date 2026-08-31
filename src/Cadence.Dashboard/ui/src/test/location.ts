import { vi } from 'vitest'

// jsdom makes every member of Location non-configurable, so a spy on `assign` is impossible and
// the whole property on `window` is what has to be replaced.
const real = Object.getOwnPropertyDescriptor(window, 'location')!
const href = window.location.href

export function stubLocation() {
  const assign = vi.fn()

  Object.defineProperty(window, 'location', {
    configurable: true,
    value: { href, origin: new URL(href).origin, assign },
  })

  return assign
}

export function restoreLocation() {
  Object.defineProperty(window, 'location', real)
}
