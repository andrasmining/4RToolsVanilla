# 4RTools Vanilla 0.6.86

## Memory monitoring and foreground recovery

- Remove background screenshots from recovery, Smart Teleport, launcher checks
  and discovery. Background clients are monitored through fresh read-only memory
  coordinates. Remove the obsolete continuous visual-monitoring switch.
- Diagnose a stalled client only after acquiring the shared input lock and
  activating that client. Confirmed logout/disconnect screens can restart it
  immediately; healthy siblings remain untouched.
- Recover stationary gameplay with foreground Smart Teleport and the configured
  Autobattle resume hotkey when needed. Recheck movement before inputs and stop
  the sequence immediately when fresh coordinates show movement.
- Keep bounded recovery attempts, verified warp-popup confirmation, the configured
  no-movement restart deadline, intentional farming holds, and cancellation on
  STOP or ownership changes. Healthy existing-client adoption stays memory-only.

## Validation

Windows release gates exercise movement-only supervision, foreground ownership,
terminal-screen recovery, cancellation, popup verification and existing regressions.
They also run Debug/Release builds, isolated mock UI and test-owned process checks,
portable packaging and public/legacy updater discovery, download and staging.
These checks do not operate live game clients or send real e-mail.
