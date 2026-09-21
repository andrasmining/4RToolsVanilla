# Vanilla UI policy

Applies to files in `Model/Vanilla/` in addition to the repository-root `AGENTS.md`.

The Vanilla workspace is functionally dense, so keep its visible UI deliberately minimal and responsive.

- Optimize first for ordinary Full-HD / RDP work areas around 1920x1080 and 1980x1020, while retaining graceful scrolling below that size. Do not design around 2K/4K space.
- Prefer adaptive layout based on current client width/height instead of large fixed panels or fixed whitespace.
- Spend vertical space on interactive data and controls, especially the account table and log. Keep the always-visible fleet strip compact without clipping location, activity or resource values.
- Do not show explanatory paragraphs, security implementation notes, or other documentation inline when a tooltip/hover description is sufficient. Normal-state helper prose should be hidden; visible text should be state, data, action, warning, or error information.
- Keep related controls on one compact row at Full-HD when practical and allow wrapping only as a responsive fallback.
- Launcher input should be only as wide as useful; do not let it consume the whole row.
- On normal desktop widths, the Recovery & relog tab should default to approximately two thirds of its width for launcher/actions/accounts and one third for the reconnect log, with a real draggable splitter so the user can enlarge either pane at runtime. Stack the same draggable split horizontally only on genuinely narrow windows or when enlarged text cannot fit both panes readably. Default layout tests should still have no unnecessary account-table horizontal scrollbar; user-driven splitter shrink may intentionally expose one.
- Do not keep a separate large recovery-status panel when the same state can be expressed beside the corresponding account. Put compact PID/state information in the account table and keep verbose detail in hover text/logging.
- The account table is the primary recovery-management surface. Reserve room for at least four account rows plus roughly one empty-row worth of breathing room. Let it consume available vertical space and show all saved profiles that fit; use an internal vertical scrollbar only when the profile list exceeds the available pane.
- All account columns must remain inside the visible table. Measure compact fields and share remaining width among Description, Username, Character name and Status. Do not retain stale fill widths from the larger legacy layout.
- Any number of account profiles may be saved persistently, but at most two profiles may be enabled/actively supervised at once. Preserve the existing two-active-client recovery/runtime limit.
- Account-specific settings belong in the account row/edit dialog. Proxy is account-specific and visible in the table; edit it through the account editor. Do not add a second selected-account proxy control below the table.
- Do not expose redundant one-off actions such as a separate `Run login now` button when the normal supervisor/test flow already owns login/recovery behavior.
- Preserve hover documentation for controls and panels so functionality remains discoverable without making the default workspace visually busy.
- UI-only refactors must reuse existing behavior/event wiring where possible and must not change sequential startup, recovery, memory observation, or automation semantics unless the task explicitly requires that behavior change.

## Required native UI validation

Use local Windows build, test and isolated hidden rendering validation. GitHub Actions is prohibited in this private repository. Do not introduce paid runners, testing services or extra infrastructure. Before publishing a UI release, run `scripts/test-ui-layout.ps1` in its background-safe mode, fix its failures and inspect the generated mock screenshots. Never show test windows, observe live clients or send desktop input during background-only development. A successful build alone is not UI validation.

Preserve the native mock-data harness's checks for full-width embedding, column visibility, pane ratio, toolbar spacing, live-card fields, large profile lists, scrolling, tab selection and resize/text-size transitions. Add a regression assertion when a screenshot reveals a missed defect. Do not silently skip or weaken a failing check just to publish. Keep the release gated on these tests.

Report actual Windows mock-data UI results separately from live gameplay/RDP testing. The harness must keep game observation, input, automatic startup, email and network update services inactive; test data must never be real credentials or user profiles.

