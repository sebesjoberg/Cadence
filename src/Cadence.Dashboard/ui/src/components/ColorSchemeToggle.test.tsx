import { MantineProvider } from '@mantine/core'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { ColorSchemeToggle } from './ColorSchemeToggle'

function mount() {
  render(
    <MantineProvider defaultColorScheme="auto">
      <ColorSchemeToggle />
    </MantineProvider>,
  )
}

const button = () => screen.getByRole('button')

describe('ColorSchemeToggle', () => {
  it('cycles system, light and dark, and comes back round', async () => {
    mount()

    // `auto` is a real position, not a starting state to be escaped: a machine that flips to dark
    // in the evening should take the dashboard with it, which a two-way switch cannot express.
    expect(button()).toHaveAccessibleName(/following the system theme/i)

    await userEvent.click(button())
    expect(button()).toHaveAccessibleName(/light theme/i)

    await userEvent.click(button())
    expect(button()).toHaveAccessibleName(/dark theme/i)

    await userEvent.click(button())
    expect(button()).toHaveAccessibleName(/following the system theme/i)
  })

  it('puts the chosen scheme on the document, which is what themes the page', async () => {
    mount()

    await userEvent.click(button())
    await userEvent.click(button())

    expect(document.documentElement.getAttribute('data-mantine-color-scheme')).toBe('dark')
  })
})
