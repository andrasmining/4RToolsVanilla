# 4RTools Vanilla 0.6.77

## Login and mouse confirmation fixes

- Accept the client's current proxy and game-server selections with one Enter
  in the intended active window. Remove the repeated Tokyo name/highlight checks
  that were blocking recovery. Retain configured loading delays, cancellation,
  and the exact server-outage check before Enter.
  Saved proxy preferences remain visible for compatibility; login uses the
  selection already present in the client.
- Move the pointer away after clicks and drags, before checking the result.
  Shared login, character-selection, temporary-action and Cart input paths park
  clear of the affected controls. Launcher actions do likewise while their window
  remains active. Release held buttons even if an operation is cancelled.
- Fix login detection mistaking the inner and outer borders of one form for
  multiple forms. This reproduces the dominant overnight login error from the
  debug log. Separate forms and incorrect service names remain rejected.
- Keep the v0.6.76 critical farming emergency stop and v0.6.75 temporary skill
  click / verified move-before-sit fixes. Existing user data stays in its stable
  per-user directory.

## Validation

Regression coverage includes scaled beveled login forms, one Enter per service
step, cancellation/focus failures, cursor exclusion geometry and release/parking
order. Windows validation builds Debug and Release, runs isolated regressions,
native test-owned process checks, mock UI and portable-package checks, and public
updater discovery/download/staging. These checks do not establish live
Vanilla/Gepard success.