The table is a character roster. Keep Enabled, Cart, Mail and Smart Teleport as
the first policy columns, followed by Smart Teleport seconds and Smart Teleport
hotkey. Then show Description, Username, Slot, Character name and the remaining
resume/password/proxy/runtime fields. Cart and Mail are independent per-character
switches; shared thresholds/hotkeys/categories/SMTP settings stay on the Weight tab. Multiple characters may share one
username; at most two enabled rows. Unknown slots remain blank, not slot 1.
Auto-discovered rows
stay disabled. Native UI checks must cover discovery and the character editor.

## User-presence-aware minimization and launcher pacing

Automatic minimization normally respects active human use. For an adopted/already-running
client or ordinary steady-state visibility, require at least 60 seconds continuously visible
AND at least 60 seconds without machine cursor movement. Any cursor movement restarts the idle
grace; unavailable cursor state fails closed and defers minimization. Manual explicit minimize
remains immediate. The exception is a client that 4RTools itself has just launched/relogged and
whose configured Autobattle hotkey has been verified by fresh X/Y movement: minimize that owned
client immediately after movement proof and release the serialized recovery/startup gate without
a 60-second wait. If the user needs to interact with a client during supervised recovery, pause
or stop supervision before doing so.

Launcher GAME START automation must never double-activate a control in one attempt.
Wait for the launcher window to remain present for at least 2.5 seconds, then use
one semantic native-control invocation when available; otherwise require two stable
visual detections before one click. Never use blind/fallback GAME START coordinates.
Wait at least 15 seconds after an actual activation before another attempt unless a
new Vanilla process appears first.

Post-login Autobattle/slave activation is memory-state-first. Fresh verified login
username + character identity + X/Y/map/living HP may establish readiness even when
the visual classifier returns Unknown. Known login/modal/logout/disconnect states
still block input. After the 10-second settle, recovery is at most three cycles of
Autobattle hotkey -> up to 10 seconds fresh X/Y observation -> verified Smart Teleport
only if still stationary -> up to 10 seconds fresh X/Y observation. Any verified
movement stops the current wait and suppresses all later input immediately. After
three stationary cycles, observe passively until the 180-second recovery deadline
before restart escalation. Every readiness transition, settle, hotkey, teleport
decision/result and movement window must also be written to the global debug log,
not only the reconnect session log.

## Existing-client startup evidence

- For an already-running, identity-matched Vanilla client, fresh verified read-only gameplay memory is the primary startup evidence: expected username + character, X/Y, map and living HP.
- Visual recognition is supplemental for existing-client adoption. `Unknown` must not fail adoption, trigger hotkey input, or repeatedly restore/focus a client solely to prove gameplay. Explicit login/logout/disconnected evidence may still fail closed.
- `COPY DEBUG LOG` must retain enough host/session/display/top-level-window telemetry to diagnose machine-specific visual/focus differences without requiring the user to reconstruct the environment manually.

## Steady-state stall escalation

- Smart Teleport is the first-line response to ordinary stationary gameplay. The per-character default is 60 seconds and may be configured independently.
- Normal Online supervision must not send Autobattle wakeup hotkeys. Fresh verified X/Y is the only steady-state health signal.
- Persist a global Recovery setting for the no-movement restart threshold. Default to 180 seconds and allow 60-3600 seconds. When reached without verified movement, restart only that client; do not disturb a healthy sibling.
- A Smart Teleport attempt must not reset/postpone the longer restart deadline merely because it briefly owns the serialized background-input lease. Verified movement or a true context/recovery reset may re-baseline it.
- Confirmed `Now Logging Out.` / `Disconnected from Server.` dialogs require two fresh matching captures and may recover immediately without waiting for the stall threshold. A stable return to the login/service shell after confirmed gameplay is also a recovery event.
- Failed recovery retries indefinitely with exponential backoff capped at one hour. There is no finite three-client-restart terminal budget.
- TESTS exposes safe manual Smart Teleport and Weight/Cart actions for the selected character. They use the production identity/lease/vision/input paths and bypass only the automatic trigger condition.
- Global debug logging records structured start/result events for every automatic/manual Smart Teleport and Cart action; COPY DEBUG LOG includes a last-24-hours action summary.

