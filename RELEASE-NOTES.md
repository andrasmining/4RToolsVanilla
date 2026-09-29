# 4RTools Vanilla 0.6.87

## Farming value monitor

- Add a generic per-character farming calculator to the Weight tab. Each character can keep its own item rows with editable item name, zeny per item, count, automatic Cart source and unit weight.
- Show live active elapsed time, total farmed zeny and zeny/hour so two farming characters or maps can be compared directly over the same run.
- RESET clears counts, elapsed time and unassigned Cart weight while preserving item definitions, then immediately starts a fresh run. PAUSE freezes time and automatic counting so prices/counts/mappings can be edited or manual loot can be added before resuming.
- Feed automatic counts only from Cart transfers that are already proven by a verified read-only Cart-weight increase. Use/Equip/Etc mappings convert that verified weight delta to quantity only when the configured unit weight divides exactly; an optional Any row can serve as a fallback.
- Never infer an item identity from an inventory category. Unmapped or non-divisible transfer weight remains visibly Unassigned so the user can correct the run manually instead of receiving a guessed count.
- Persist farming-monitor definitions, counts, run state and elapsed-time checkpoints in the Vanilla app-data folder.

## Validation

Windows release gates cover farming-monitor reset/pause/resume behavior, exact Cart-delta quantity conversion, ambiguous/unassigned handling, manual value calculations and persistence alongside the existing full diagnostics suite. The release pipeline also runs Debug/Release builds, portable-package smoke tests, native test-owned process checks, isolated mock UI rendering and public updater verification. These checks do not operate a live Vanilla/Gepard client.
