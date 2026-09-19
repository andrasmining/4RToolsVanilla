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

Use the existing standard Windows GitHub Actions runners for build, test and rendering validation. Do not introduce paid runners, testing services or extra infrastructure. Before publishing a UI release, run `scripts/test-ui-layout.ps1`, fix its failures and inspect the generated screenshots. A successful build alone is not UI validation.

Preserve the native mock-data harness's checks for full-width embedding, column visibility, pane ratio, toolbar spacing, live-card fields, large profile lists, scrolling, tab selection and resize/text-size transitions. Add a regression assertion when a screenshot reveals a missed defect. Do not silently skip or weaken a failing check just to publish. Keep the release gated on these tests.

Report actual Windows mock-data UI results separately from live gameplay/RDP testing. The harness must keep game observation, input, automatic startup, email and network update services inactive; test data must never be real credentials or user profiles.

The table is a character roster. Keep Enabled, Weight and Smart Teleport as
the first three columns, followed by Smart Teleport seconds and Smart Teleport
hotkey. Then show Description, Username, Slot, Character name and the remaining
resume/password/proxy/runtime fields. Weight is on/off only in this list; its
detailed settings stay on the Weight tab. Multiple characters may share one
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
- Send the dedicated Weight Autobattle STOP hotkey only after the affected client owns the global serialized input lease. Never use the character ResumeHotkey to stop Autobattle. No healthy sibling input is authorized during that lease.
- A stack quantity confirmation may use Enter only after a fresh, positive quantity-dialog recognition. Quantity-one transfers produce no dialog: no dialog means **no Enter**. Ambiguous/late modal evidence fails closed and must never fall through to chat input.
- Verify transfer progress after every drag. A Cart destination may be any point inside the positively detected Cart item body; use a small deterministic rotation of safely inset panel-interior points and do not require a destination slot, empty-slot lattice, or Cart item grid to remain visually detectable once the Cart contains items. There is no arbitrary screen-coordinate fallback. Verified read-only Cart-weight increase is the authoritative transfer-success signal after a drag; carried-weight reduction may corroborate it, while source-slot visual re-detection must not veto a transfer already proven by Cart weight because Vanilla compacts the next stack into the same first slot. If Cart acceptance/progress cannot be established, first-slot state stays ambiguous, UI ownership is lost, or the Cart rejects a transfer, stop further input, leave Autobattle OFF, and place only that character in an explicit manual Cart hold. Automatic recovery must not restart a held character until the user explicitly clears that hold. Unrelated settings changes, STOP/START, relog attempts or app supervision restarts must not silently clear the hold; clearing one Cart hold must not erase unrelated Error states.
- Every major Weight/Cart step (lease, Autobattle stop, panel detection, category selection verification, empty-category advance, transfer/quantity handling, completion/failure) must appear in the visible Recovery log and global debug log. After successful transfers, resume through the same shared verified ResumeHotkey routine used by recovery diagnostics, require fresh X/Y movement, then minimize the owned client. STOP/settings/client/session/character replacement cancel outstanding Cart work.
- The fleet cards show compact HP, SP, carried Weight and Cart Weight values/bars plus Location. Do not show an Activity field until activity semantics are independently verified.
- Cart transfer safety uses the verified Cart weight after every accepted drag. At or above 95% full, unknown-weight item categories must stop without a blind full-stack transfer. Current precision-fill rules are Use/Mastela Fruit = 3 weight per item and Etc/Peco Feather = 1 weight per item; Equip stays unknown. Use the positively recognized quantity dialog to request only a capacity-safe amount, verify the resulting Cart-weight delta is coherent with that unit weight, and never exceed the fixed 10000 capacity.
- Cart 100% is a farming milestone: when valid saved SMTP/addresses are configured, send one Cart-full mail independently of the carried-weight warning checkbox and continue farming on the character. When Cart is still 100% and carried weight reaches at least 50%, send the dedicated Weight Autobattle STOP hotkey, keep that character on an intentional completed-farming hold so recovery does not restart it, and send one DONE mail through the same saved SMTP transport. Clearing Weight/Cart holds explicitly clears this completed-farming hold too.

- Weight actions are a per-character policy as well as a global feature policy. Every saved character row must carry an independent Weight/Cart enable switch; global Weight-tab settings must never authorize e-mail or Cart input for a character whose row switch is off. Disabling the switch must cancel outstanding Weight/Cart ownership safely and must not affect the healthy sibling character.

## Character-bound Smart Teleport

- Integrated Smart Teleport is a per-character policy keyed by the verified username + character-name row. Never require or expose PID/process selection for this feature; resolve the live PID from the existing identity binding.
- Its automatic trigger is fresh verified read-only X/Y only. Default stationary time is 60 seconds and is independently configurable per character. Target/combat/casting state is not required. Any verified movement, map/session replacement, stale/missing/unverified coordinates or observation gap resets/re-baselines the idle timer rather than counting as stationary.
- Capture the teleport hotkey live in the character editor (including modifiers), like the Resume hotkey; do not use a fixed dropdown or hard-code a teleport key.
- Background teleport must not restore/focus a minimized/hidden client as a fallback. Use ordinary Windows input targeted to the verified owned Vanilla HWND and fail closed if that background path or window ownership is unavailable.
- Enter is authorized only after a fresh positive recognition of the expected Select an Area to Warp modal with the first choice highlighted, preferably on two consecutive captures. An already-open dialog before the teleport hotkey receives no input. Missing/ambiguous popup evidence means no Enter, and the popup must clear after Enter.
- Smart Teleport shares the global per-client serialized automation/recovery lease. STOP/settings/PID/session/character replacement cancel outstanding work; contention defers without disturbing the healthy sibling.