## Launcher and helper-window ownership

- Never treat `GDI+ Hook Window Class` / `GDI+ Window (...)`, IME helpers, Gepard splash windows, or other tiny bootstrap/helper surfaces as an interactive Vanilla game window. In particular, never call ShowWindow/restore/focus on the 1x1 GDI+ helper just because its title contains `Vanilla MMO`.
- Launcher GAME START must be bound to a verified launcher HWND/PID. Foreground acquisition must be ownership-checked, bounded, and fail closed; never use taskbar/desktop coordinates to work around Windows foreground lock.
- When GAME START has been detected twice at a stable launcher-client position, prefer one direct owned-window client message to that verified launcher HWND over a global cursor/SendInput click. No blind coordinate fallback is authorized.

## Weight / Cart UI automation

- Weight-triggered Cart maintenance remains ordinary UI input driven by verified read-only state. Use the shared fleet reader for carried weight, Cart current/max weight and movement; never read inventory item identity/count from memory and never write game memory. For the current verified Vanilla fingerprint, Cart Current/Max are module offsets 0xD34B3C/0xD34B40 as UInt32. Accept Cart maximum only when it equals the fixed capacity 10000 and require 0 <= current <= maximum.
- Autobattle STOP, Inventory and Cart hotkeys and the Use/Equip/Etc category selection are configuration, not hard-coded assumptions. The Weight Autobattle STOP hotkey defaults to Alt+3 and is persisted independently from the character ResumeHotkey. Detect the actual opened panel, slot geometry and four-tab category rail from the current client image. Do not interpret the permanently blue Fav styling as selection; identify the active category structurally from the tab whose right border is open into the Inventory body while inactive tabs retain that border. Category clicks must target visually detected tab bounds and positively verify the resulting selected-tab state; never derive category clicks from fixed/percentage panel coordinates. If a detected category click is not verified, re-detect the rail and retry only through a small bounded deterministic set of safe interior points; do not add stochastic click/timing behavior for anti-detection. Timing waits should poll observed UI state with bounded intervals rather than rely on long blind sleeps. Vanilla compacts category contents to the first slot, so classify only that first slot against a fresh detected empty-slot reference. Require two consecutive Occupied observations before a drag and two consecutive Empty observations before advancing; ambiguous state fails closed.
- Send the dedicated Weight Autobattle STOP hotkey only after the affected client owns the global serialized input lease, then verify the STOP from fresh read-only X/Y before opening Inventory/Cart or declaring farming complete. Require a continuous 5-second unchanged-X/Y window inside each 10-second STOP attempt. Any X/Y movement resets the stillness timer. Retry the dedicated STOP only at the next 10-second attempt boundary, maximum 3 attempts. Keep the first fresh STOP-sequence HP percentage as one cumulative damage baseline across all STOP retries and subsequent Cart work. While Autobattle is intentionally stopped, continuously re-check fresh verified HP during waits and mouse drag/drop; a drop of more than 10 percentage points, or loss of fresh verified HP after STOP, aborts Cart work, resumes Autobattle through the shared verifier, closes panels best-effort, minimizes, and schedules another Cart attempt in about 60 seconds. The same HP guard applies to farming-complete STOP, whose next attempt is also delayed about 60 seconds after danger/failure. No Inventory/Cart input is authorized before STOP verification. If 3 attempts cannot prove stillness without HP danger, leave the panels closed and defer/retry instead of touching a moving client; the final farming-complete STOP likewise must not arm the completed hold until stillness is verified. Never use the character ResumeHotkey to stop Autobattle. No healthy sibling input is authorized during that lease.
- A stack quantity confirmation may use Enter only after a fresh, positive quantity-dialog recognition. Quantity-one transfers produce no dialog: no dialog means **no Enter**. Ambiguous/late modal evidence fails closed and must never fall through to chat input.
- Verify transfer progress after every drag. A Cart destination may be any point inside the positively detected Cart item body; use a small deterministic rotation of safely inset panel-interior points and do not require a destination slot, empty-slot lattice, or Cart item grid to remain visually detectable once the Cart contains items. There is no arbitrary screen-coordinate fallback. Cart drag-and-drop keeps deliberate source/drop holds but uses fast cursor travel: approximately 250 ms source hold, 6 deterministic movement steps at ~35 ms each, 300 ms destination hold and 500 ms post-release settle. The problem being hardened is reliable grab/drop, not slow travel across the screen. Verified read-only Cart-weight increase is the authoritative transfer-success signal after a drag; carried-weight reduction may corroborate it, while source-slot visual re-detection must not veto a transfer already proven by Cart weight because Vanilla compacts the next stack into the same first slot. A missing Cart-weight increase after one drag is not yet a failure: wait for delayed evidence, re-check Cart weight before every retry, re-detect the first source slot, and retry up to three slow drag attempts through different safe Cart interior points. Never repeat a drag after late Cart-weight evidence proves the preceding attempt succeeded. If all three attempts fail to produce verified Cart progress, treat the result as transient lag: close Inventory/Cart, resume Autobattle through the verified movement routine, minimize, and schedule another Cart-maintenance attempt after about 60 seconds. Do not place the character on manual hold for transfer non-progress alone. If UI ownership is lost, a late quantity modal remains, or another genuinely ambiguous state prevents a safe retry/resume, fail closed earlier rather than blindly dragging a different compacted item. Automatic recovery must not restart a held character until the user explicitly clears that hold. Unrelated settings changes, STOP/START, relog attempts or app supervision restarts must not silently clear the hold; clearing one Cart hold must not erase unrelated Error states.
- Every major Weight/Cart step (lease, Autobattle stop, panel detection, category selection verification, empty-category advance, transfer/quantity handling, completion/failure) must appear in the visible Recovery log and global debug log. After successful transfers, resume through the same shared verified ResumeHotkey routine used by recovery diagnostics, require fresh X/Y movement, then minimize the owned client. STOP/settings/client/session/character replacement cancel outstanding Cart work.
- The fleet cards show compact HP, SP, carried Weight and Cart Weight values/bars plus Location. Do not show an Activity field until activity semantics are independently verified.
- Keep Weight/Recovery UI explanations compact. Persistent multi-line instructional prose should be moved to hover tooltips or a small info glyph; keep always-visible text for controls, live state, active warnings/errors and short status only.
- Cart transfer safety uses the verified Cart weight after every accepted drag. At or above 75% Cart usage, unknown-weight item categories must stop without a blind full-stack transfer. Current precision-fill rules are Use/Mastela Fruit = 3 weight per item and Etc/Peco Feather = 1 weight per item; Equip stays unknown. Use the positively recognized quantity dialog to request only a capacity-safe amount, verify the resulting Cart-weight delta is coherent with that unit weight, and never exceed the fixed 10000 capacity.
- Cart 100% alone is NOT an e-mail event; Mail in Cart mode requires both capacity limits and verified completion STOP, as well as the character Mail switch and shared e-mail master/SMTP transport. Farming-complete STOP is intentionally looser: when verified Cart weight is at least 99% and carried weight reaches at least 50%, send the dedicated Weight Autobattle STOP hotkey, keep that character on an intentional completed-farming hold so recovery does not restart it, and send one DONE mail through the same saved SMTP transport. Clearing Weight/Cart holds explicitly clears this completed-farming hold too.

