import { ActionIcon, Tooltip, useMantineColorScheme } from '@mantine/core'

const NEXT = { auto: 'light', light: 'dark', dark: 'auto' } as const

const FACE = {
  auto: { glyph: '◐', label: 'Following the system theme' },
  light: { glyph: '☀', label: 'Light theme' },
  dark: { glyph: '☾', label: 'Dark theme' },
} as const

/**
 * Light, dark, or whatever the operating system says. `auto` is kept as a real third position
 * rather than being collapsed into a two-way switch: a machine that flips to dark in the evening
 * should take the dashboard with it, and there is no way to ask for that from a binary toggle.
 *
 * Mantine persists the choice per browser; the inline script in index.html applies it before the
 * bundle loads, so a dark-mode operator never gets a white flash on the way in.
 */
export function ColorSchemeToggle() {
  const { colorScheme, setColorScheme } = useMantineColorScheme()
  const face = FACE[colorScheme]

  return (
    <Tooltip label={`${face.label} — click to change`}>
      <ActionIcon
        variant="subtle"
        aria-label={`Theme: ${face.label}`}
        onClick={() => setColorScheme(NEXT[colorScheme])}
      >
        {face.glyph}
      </ActionIcon>
    </Tooltip>
  )
}
