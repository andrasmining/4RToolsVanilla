# 4RTools Vanilla 0.6.63

## Weight/Cart: Autobattle STOP is now verified before any Cart UI input

The live Cart test showed that sending the dedicated Weight Autobattle STOP hotkey was not sufficient: the character could still be moving while 4RTools proceeded to Inventory/Cart interaction.

That is no longer allowed.

Before Weight/Cart may open Inventory or Cart, 4RTools now positively verifies the STOP from fresh read-only X/Y:

- each STOP attempt has a **10-second observation window**;
- after sending the dedicated Weight STOP hotkey, X/Y must remain unchanged continuously for at least **5 seconds**;
- any X/Y movement resets that 5-second stillness window;
- if no continuous 5-second stationary window is achieved inside the 10-second attempt, the STOP hotkey is retried;
- STOP is retried at most **3 times**, on the 10-second attempt cadence;
- Inventory and Cart are never opened until stationary STOP verification succeeds.

If all three STOP attempts still show movement/unverified stop state, the Cart pass is deferred safely. 4RTools sends no Inventory/Cart input for that pass and schedules a later Cart retry instead of interacting with a moving character.

The same STOP verifier is also used for the final farming-complete stop. The completed-farming hold is not armed until Autobattle STOP has been verified stationary.

## Why the stationary check is strict

The verifier uses the same fresh, read-only identity/X/Y discipline as Autobattle resume verification:

- PID/session/character/map identity must remain coherent;
- X/Y must be fresh and verified;
- stale/cached/unknown observations cannot become "stationary";
- a 5-second timeout alone is not enough — a fresh sample at the end of the stillness window is required;
- cancellation/settings/client replacement stops the verifier immediately.

This keeps Cart UI input out of a client that is still being driven by Autobattle.

## Existing Cart policies retained

v0.6.63 keeps all current Cart behavior:

- capacity-aware precision filling starts at **75% Cart usage**;
- Mastela Fruit = **3 weight**;
- Peco Feather = **1 weight**;
- Cart capacity = **10000**;
- quantity entry is bounded by remaining Cart capacity;
- slow drag-and-drop uses up to 3 attempts;
- pure transfer non-progress resumes Autobattle and retries Cart maintenance after about 60 seconds;
- farming completes at **Cart >=99% + carried weight >=50%**;
- exact Cart 100% remains the separate Cart-full e-mail milestone;
- per-process timestamped debug logs and 10 MiB per-file hard rotation remain enabled.

## Regression coverage

New deterministic tests verify:

- a stationary client is not accepted until a full 5 seconds of unchanged X/Y has elapsed;
- movement resets the continuous stillness window;
- a moving client gets STOP retries at 0s, 10s and 20s;
- three continuously moving 10-second attempts end without authorizing Cart/Inventory input;
- STOP verification remains bounded to 3 attempts / 10-second windows / 5-second required stillness.

The full Windows pipeline also passed shipped build-profile validation, Debug and Release regression suites, portable package/smoke validation, native recovery checks and mock-data UI/layout validation before this release was versioned.

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP session. The next VPS Weight/Cart pass is the live validation boundary for STOP-stationarity verification.
