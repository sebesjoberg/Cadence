import { Anchor, Breadcrumbs, Text } from '@mantine/core'
import { Link } from '@tanstack/react-router'

interface CrumbsProps {
  /** Where this screen sits under, as a label and the route to reach it. */
  parent: { label: string; to: string }
  /** This screen, which is where you already are, so it is not a link. */
  current: string
}

/**
 * Breadcrumbs rather than a back button, because a deep link is the case that matters: someone
 * pasted `/cadence/runs/{id}` into a browser with no history behind it, and a back button there
 * goes nowhere. A crumb resolves whether or not you arrived by navigating.
 */
export function Crumbs({ parent, current }: CrumbsProps) {
  return (
    <Breadcrumbs separator="/" mb="xs">
      <Anchor component={Link} to={parent.to} size="sm">
        {parent.label}
      </Anchor>
      <Text size="sm" c="dimmed" lineClamp={1}>
        {current}
      </Text>
    </Breadcrumbs>
  )
}
