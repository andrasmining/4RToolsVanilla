# 4RTools Vanilla 0.6.64

## Weight/Cart: HP damage guard while Autobattle is stopped

The live v0.6.63 test showed an important edge case: a character can satisfy the new stationary X/Y STOP check while standing in range of a monster such as a Geographer. In that situation the character may be stationary because Autobattle really stopped, but continuing to lose HP makes opening Inventory/Cart unsafe.

This release adds a cumulative HP safety guard to the complete stopped phase.

### STOP verification now checks HP as well as X/Y

The first fresh verified HP percentage observed at the beginning of the STOP sequence becomes the damage baseline.

During the existing STOP verification:

- STOP still requires **5 continuous seconds of unchanged X/Y** inside each **10-second** attempt;
- STOP still has at most **3 attempts**;
- the original HP baseline is retained across all STOP retries;
- if HP falls by **more than 10 percentage points** from that baseline, STOP verification aborts immediately;
- Inventory and Cart are not opened after that damage signal.

Exactly a 10-point drop does not cross the guard; the abort condition is strictly greater than 10 percentage points.

### HP remains guarded throughout Cart maintenance

After STOP is verified, the same original HP baseline remains active for the entire period in which Autobattle is intentionally OFF.

Fresh verified HP is re-checked during:

- panel opening/detection;
- category selection and confirmation waits;
- first-slot classification;
- quantity-dialog waits;
- Cart-weight verification waits;
- retry pauses;
- mouse source hold;
- cursor movement;
- destination/drop hold;
- post-release settle;
- panel closing.

The guard is therefore active even in the middle of drag-and-drop.

If HP falls by more than 10 percentage points, or fresh verified HP becomes unavailable after STOP:

1. further Cart input is cancelled;
2. the mouse is safely released if a drag was in progress;
3. 4RTools switches the input cancellation policy back to normal supervisor ownership;
4. Autobattle is resumed through the shared verified ResumeHotkey/X/Y routine;
5. any open Inventory/Cart panels are closed best-effort after resume;
6. the client is minimized;
7. Cart maintenance is re-armed for another attempt after about **60 seconds**.

Pure HP danger therefore behaves like a transient farming condition, not like a permanent Cart error/manual hold. If the emergency Autobattle resume itself cannot be verified, the client still fails closed for manual inspection rather than pretending it is safe.

### Farming-complete STOP uses the same HP guard

The final STOP used for the completed-farming hold is also HP-protected.

If HP drops by more than 10 percentage points while trying to stop at the DONE threshold, Autobattle is resumed and the completed hold is **not** armed. Another farming-complete STOP is delayed for about **60 seconds** rather than being retried immediately.

The DONE threshold remains:

- Cart **>=99%**
- carried weight **>=50%**

## Faster cursor travel, same deliberate grab/drop

The previous Cart hardening made the cursor movement itself unnecessarily slow. The live issue was reliability of the initial grab and final drop, not the transit across the screen.

The deliberate Cart drag now keeps the proven source/drop timing but accelerates only the travel phase:

- source hold before mouse-down: **250 ms** (unchanged);
- cursor travel: **6 deterministic steps x 35 ms** (about 210 ms total, down from 12 x 100 ms / about 1.2 s);
- destination hold before mouse-up: **300 ms** (unchanged);
- post-release settle: **500 ms** (unchanged).

Cancellation/HP checks also run during these holds and movement slices, so faster travel does not weaken the stopped-HP safety guard.

## Existing Cart behavior retained

- precision item-weight filling begins at **75% Cart usage**;
- Mastela Fruit / Use = **3 weight**;
- Peco Feather / Etc = **1 weight**;
- Cart maximum = **10000**;
- precision quantity is calculated from verified remaining Cart capacity;
- up to three slow/reliable transfer attempts remain available;
- pure transfer non-progress resumes Autobattle and retries after about 60 seconds;
- exact Cart 100% remains the Cart-full e-mail milestone;
- per-process timestamped debug files and the 10 MiB per-file hard log cap remain in place.

## Validation

Before versioning this release, the full Windows pipeline passed:

- shipped Vanilla build-profile validation;
- complete Debug diagnostics/regression suite;
- Release build/package/smoke tests;
- native test-owned recovery checks;
- mock-data UI rendering/layout validation.

New deterministic regressions cover:

- HP damage >10 percentage points aborting STOP verification immediately;
- exactly 10 percentage points remaining below the abort boundary;
- one cumulative HP baseline surviving across STOP retries;
- Cart HP threshold semantics;
- faster cursor-travel bounds while preserving deliberate source/destination holds;
- existing 5-second stationary STOP / 10-second attempts / 3-attempt budget;
- 75% precision filling, 99%+50% DONE threshold and one-minute retry policies.

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP combat timing. The next VPS Weight/Cart pass remains the live validation boundary for the HP-danger recovery path and the faster drag travel.
