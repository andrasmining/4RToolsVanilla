# 4RTools Vanilla 0.6.60

## Weight/Cart: slower drag-and-drop with three bounded retries

The live v0.6.59 Cart test showed a different failure from the previous populated-Cart issue: the Use transfers completed, but the Etc/Peco Feather drag produced no quantity dialog and no verified Cart-weight increase. The automation treated that first unsuccessful drag as final and immediately put the character on a manual Cart hold.

This release makes individual Cart transfers deliberately slower and tolerant of transient RDP/client lag.

### Deliberate drag pacing

Weight/Cart no longer uses the ordinary fast mouse-drag timing. Each Cart drag now:

- holds the cursor on the verified source slot for **250 ms** before mouse-down;
- moves through **12 deterministic intermediate steps**;
- waits **100 ms per movement step**;
- holds over the verified Cart destination for **300 ms** before mouse-up;
- waits **500 ms after release**;
- then gives the client another **700 ms** settle period before quantity/progress handling.

This changes only the Cart-maintenance drag path; unrelated ordinary input keeps its existing timing.

### Three attempts before manual hold

One missing Cart-weight increase is no longer enough to fail the maintenance pass.

For each detected first-slot item, 4RTools now makes up to **3 slow drag attempts**:

1. send the slow drag to a safe point inside the positively detected Cart body;
2. allow up to **2.2 seconds** for a quantity dialog;
3. if a quantity dialog is positively recognized, handle it with the existing safe Enter/precision-fill rules;
4. allow up to **3 seconds** for verified read-only Cart-weight progress;
5. if no progress is seen, wait **800 ms**, re-check Cart weight for delayed evidence, re-detect the first inventory slot, and retry through another deterministic safe Cart point.

Before every retry, fresh Cart weight is checked again. If late read-only evidence shows the previous drag actually succeeded, the retry is suppressed so 4RTools cannot duplicate the transfer.

If the first slot disappears after an earlier attempt while Cart weight still has not increased, 4RTools does not blindly drag whatever compacted item may now occupy that location; it gives Cart memory one final bounded chance to catch up and otherwise fails closed.

Only after all **three** slow attempts fail to produce verified Cart-weight progress does the character enter the existing manual Cart hold.

### Existing safety remains intact

- Cart weight remains read-only and is the authoritative transfer-success signal.
- Inventory identity/count is not read from game memory and game memory is never written.
- Enter is still sent only after a positively recognized quantity dialog.
- Quantity-one transfers may legitimately have no dialog, so no dialog still means no Enter.
- At/above 95%, the existing Mastela Fruit = 3 and Peco Feather = 1 capacity-aware rules remain in force.
- Cart capacity remains fixed/validated at 10000.
- UI ownership, cancellation, stale/unverified state, late modal ambiguity and incoherent precision-weight deltas still fail closed.

## Regression coverage

The diagnostics tests now lock the lag-tolerant policy:

- at least 3 transfer attempts;
- >=700 ms transfer settle;
- >=2.0 s quantity-dialog observation;
- >=3.0 s Cart-progress observation;
- >=700 ms retry pause;
- deliberate drag source/destination holds and multi-step movement cannot regress back to the previous fast timing.

The full Windows pipeline also runs the existing build-profile checks, Debug/Release regression suites, portable package smoke test, native recovery checks, and mock-data UI validation.

## Live-validation boundary

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP timing. The failed v0.6.59 feather transfer is grounded in the supplied live screenshot/log; v0.6.60 specifically changes that transfer path so the next live Weight/Cart run can verify the slower three-attempt behavior.
