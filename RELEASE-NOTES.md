# 4RTools Vanilla 0.6.78

## Remembered-username login fix

- Fix login stopping before credential entry when Vanilla selects a remembered
  username and suppresses its blinking caret. After clicking a freshly recognized
  field, press End to collapse the selection, then verify focus before typing.
- Identify the affected field and verification stage in debug logs without
  recording credential contents. Keep foreground ownership, cancellation, exact
  username and password-mask checks before submission.
- Retain one Enter for proxy/server selection, cursor parking after clicks and
  drags, the critical farming emergency stop, and temporary skill targeting with
  verified movement before SP-rest sitting. Existing saved settings are preserved.

## Validation

Live diagnosis reproduced the selected-username focus failure. With this fix,
the production credential flow verified and submitted saved credentials and
reached the game's server-selection screen. No credential images or secret
contents were exported.

Focused offline validation passed 76 cases across credentials, login recognition,
service selection and stability. Windows release validation also covers full
Debug/Release regressions, native test-owned processes, isolated mock UI,
portable packaging and public updater discovery/download/staging. Pipeline
checks are separate from the live login evidence above.
