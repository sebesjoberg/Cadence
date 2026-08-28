import { describe, expect, it } from 'vitest'
import { PAUSE_SCOPES, pausesSchedule, pausesTriggers, scopeFrom } from './pause'

describe('pause scopes', () => {
  // The whole table, because the two switches being independent is what this module exists for:
  // a boolean-collapsed implementation gets three of these four rows wrong.
  it.each([
    [false, false, 'None'],
    [true, false, 'Schedule'],
    [false, true, 'Triggers'],
    [true, true, 'All'],
  ])('names the scope for schedule=%s triggers=%s', (schedule, triggers, expected) => {
    expect(scopeFrom(schedule, triggers)).toBe(expected)
  })

  it.each([
    ['None', false, false],
    ['Schedule', true, false],
    ['Triggers', false, true],
    ['All', true, true],
    // Enum.ToString names All rather than the pair, but Enum.TryParse accepts the list form.
    ['Schedule, Triggers', true, true],
  ])('reads %s back as the switches it closes', (scope, schedule, triggers) => {
    expect(pausesSchedule(scope)).toBe(schedule)
    expect(pausesTriggers(scope)).toBe(triggers)
  })

  it('round-trips every scope it offers', () => {
    for (const scope of PAUSE_SCOPES) {
      expect(scopeFrom(pausesSchedule(scope), pausesTriggers(scope))).toBe(scope)
    }
  })
})
