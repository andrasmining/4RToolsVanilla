# 4RTools Vanilla 0.6.59

## Weight/Cart: keep filling after the first successful transfer

The v0.6.57 live debug log showed that the Cart was not actually full or rejecting items. One Use-stack transfer completed successfully, including a positively detected quantity dialog, and the next compacted first inventory slot was again positively classified as occupied. The maintenance pass then stopped only because 4RTools tried to rediscover the Cart's empty-slot lattice after the first item had already been added.

That assumption is removed.

- Cart drops now use a bounded deterministic set of safe points inside the already positively detected Cart panel body. Vanilla accepts a drop anywhere in that body; a particular Cart slot does not need to look empty.
- A populated Cart is allowed to cover the pale empty-slot lattice. The code does not re-require that lattice after each successful transfer.
- Fresh verified read-only **Cart weight increase** is now the authoritative transfer proof. The source slot is not required to look empty because Vanilla compacts the next stack into the first slot immediately.
- Carried-weight reduction remains corroborating evidence, but a delayed carried-weight observation no longer invalidates a transfer whose Cart-weight increase was verified.
- Quantity dialogs still require positive visual recognition before Enter, and any lingering/late quantity modal still fails closed.
- At/above 95% the existing capacity-aware Mastela/Peco rules remain unchanged, including the fixed 10000 Cart ceiling.

Offline regression coverage reproduces the populated-Cart condition and verifies that later transfers still have a safe Cart destination.

## One debug file per application start; 10 MiB hard cap

The old v0.6.57 debug file could contain many application restarts in one huge file. Debug logging is now process/session-owned from application startup rather than being tied to whichever UI initializes first.

- Every normal 4RTools process creates a new file immediately at startup:
  `debug-YYYYMMDD-HHmmss-fff-p<PID>.log`
- A later app restart never appends to the prior process's debug file.
- If two starts somehow collide on the same timestamp/PID token, a unique suffix is used rather than sharing a file.
- Every debug part is hard-capped at **10 MiB** and rotates to `-part02`, `-part03`, etc.
- The former fixed `debug.log`, when present from an older version, is archived/migrated once and is never used as the live log again.
- Reconnect/session, memory-access, updater-error and other application-managed `.log` files retain the same **10 MiB per-file hard cap** and bounded history.
- Oversized legacy logs are split into bounded archive parts during migration.
- COPY DEBUG LOG uses the current process's debug session rather than concatenating historical debug sessions.

Regression tests explicitly create two simulated app starts and verify that their log paths/content are isolated, and verify that oversized debug/session payloads cannot create a file above the configured cap.

## Recovery layout thread exception fixed

The supplied v0.6.57 log also contained repeated WinForms exceptions:

`SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize`

The Recovery pane could change SplitContainer orientation during a transient resize/layout state where one axis was only a few pixels wide. WinForms validates the old splitter distance inside the Orientation setter, so the setter itself could throw.

4RTools now sizes the SplitContainer first, defers orientation changes while either axis is only a transient layout sliver, and retries on the next normal layout event. The draggable Characters/Log divider remains intact.

## Smart Teleport: minimized client resolution

The same log showed repeated automatic Smart Teleport attempts failing before sending the hotkey because a minimized Vanilla main window reported a current client rectangle of 0x0.

Background Smart Teleport now:

- resolves the verified owned Vanilla main HWND by PID/class/title even while minimized;
- derives the last normal client capture size from `WINDOWPLACEMENT` and the real window style without restoring, moving or foregrounding the game;
- uses that derived size only for the existing background PrintWindow path;
- still requires a nonblank/usable capture before sending the teleport hotkey;
- still requires positive recognition of the expected warp popup before Enter;
- fails closed if ownership, geometry, capture, popup verification or background input is unavailable.

This preserves the rule that Smart Teleport must not pop a minimized gameplay client to the foreground merely to recover it.

## Validation

Before versioning this release, the full Windows pipeline passed:

- shipped Vanilla build-profile validation;
- complete Debug diagnostics/regression suite;
- Release build/package/smoke tests;
- native test-owned recovery checks;
- mock-data UI rendering/layout checks.

New regressions cover populated-Cart drop handling, per-start debug isolation and hard rotation, transient Recovery splitter geometry, and minimized background-teleport capture sizing.

The engineering runner cannot reproduce the user's live Gepard/Vanilla/RDP session. The Cart root cause is grounded in the supplied live v0.6.57 log, while the corrected subsequent multi-item transfer and minimized PrintWindow behavior remain the next live VPS validation boundary.