- Cart maintenance and Weight e-mail are separate per-character policies. Every saved character row carries independent Cart and Mail switches. Shared Weight-tab masters/settings never authorize a character whose matching switch is off. Cart OFF + Mail ON means the carried-weight warning threshold applies. Cart ON + Mail ON suppresses both carried-only and Cart-only warnings and sends one combined DONE notification instead. Cart ON + Mail OFF runs maintenance without mail. Cart OFF + Mail OFF runs neither. Legacy rows with no split fields inherit the old WeightEnabled value; explicit split values survive clone/catalog/discovery/identity enrichment unchanged. Disabling Cart must cancel outstanding Cart ownership safely without disabling Mail or disturbing a healthy sibling; disabling Mail suppresses messages without disabling Cart.

## Character-bound Smart Teleport

- Integrated Smart Teleport is a per-character policy keyed by the verified username + character-name row. Never require or expose PID/process selection for this feature; resolve the live PID from the existing identity binding.
- Its automatic trigger is fresh verified read-only X/Y only. Default stationary time is 60 seconds and is independently configurable per character. Target/combat/casting state is not required. Any verified movement, map/session replacement, stale/missing/unverified coordinates or observation gap resets/re-baselines the idle timer rather than counting as stationary.
- Capture the teleport hotkey live in the character editor (including modifiers), like the Resume hotkey; do not use a fixed dropdown or hard-code a teleport key.
- Background teleport must not restore/focus a minimized/hidden client as a fallback. Use ordinary Windows input targeted to the verified owned Vanilla HWND. A minimized Vanilla main window may report a 0x0 current client rectangle; resolve that known game HWND by verified PID/class/title and derive its last normal capture size from WINDOWPLACEMENT + window style without restoring it. PrintWindow output must still pass visual usability/popup verification before any teleport/Enter sequence continues. Fail closed if ownership, sizing, capture, or background input is unavailable.
- Enter is authorized only after a fresh positive recognition of the expected Select an Area to Warp modal with the first choice highlighted, preferably on two consecutive captures. An already-open dialog before the teleport hotkey receives no input. Missing/ambiguous popup evidence means no Enter, and the popup must clear after Enter.
- Smart Teleport shares the global per-client serialized automation/recovery lease. STOP/settings/PID/session/character replacement cancel outstanding work; contention defers without disturbing the healthy sibling.

