# 4RTools Vanilla 0.6.66

## Independent Cart and Mail controls, completed

Each character has independent **Cart** and **Mail** switches in the Recovery roster and character editor. Shared Cart/e-mail masters, thresholds, categories, hotkeys and SMTP configuration remain on the Weight tab.

| Effective Cart maintenance | Character Mail | Behavior |
| --- | --- | --- |
| Off | On | Carried-weight warning at the configured threshold, for example 45%. |
| On | On | No carried-only or Cart-only warning. One DONE notification when Cart is at least 99% AND carried weight is at least 50%, after verified Autobattle STOP. |
| On | Off | Cart maintenance without notification. |
| Off | Off | Neither automatic Cart maintenance nor weight notification. |

The shared e-mail master must also be enabled for automatic mail. Disabling the shared Cart master makes Cart maintenance inactive, so enabled Mail uses the carried-weight threshold instead.

This release removes the early Cart-100%-only e-mail from v0.6.65: a full Cart alone still leaves carried capacity available. Combined completion retains the previously configured farming thresholds, Cart >=99% and carried >=50%; it does not require the character to reach 100% carried weight.

Automatic dispatch re-checks the current character binding and Cart/Mail settings immediately before sending. A queued completion cannot rely on a previously enabled Mail switch. DONE mail has a per-character in-flight reservation to prevent duplicate overlapping sends. An SMTP send already in progress cannot be recalled.

Mail-only character edits update the live runtime without cancelling Cart work or recovery. Other input-affecting edits still cancel safely; unchanged explicit settings applications retain their recovery-reset semantics. All Cart start/cancellation/manual-test checks now use the effective Cart switch rather than the legacy combined Weight flag. Existing profiles retain their legacy fallback unless explicit split switches were saved.

## HP safety and faster dragging retained

One cumulative HP baseline is retained from the beginning of Autobattle STOP verification through the stopped Cart operation. A drop of **more than 10 percentage points** aborts Cart work, including during drag/drop and waits. After verified resume through the shared Autobattle/X/Y routine, panels are closed best-effort, the client is minimized and Cart is retried after about **60 seconds**. Exactly 10 percentage points does not cross the threshold. Unavailable verified HP after STOP also aborts. Failure to verify the emergency resume remains a safety hold, not a false success.

STOP requires five continuous stationary seconds in a ten-second attempt window, with at most three attempts. The HP baseline is not reset between attempts. Farming-complete STOP uses the same damage check before arming its completed hold.

Cursor travel uses **6 steps at approximately 35 ms** (about 210 ms rather than the earlier 1.2 seconds). The existing 250 ms source-position settle, 300 ms destination hold and 500 ms post-release settle remain unchanged.

## Compact UI and earlier fixes retained

Cart/Mail explanations are hover tooltips or small info glyphs rather than persistent paragraphs. Save status is short. Compact roster policy columns, the Pwd header, HP/SP/Weight/Cart resource display and the draggable character/log divider are preserved.

Cart maintenance retains first-slot inventory classification, verified category changes, safe detected Cart destinations, positive quantity-dialog checks, delayed-progress verification and bounded transfer retries. Precision filling begins at 75% Cart usage, using Mastela Fruit weight 3 and Peco Feather weight 1 within the verified 10000 Cart capacity. Timestamped session logs retain their 10 MiB per-file cap.

## Validation

Before versioning, the feature commit 79e724d2ca085815960f5f19deb5d170c9360539 passed all 401 regression checks in both Debug and Release, build-profile validation, portable-package smoke tests, native test-owned recovery checks and all 25 mock-data UI cases. The Weight tab, independent character controls, Full-HD recovery layout and narrow enlarged-text layout screenshots were inspected.

Added coverage includes all 16 shared/character Cart/Mail combinations, combined-capacity boundaries and missing/unverified observations, explicit Cart ON over legacy Weight OFF, and runtime Mail-only edits preserving an active input lease while Cart edits still cancel it. Existing cancellation regression tests remain in force.

The release pipeline repeats validation, downloads and verifies the published ZIP/checksum and source identity, then exercises the installed-version updater. These checks do not reproduce the user's live Vanilla/Gepard client, actual game dragging or real SMTP delivery.
