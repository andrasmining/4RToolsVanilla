# 4RTools Vanilla 0.6.61

## Weight/Cart: transient failures no longer stop farming

The live v0.6.59/v0.6.60 Cart testing showed that an individual drag can fail temporarily because of RDP/client lag even though the Inventory and Cart are otherwise healthy. Cart maintenance is now intentionally tolerant of that condition.

### Slow drag timing remains

Every Weight/Cart transfer uses the deliberate Cart drag path:

- 250 ms source hold before mouse-down;
- 12 deterministic movement steps;
- 100 ms per movement step;
- 300 ms hold over the detected Cart body before release;
- 500 ms post-release wait;
- 700 ms additional transfer settle.

The quantity-dialog observation window is now 3 seconds and Cart-weight progress may take up to 4 seconds before an attempt is considered unsuccessful.

### Three attempts, then resume and retry later

For one detected first-slot item, 4RTools makes up to three slow drag attempts. Between retries it:

- waits 1 second;
- checks verified Cart weight both before and after that pause;
- suppresses the retry immediately if delayed Cart-weight progress proves the previous drag actually succeeded;
- re-detects the compacted first inventory slot before another drag;
- rotates through another safe point inside the positively detected Cart body.

If all three drag attempts still produce no verified Cart-weight increase, this is **not** a manual-hold condition anymore. 4RTools:

1. stops sending Cart input for that pass;
2. closes Cart/Inventory;
3. resumes Autobattle through the existing verified ResumeHotkey/X-Y movement routine;
4. minimizes the client;
5. re-arms Cart maintenance for another automatic attempt after about 60 seconds.

This applies to ordinary transfer non-progress. Truly unsafe states still fail closed: lost client/input ownership, stale/unverified Cart weight, an unresolved modal state, incoherent precision-weight deltas, cancellation/identity replacement, or another condition where a safe resume cannot be established.

## Cart completion threshold: 99% + 50% carried

Farming completion no longer requires an exact 10000/10000 Cart.

- **DONE condition:** verified Cart weight >= **99%** and verified carried weight >= **50%**.
- When both are reached, 4RTools sends the dedicated Weight Autobattle STOP hotkey and keeps that character on the intentional completed-farming hold so recovery cannot restart Autobattle.
- The DONE e-mail uses the actual observed Cart percentage/value.
- Exact **100% Cart** remains the separate Cart-full e-mail milestone.

The completion check runs both in the continuous Weight monitor and immediately after a Cart-maintenance pass, so a client that already satisfies 99% + 50% does not unnecessarily resume farming or start another Cart transfer.

## Capacity-aware final filling remains enabled

The existing verified item-weight calculation remains active:

- Mastela Fruit / Use = **3 weight each**
- Peco Feather / Etc = **1 weight each**
- Cart maximum = **10000**

Starting at 95% Cart usage, 4RTools calculates the remaining capacity and uses the positively recognized quantity dialog to request at most the count that can fit. For example, a 2-weight remainder cannot accept another Mastela but can accept two Peco Feathers. Every accepted precision transfer must still produce a coherent verified Cart-weight delta and must never exceed 10000.

Because the new DONE threshold is 99%, 4RTools may stop before exact 10000 when carried weight is already >=50%; otherwise it continues using the precision rules toward the fullest safe Cart.

## Validation

Regression coverage locks:

- at least three slow transfer attempts;
- 3-second quantity-dialog observation;
- 4-second Cart-progress observation;
- 1-second inter-attempt pause;
- delayed-progress checks before a duplicate drag;
- 60-second transient Cart retry scheduling;
- farming completion at Cart >=99% plus carried >=50%;
- existing Mastela/Peco capacity arithmetic and 10000 Cart ceiling;
- existing per-start debug-log isolation and 10 MiB hard cap.

The full Windows validation/release pipeline also runs shipped build-profile checks, Debug and Release tests, portable-package smoke tests, native recovery checks, and mock-data UI validation.

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP timing. The next VPS run remains the live validation boundary for the slow three-attempt feather transfer and one-minute automatic retry behavior.
