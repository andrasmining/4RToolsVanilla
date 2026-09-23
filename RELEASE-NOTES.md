# 4RTools Vanilla 0.6.76

## Critical farming emergency stop

- Close only the affected client as soon as a fresh verified snapshot shows all
  three conditions: carried weight **over 50%**, SP **under 25%**, and HP
  **under 50%**. The guard checks every 500 ms independently of the configurable
  Cart/mail interval and remains active for enabled character rows even when
  reconnect supervision or Cart maintenance is OFF.
- Emergency closure takes priority over Cart damage-abort resume/retry, farming
  completion, teleport and recovery. It uses the existing Windows process handle
  pinned to that client's creation time, skips the ordinary graceful-close delay,
  and confirms exit. Healthy siblings remain untouched.
- Save an emergency hold before closing. STOP/START, settings edits, updates and
  application restart do not automatically relaunch or resume the held character.
  Inspect the character, then use **CLEAR EMERGENCY HOLD** on the Weight tab when
  ready. Ordinary Weight/Cart hold-clear does not clear emergency holds.
- Show the emergency status in the Weight and Recovery views. Record exact
  HP/SP/weight values, character/session identity and the close result in logs.
  Stale, missing, unverified or incoherent state cannot authorize a close.

## Investigation and validation

Recent logs showed Cart deferrals while reconnect supervision was OFF, and an
earlier damage-aborted Cart attempt that resumed Autobattle for a later retry.
Another attempt reached its transfer retry limit near Cart capacity. The existing
logs did not include enough HP/SP history to prove the exact death sequence.

Regression checks cover strict thresholds, freshness, ownership, immediate close,
cancellation, persistent holds and affected-client isolation. Windows validation
builds Debug/Release, runs the isolated suite and native test-owned process checks,
and validates mock UI, the portable package, checksums and public updater staging.
These are offline/native test-process checks, not live Vanilla/Gepard validation.

Includes the v0.6.75 temporary skill-click and verified move-before-sit fixes.
Existing user configuration stays in the stable per-user data directory.
