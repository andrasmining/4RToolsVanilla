# 4RTools Vanilla 0.6.78

## Login and character-selection fixes

- Fix login stopping before credential entry when Vanilla selects a remembered
  username and suppresses its blinking caret. After clicking a freshly recognized
  field, press End to collapse the selection, then verify focus before typing.
- Identify the affected field and verification stage in debug logs without
  recording credential contents. Keep foreground ownership, cancellation, exact
  username and password-mask checks before submission.
- Recognize the character screen's selected-card shape, cyan border with a blue
  name strip, and page controls. Preserve the fifteen-card grid, unique selection,
  configured slot, keyboard-transition and expected gameplay identity checks.
  When the configured occupied slot is already selected, confirm it directly
  after a fresh check instead of moving away to an empty origin slot.
- Send navigation and editing keys with their proper extended scan codes, so
  arrows and End are not delivered as numeric-keypad keys.
- Retain one Enter for proxy/server selection, cursor parking after clicks and
  drags, the critical farming emergency stop, and temporary skill targeting with
  verified movement before SP-rest sitting. Existing saved settings are preserved.

## Validation

Live diagnosis reproduced the selected-username focus failure. With this fix,
the production credential flow verified and submitted saved credentials. The
correct configured character entered gameplay, its identity/HP/map/position were
verified, and the configured Autobattle hotkey produced verified movement.
No credential images or secret contents were exported.

Focused offline validation covers credentials, login recognition, service
selection, character-card layouts/navigation and stability. Windows release
validation also covers full Debug/Release regressions, native test-owned processes, isolated mock UI,
portable packaging and public updater discovery/download/staging. Pipeline
checks are separate from the live login evidence above.
