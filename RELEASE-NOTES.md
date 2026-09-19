# 4RTools Vanilla 0.6.62

## Cart precision filling now starts at 75%

The capacity-aware Cart logic previously waited until 95% Cart usage before it switched from ordinary full-stack handling to item-weight-aware quantity calculation. That leaves too little headroom when the character may already be carrying roughly half of its own maximum weight.

This release moves the precision threshold to **75% Cart usage**.

From 75% onward:

- Cart capacity is recalculated before every transfer from the verified read-only Cart current/max weight.
- Mastela Fruit remains **3 weight per item**.
- Peco Feather remains **1 weight per item**.
- If an entire carried stack is provably safe, the existing full-stack confirmation is still allowed.
- Otherwise 4RTools calculates the maximum quantity that can fit without exceeding the verified 10000 Cart capacity and enters that value only after the quantity dialog is positively recognized.
- If the remaining capacity is smaller than one Mastela, Mastela is skipped so Peco Feather can still use the remaining 1- or 2-weight space.
- Unknown-weight categories are not blindly transferred once the Cart has reached the 75% precision threshold.
- Every accepted precision transfer still requires a coherent verified Cart-weight delta.

The 75% boundary is intentionally conservative: it gives the automation enough room to handle a character carrying a large amount of loot before the Cart becomes nearly full.

## Existing resilient transfer policy retained

The v0.6.61 lag-tolerant transfer behavior is unchanged:

- slow deliberate Cart drag;
- up to three attempts per item;
- delayed Cart-weight progress suppresses duplicate retries;
- three pure non-progress attempts resume Autobattle instead of creating a manual hold;
- Cart maintenance retries automatically after about 60 seconds;
- truly unsafe/ambiguous ownership, modal or memory states still fail closed.

## Farming completion retained

The farming-complete condition remains:

- verified Cart weight **>=99%**; and
- verified carried weight **>=50%**.

Exact Cart 100% remains the separate Cart-full e-mail milestone.

## Validation

Regression coverage now locks the precision boundary at exactly 75%:

- 74.999% does not enable precision mode;
- 75% and above do;
- the existing Mastela/Peco capacity arithmetic, 10000 Cart ceiling, three-attempt retry policy, 60-second transient retry, 99%+50% DONE condition, per-start debug files and 10 MiB log cap remain covered.

The full Windows build/release pipeline also validates shipped build profiles, Debug and Release tests, portable-package smoke tests, native recovery checks, and mock-data UI layout.

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP timing. The next VPS Cart cycle is the live validation boundary for the earlier 75% transition.
