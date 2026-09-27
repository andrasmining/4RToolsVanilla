# 4RTools Vanilla 0.6.82

## Recovery on slower PCs

- Wait for the actual interactive game window through the full 60-second startup
  budget. Splash/helper windows no longer prematurely end this wait and leave
  game readiness to the shorter foreground-acquisition timeout.
- Require confirmed exit of any retained failed client before another startup
  launch. A failed close retries with backoff without creating a duplicate client.
- Keep established clients under X/Y supervision when minimized capture fails.
  A timeout cannot change a healthy session into startup or authorize login and
  Autobattle input from an old launch timestamp.
- After sustained movement failure and unavailable minimized capture, diagnose
  only the affected screen under the shared input lock. Confirm exact terminal
  dialogs with two fresh observations; send no dismissal or gameplay input.
  Preserve the existing movement deadline, healthy siblings, cancellation,
  emergency/Cart holds and confirmed server-outage retry interval.
- Add bounded per-client health diagnostics showing sample freshness, movement
  timer, capture errors and reasons recovery is suppressed. Ordinary recovery
  captures no longer flood the log with misleading TELEPORT binding messages.

This release retains the Cart quantity-reader and confirmation fixes in 0.6.81.

## Evidence and validation

The supplied other-PC 0.6.80 logs show repeated minimized-capture failures and a
capture timeout incorrectly changing Online to WaitingForWindow. The recorded
startup succeeded; the logs contain no recognized disconnect dialog. A persisted
critical-farming emergency hold blocked one character, and supervision was
explicitly stopped from 10:04:43 through the end of the supplied log. These are
separate causes and remain visible in the recovery diagnostics.

Regression coverage uses fake clocks/processes and isolated test-owned windows.
Windows release gates cover Debug/Release tests, native process checks, mock UI,
portable packaging and public updater discovery/download/staging. This does not
claim live reproduction of the other computer's disconnect popup.
