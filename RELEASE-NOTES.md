# 4RTools Vanilla 0.6.67

## Recover a stalled launcher update without bypassing the launcher

The launcher can remain on its patch progress screen without a GAME START button while an existing Vanilla client still has the installation open. A frozen screen suggests a blocked update; it does not independently prove a file lock.

4RTools now recognizes the launcher's thin yellow progress bar and status area as an update state. Recovery requires the same verified launcher window/process to show no GAME START and no visible progress/status change for **60 continuous seconds**. Missing Start alone, unknown/blank/failed captures, capture gaps, changed windows/processes and normal visible update progress do not authorize closing clients.

Once a fresh confirmation and two matching process-identity snapshots establish the recovery conditions, the current startup/recovery operation retains its global lease and:

1. closes the same-installation launchers, including leftover patchers, so they cannot spawn a game during shutdown;
2. closes both verified Vanilla game clients from that installation (or the one remaining client), confirming every exit;
3. checks that no game/launcher process remains or appeared in that installation;
4. starts the configured **launcher again**, lets patching finish, and uses the verified GAME START path;
5. restores enabled characters sequentially through login, verified Autobattle movement and minimization.

Cold startup revisits earlier characters closed by the update reset before enabling continuous supervision. Ordinary disconnect and movement-stall recovery still leave healthy siblings untouched. Disabled profiles and existing Cart/manual/completed-farming holds are preserved.

## No direct-game fallback

Vanilla startup must use **Vanilla Launcher.exe** or **patcher.exe**. A saved game-executable path may resolve to an adjacent launcher. Without a launcher, startup reports a configuration error rather than directly launching Vanilla MMO.exe and bypassing updates.

## Ownership and bounded recovery

Process identity uses one limited-query Windows handle for the executable path and creation time, with no game-memory access and no alternate access after denial. Shutdown reuses the existing creation-time-pinned graceful-close/termination protocol. Another installation is not a reset target; unverified identities fail closed.

One launch invocation can perform at most one whole-installation update reset. A **ten-minute supervisor cooldown** prevents repeated client shutdowns across retries. Visible changing update progress may extend the normal launch wait, subject to a **ten-minute hard deadline**.

STOP, settings/generation changes, runtime/PID/session replacement and competing recovery ownership invalidate pending work. Automated Process.Start itself now runs under the supervisor ownership lock: cancellation cannot slip between the last check and starting the replacement launcher.

## Validation

The feature implementation and the final launcher-start ownership guard passed the complete Windows Debug and Release regression suites, Release package/smoke checks, native test-owned process lifecycle checks and all 25 mock-data UI cases before versioning.

New regressions cover progress-screen detection at six image scales, blank/ready/changing screens, continuous stall timing and observation gaps, PID/HWND/creation-time changes, one/two-client close ordering, leftover launchers, other-installation and unknown-identity rejection, process replacement during preflight/shutdown, STOP, competing ownership, cooldown and cancellation at the actual launcher-start boundary. Native inert-child tests additionally verify limited-query path/creation-time metadata.

The detector's progress-bar/footer predicates were also checked offline against the provided launcher screenshot. This is not a live Vanilla/Gepard/RDP patching test. Actual live update recovery remains unverified on the user's PC.

## Separate character-slot issue remains open

This release does **not** claim to fix the newly reported wrong-slot/Character Creation issue. The current keyboard selection routine still assumes a clamped grid instead of verifying the selected slot. The supplied screenshots show Character Creation over the selection screen, so they do not expose the actual 1–15 slot layout and selection marker. An unobscured character-selection capture is required to implement and validate a detected-slot replacement without inventing coordinates or UI state.

All Cart/Mail, HP safety, drag timing and log-rotation changes from v0.6.66 are retained.
