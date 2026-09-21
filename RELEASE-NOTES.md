# 4RTools Vanilla 0.6.69

## Server-down detection and 15-minute checks

The exact **Server Closed.(1)** message now enters a dedicated outage state after
two fresh matching observations. Recognition finds the Message form and its text;
it does not click a fixed screen location or treat the separate Please wait...
dialog as proof of an outage.

Pending accounts share one retry schedule. After confirmed downtime, 4RTools
waits **15 minutes** before trying the ordinary launcher/login recovery flow.
Repeated failures keep that fixed interval rather than switching to faster
retries or increasing toward an hour. Input/recovery ownership is released while
waiting, and healthy clients continue normally.

Only the recovering account's freshly verified gameplay identity clears the
outage state. Missing captures, unknown dialogs, a vanished popup or a healthy
sibling's existing gameplay do not prove that the failed recovery succeeded.
STOP and ownership/configuration changes cancel stale pending work. The normal
recovery status and log show the next check.

Generic failures outside confirmed server downtime keep their existing
exponential backoff. Launcher updates, verified service/credential/character
selection, post-login movement checks and healthy-client isolation remain in force.
There are no direct game-server protocol requests or game-file changes.

## Validation

Full local Debug and Release suites passed with zero failures, including eight
visual-recognition groups and 31 outage policy/supervisor regressions. Coverage
includes moved/scaled and softened dialogs, the accompanying Please wait form,
wrong-message rejection, fixed retry timing, shared ownership, cancellation,
healthy-client isolation and recovery success. Four native test-owned process
checks, 27 mock UI cases and portable OCR/smoke validation also passed. The seven
existing compiler/reference warnings are unchanged.

Publication additionally verifies the private Latest release, exact tag/source
commit, downloaded asset checksums and the application's authenticated update
discovery/download/staging path. It does not install or launch the normal app.

Recognition fixtures are generated from the supplied screenshot's visible form
and text; they are not captured from a live client. No live Vanilla/Gepard server
outage/reconnection test, desktop interaction or user installation was performed.

The private repository and local build/update delivery introduced in v0.6.68 are
retained. GitHub Actions remains disabled.
