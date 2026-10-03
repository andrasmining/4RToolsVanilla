# 4RTools Vanilla 0.6.89

## Launcher updates and unattended Cart cleanup

- A verified launcher that remains open without GAME START for two minutes now triggers one coordinated update retry, including update-error screens without a progress bar. Same-installation patchers and both clients close with verified process identities and confirmed exits before the launcher restarts. The existing faster frozen-progress check, ten-minute reset cooldown, cancellation and sequential client recovery remain in effect.
- Cart quantity confirmation no longer requires numeric OCR merely because Cart usage crossed 75%. At any fill level, a fresh coherent snapshot proving the entire carried inventory fits permits one confirmation of the untouched default amount. Capacity is checked again immediately before input, and a verified Cart-weight increase is still required. Capacity-limited transfers retain conservative count bounds and exact numeric readback.
- Quantity cleanup allows bounded retries only while the originally recognized dialog remains verified. Losing the number's blue selection is not proof that the dialog closed. After transfer or cancellation, two fresh owned-window observations must prove actual modal disappearance before cleanup/resume continues. Missing, stale, changed or ambiguous evidence still withholds input.
- Recovery/debug logs now include carried weight, free Cart capacity and specific quantity-dismissal evidence.

## Validation of this update

Regression cases cover the reported remaining carried weight of 329 with Cart weight 7632/10000, high-fill whole-inventory capacity proof, delayed dialog closure, unselected surviving modals, bounded Escape retries, cancellation/ownership changes, launcher errors without progress, changing progress and the two-minute timeout boundary. Windows gates build Debug and Release, run the complete isolated diagnostics suite, native test-owned process checks, mock UI and portable-package smoke tests, and public/legacy updater discovery/download/staging checks. These pipeline checks do not establish live Vanilla gameplay or a real server patch.

The farming calculator and persistent user settings from 0.6.88 are retained.

## Resettable farming calculator

The Weight tab contains an independent calculator for each of the two active characters: editable item name, zeny per item, count, automatic Cart source and weight per item. Total value and zeny/hour use the configured prices and active timer. No monster or item list is hard-coded.

START / RESUME continues the same run. PAUSE freezes the monitor timer and automatic counting, without stopping gameplay, and unlocks the rows for manual corrections or additional untransferred loot. RESET clears counts, elapsed time and unassigned weight, preserves item definitions and immediately starts a new run. Empty carried farming loot provides the cleanest baseline. Definitions and session totals are kept in the stable application-data folder.

Automatic quantities are derived only from transfers already proven by the existing Cart-weight verification. A configured category/weight mapping is a user assumption, not visual or memory-based item identification. Use one automatic row per category; for mixed items in the same category use a combined average value (an estimate), or enter exact item counts manually while paused. Unmapped/non-divisible weight remains explicitly unassigned. Manual final-inventory additions should stay paused to avoid counting them again when they are later transferred.

## Reliability improvements

- Reset, pause/resume and edited definitions invalidate in-flight accounting tickets. A late transfer cannot enter a new run or be counted twice. Other characters remain independent.
- Failed calculator writes preserve the previously saved rows, counts and timer. Calculator storage errors never interrupt the existing Cart cleanup/resume path; incomplete totals receive a visible warning. An unreadable/corrupt calculator file is preserved and disables only the calculator, not game supervision.
- Decimal point/comma prices are read without interpreting the decimal point as a thousands separator. Editing another field no longer rounds a high-precision price. Enter prices without thousands separators.
- Pausing refreshes the saved counts immediately, including a transfer arriving between UI ticks, so subsequent manual edits cannot overwrite it with a stale displayed count.
- Rebuilt calculator cards dispose their old controls rather than retaining hidden resources.

## Validation

Windows gates run Debug and Release builds, the full isolated diagnostics suites (including reset/pause/resume, stale/duplicate transfers, character isolation, persistence failures, decimal input and paused UI edits), native test-owned process checks, portable-package smoke tests, mock UI rendering, and real public release updater discovery/download/staging verification. These checks are not live Vanilla/Gepard gameplay or an in-place update on the user's VPS.