## Combined-capacity notification completion (2026-09-20)

In active Cart mode, notify only after Cart >=99% AND carried weight >=50% plus a verified completion STOP. Neither capacity alone sends mail. Without active Cart maintenance, Mail uses the configured carried-weight warning threshold. Re-check current shared and per-character Mail settings immediately before dispatch so queued work cannot use an obsolete enabled switch. Reserve a single in-flight DONE send per character. Mail-only profile edits must preserve Cart/recovery input ownership; Cart, identity, enabled-state or input-setting edits retain normal cancellation. Use EffectiveCartMaintenanceEnabled in every Cart guard, never the legacy WeightEnabled field directly.

## Launcher update reset exception (2026-09-20)

Always start through Vanilla Launcher.exe / patcher.exe, never bypass updates with
a direct game launch. A supplied game path may resolve to its adjacent launcher;
missing launcher means configuration failure, not a fallback.

The user authorizes a narrow exception to healthy-sibling isolation: a verified
launcher update progress screen with no GAME START and no progress/status change
for 60 continuous seconds may close same-installation patchers and BOTH game
clients. Missing Start alone, failed/unknown captures and changing progress never
authorize this. Preflight paths plus creation times twice, freshly reconfirm the
screen, retain the global recovery lease, close patchers then games and confirm
every exit plus a final empty process snapshot before restarting the launcher.
Use the existing bounded creation-time-pinned Windows close protocol. Metadata
uses limited query only, with no alternate access after failure.

Allow one reset per launch invocation, with a ten-minute supervisor cooldown.
Recognized changing update progress may extend waiting to a ten-minute hard bound.
STOP, settings/generation, runtime/PID/session replacement cancel pending closes
and delayed restart. Preserve disabled characters and Cart/manual/completion holds.
Restore enabled characters sequentially through login, verified movement and
minimization; cold startup revisits earlier closed rows before supervision starts.
Ordinary recovery still leaves healthy siblings untouched. Log the evidence and
confirmed exits; do not claim a proven file lock from a frozen update screen alone.
