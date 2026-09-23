# 4RTools Vanilla 0.6.75

## Temporary skill clicks and SP recovery

- Fix temporary actions stopping before F1 with "captured target is not
  confidently visible" when the selected character's sprite or aura animates.
  The user-captured stationary point is now aligned using surrounding scene
  evidence, rather than requiring an identical 48-pixel character screenshot.
- Preserve saved F1/click/SP settings. Small camera translations after the
  linker's rest movement are followed using multiple surrounding anchors.
  Ambiguous scenery, changed window size or a point leaving the view stop input.
  This is a stationary captured point: capture again if the target moves or the
  camera rotates/zooms.
- Start the configured target delay after the skill hotkey finishes. Capture,
  focus and input delivery no longer consume that delay or the post-cast settle.
- Finish the pending skill click, settle, then click the configured nearby rest
  ground. Each SP-rest cycle requires at least one tile of fresh verified X/Y
  movement and fresh observations of settled movement before sending sit.
  Repeated cached coordinates cannot authorize sitting.
- Preserve SP hysteresis, bounded movement attempts, cancellation and serialized
  client ownership. Log rest/movement/sit/stand progress in the debug session.

## Validation and updates

Regression coverage includes animated targets, translated scenes, ambiguous and
changed views, target-click timing, repeated rest cycles, stale coordinates,
movement settling, timeouts and cancellation. Windows validation builds Debug
and Release, runs the full isolated suite, native test-owned recovery checks,
mock UI checks and portable-package smoke tests. Publication verifies the clean
main SHA, downloadable ZIP/checksum, public Latest release and real anonymous
updater discovery/download/staging.

Diagnosis uses the supplied screenshots, saved temporary settings and runtime
stop messages. Synthetic/offline checks are not live Vanilla/Gepard gameplay
validation. Existing configuration remains in the stable per-user data folder.
v0.6.74 can discover this release through CHECK FOR UPDATES.
