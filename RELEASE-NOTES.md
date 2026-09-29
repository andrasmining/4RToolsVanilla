# 4RTools Vanilla 0.6.88

## Resettable farming calculator

The Weight tab contains an independent calculator for each of the two active characters: editable item name, zeny per item, count, automatic Cart source and weight per item. Total value and zeny/hour use the configured prices and active timer. No monster or item list is hard-coded.

START / RESUME continues the same run. PAUSE freezes the monitor timer and automatic counting, without stopping gameplay, and unlocks the rows for manual corrections or additional untransferred loot. RESET clears counts, elapsed time and unassigned weight, preserves item definitions and immediately starts a new run. Empty carried farming loot provides the cleanest baseline. Definitions and session totals are kept in the stable application-data folder.

Automatic quantities are derived only from transfers already proven by the existing Cart-weight verification. A configured category/weight mapping is a user assumption, not visual or memory-based item identification. Use one automatic row per category; for mixed items in the same category use a combined average value (an estimate), or enter exact item counts manually while paused. Unmapped/non-divisible weight remains explicitly unassigned. Manual final-inventory additions should stay paused to avoid counting them again when they are later transferred.

## Reliability improvements

- Reset, pause/resume and edited definitions invalidate in-flight accounting tickets. A late transfer cannot enter a new run or be counted twice. Other characters remain independent.
- Failed calculator writes preserve the previously saved rows, counts and timer. Calculator storage errors never interrupt the existing Cart cleanup/resume path; incomplete totals receive a visible warning. An unreadable/corrupt calculator file is preserved and disables only the calculator, not game supervision.
- Decimal point/comma prices are read without interpreting the decimal point as a thousands separator. Editing another field no longer rounds a high-precision price. Enter prices without thousands separators.
- Rebuilt calculator cards dispose their old controls rather than retaining hidden resources.

## Validation

Windows gates run Debug and Release builds, the full isolated diagnostics suites (including reset/pause/resume, stale/duplicate transfers, character isolation, persistence failures, decimal input and paused UI edits), native test-owned process checks, portable-package smoke tests, mock UI rendering, and real public release updater discovery/download/staging verification. These checks are not live Vanilla/Gepard gameplay or an in-place update on the user's VPS.
